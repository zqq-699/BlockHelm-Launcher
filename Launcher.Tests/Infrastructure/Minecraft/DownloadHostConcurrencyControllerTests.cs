using System.Net;
using System.Net.Http;
using Launcher.Application.Services;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class DownloadHostConcurrencyControllerTests
{
    private static readonly Uri CurseForgeUri = new("https://edge.forgecdn.net/file.jar");
    private static readonly Uri MojangUri = new("https://piston-data.mojang.com/file.jar");

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public async Task SingleHostCanUseEveryConfiguredGlobalSlot(int maximum)
    {
        var limiter = new ImportConcurrencyLimiter();
        limiter.SetMaximumDownloadConcurrency(maximum);
        var controller = new DownloadHostConcurrencyController();
        var leases = new List<DownloadHostConcurrencyController.DownloadAdmissionLease>();
        using var cancellation = new CancellationTokenSource();
        try
        {
            for (var index = 0; index < maximum; index++)
                leases.Add(await controller.AcquireAsync(CurseForgeUri, limiter.AcquireRuntimeDownloadSlotAsync,
                    cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(maximum, limiter.DownloadSnapshot.ActiveCount);
            var queued = controller.AcquireAsync(MojangUri, limiter.AcquireRuntimeDownloadSlotAsync,
                cancellation.Token).AsTask();
            Assert.False(queued.IsCompleted);
            Assert.Equal(1, limiter.DownloadSnapshot.WaitingCount);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
                lease.Dispose();
            }
        }
        Assert.Equal(0, limiter.DownloadSnapshot.ActiveCount);
        Assert.Equal(0, limiter.DownloadSnapshot.WaitingCount);
    }

    [Theory]
    [InlineData(0, 300, 1200)]
    [InlineData(1, 600, 1400)]
    public void RetryDelayIsShortAndBoundedButPreservesRetryAfter(double jitter, int firstDelayMs, int thirdDelayMs)
    {
        var options = DownloadRetryOptions.Default;
        using var httpClient = new HttpClient(new SuccessHandler());
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            retryOptions: options,
            nextRetryJitter: () => jitter);
        var transient = new DownloadAttemptException(
            DownloadFailureDisposition.RetryCurrentSource,
            DownloadFailureReason.Network,
            "transient");
        var rateLimited = new DownloadAttemptException(
            DownloadFailureDisposition.RetryCurrentSource,
            DownloadFailureReason.HttpStatus,
            "rate limited",
            statusCode: HttpStatusCode.TooManyRequests,
            retryAfter: TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromMilliseconds(firstDelayMs), executor.GetRetryDelay(transient, attempt: 1));
        Assert.Equal(TimeSpan.FromMilliseconds(thirdDelayMs), executor.GetRetryDelay(transient, attempt: 3));
        Assert.Equal(TimeSpan.FromMilliseconds(1400), executor.GetRetryDelay(transient, attempt: 10));
        Assert.Equal(TimeSpan.FromSeconds(7), executor.GetRetryDelay(rateLimited, attempt: 1));
        var longRateLimit = new DownloadAttemptException(DownloadFailureDisposition.RetryCurrentSource,
            DownloadFailureReason.HttpStatus, "rate limited", statusCode: HttpStatusCode.TooManyRequests,
            retryAfter: TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(2), executor.GetRetryDelay(longRateLimit, attempt: 1));
    }

    [Fact]
    public async Task CooldownBlocksOnlyAffectedHostWithoutHoldingSlotsAndCanBeCanceled()
    {
        var clock = new TestTimeProvider();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var globalAcquires = 0;
        var controller = new DownloadHostConcurrencyController(timeProvider: clock, delayAsync: (delay, token) =>
            {
                Assert.Equal(TimeSpan.FromMinutes(2), delay);
                waiting.TrySetResult();
                return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, token));
            });
        controller.DeferRequests(Origin(CurseForgeUri), TimeSpan.FromMinutes(2));
        controller.DeferRequests(Origin(CurseForgeUri), TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var pending = controller.AcquireAsync(CurseForgeUri, AcquireGlobal, cancellation.Token).AsTask();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, globalAcquires);
        Assert.Null(controller.TryAcquireAvailable(CurseForgeUri, () => new NoopLease()));
        await using (await controller.AcquireAsync(MojangUri, AcquireGlobal, CancellationToken.None))
            Assert.Equal(1, globalAcquires);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        ValueTask<IImportConcurrencyLease> AcquireGlobal(CancellationToken token)
        {
            Interlocked.Increment(ref globalAcquires);
            return AcquireNoopGlobalAsync(token);
        }
    }

    [Fact]
    public async Task CooldownAppliedWhileGlobalAdmissionIsQueuedReleasesAndReacquiresSlots()
    {
        var clock = new TestTimeProvider();
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquired = 0;
        var disposed = 0;
        var controller = new DownloadHostConcurrencyController(timeProvider: clock, delayAsync: (delay, _) => { clock.Advance(delay); return ValueTask.CompletedTask; });
        var pending = controller.AcquireAsync(CurseForgeUri, AcquireGlobal, CancellationToken.None).AsTask();
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        controller.DeferRequests(Origin(CurseForgeUri), TimeSpan.FromMinutes(2));
        release.TrySetResult();
        await using (await pending.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            Assert.Equal(2, acquired);
            Assert.Equal(1, disposed);
            Assert.Equal(TimeSpan.Zero, controller.GetRemainingCooldown(CurseForgeUri));
        }
        Assert.Equal(2, disposed);

        async ValueTask<IImportConcurrencyLease> AcquireGlobal(CancellationToken token)
        {
            if (++acquired == 1) { queued.TrySetResult(); await release.Task.WaitAsync(token); }
            return new CountingLease(() => disposed++);
        }
    }

    [Fact]
    public async Task ExtendingCooldownCannotRestartTheWaitBudget()
    {
        var clock = new TestTimeProvider();
        var waited = TimeSpan.Zero;
        DownloadHostConcurrencyController? controller = null;
        controller = new DownloadHostConcurrencyController(timeProvider: clock, delayAsync: (delay, _) =>
            {
                waited += delay;
                clock.Advance(delay);
                controller!.DeferRequests(Origin(CurseForgeUri), TimeSpan.FromSeconds(80));
                return ValueTask.CompletedTask;
            });
        controller.DeferRequests(Origin(CurseForgeUri), TimeSpan.FromSeconds(80));
        var failure = await Assert.ThrowsAsync<DownloadCooldownWaitException>(() => controller.AcquireAsync(
            CurseForgeUri, _ => throw new InvalidOperationException("Cooldown must not occupy a global slot."),
            CancellationToken.None).AsTask());
        Assert.Equal(TimeSpan.FromSeconds(80), waited);
        Assert.Equal(TimeSpan.FromSeconds(80), failure.RetryAfter);
        Assert.Equal(DownloadFailureDisposition.SwitchSource, failure.Disposition);
    }

    private sealed class CountingLease(Action onDispose) : IImportConcurrencyLease
    {
        public void Dispose() => onDispose();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public void OpportunisticAdmissionUsesOnlyGlobalCapacity()
    {
        var controller = CreateController(new TestTimeProvider());
        var limiter = new ImportConcurrencyLimiter();
        limiter.SetMaximumDownloadConcurrency(1);
        using var accepted = controller.TryAcquireAvailable(CurseForgeUri, AcquireGlobal);
        Assert.NotNull(accepted);
        Assert.Null(controller.TryAcquireAvailable(MojangUri, AcquireGlobal));
        Assert.Equal(1, limiter.DownloadSnapshot.ActiveCount);
        accepted.Dispose();
        accepted.Dispose();
        using var replacement = controller.TryAcquireAvailable(MojangUri, AcquireGlobal);
        Assert.NotNull(replacement);

        IImportConcurrencyLease? AcquireGlobal() =>
            limiter.TryAcquireAvailableDownloadSlot(out var lease) ? lease : null;
    }

    private static DownloadHostConcurrencyController CreateController(TestTimeProvider clock) => new(
        timeProvider: clock,
        delayAsync: static (_, _) => ValueTask.CompletedTask);

    private static string Origin(Uri uri) => DownloadHostConcurrencyController.NormalizeOrigin(uri);

    private static ValueTask<IImportConcurrencyLease> AcquireNoopGlobalAsync(CancellationToken _) =>
        ValueTask.FromResult<IImportConcurrencyLease>(new NoopLease());

    private sealed class NoopLease : IImportConcurrencyLease
    {
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => utcNow.UtcTicks;
        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class SuccessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent("ok"u8.ToArray())
            });
    }
}
