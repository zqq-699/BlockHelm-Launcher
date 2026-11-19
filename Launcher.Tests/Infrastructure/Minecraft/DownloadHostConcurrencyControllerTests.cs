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

    [Fact]
    public async Task RateLimitReducesOnlyTheAffectedHost()
    {
        var clock = new TestTimeProvider();
        var controller = CreateController(clock);
        await CreateHostAsync(controller, CurseForgeUri);
        await CreateHostAsync(controller, MojangUri);

        var adjustment = controller.RecordResult(
            CurseForgeUri,
            DownloadFailureReason.HttpStatus,
            HttpStatusCode.TooManyRequests);

        Assert.NotNull(adjustment);
        Assert.Equal(32, controller.GetSnapshot(Origin(CurseForgeUri)).CurrentTarget);
        Assert.Equal(64, controller.GetSnapshot(Origin(MojangUri)).CurrentTarget);
    }

    [Fact]
    public async Task CompletedColdStartDoesNotJitterSubsequentRequests()
    {
        var delays = new List<TimeSpan>();
        var controller = new DownloadHostConcurrencyController(
            maximumJitter: TimeSpan.FromSeconds(2),
            nextJitter: () => 0.5,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        await using var first = await controller.AcquireAsync(
            CurseForgeUri,
            AcquireNoopGlobalAsync,
            applyColdStartJitter: true,
            CancellationToken.None);
        await using var initialBurstRequest = await controller.AcquireAsync(
            CurseForgeUri,
            AcquireNoopGlobalAsync,
            applyColdStartJitter: true,
            CancellationToken.None);

        controller.RecordResult(CurseForgeUri, failureReason: null);

        await using var steadyStateRequest = await controller.AcquireAsync(
            CurseForgeUri,
            AcquireNoopGlobalAsync,
            applyColdStartJitter: true,
            CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task CancellationDuringJitterReleasesHostWithoutTakingGlobalSlot()
    {
        var globalAcquireCount = 0;
        var controller = new DownloadHostConcurrencyController(
            maximumJitter: TimeSpan.FromSeconds(2),
            nextJitter: () => 0.5,
            delayAsync: static (_, token) => new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, token)));
        await using var first = await controller.AcquireAsync(
            CurseForgeUri,
            AcquireGlobalAsync,
            applyColdStartJitter: true,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var canceled = controller.AcquireAsync(
            CurseForgeUri,
            AcquireGlobalAsync,
            applyColdStartJitter: true,
            cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);
        Assert.Equal(1, controller.GetSnapshot(Origin(CurseForgeUri)).ActiveCount);
        Assert.Equal(1, globalAcquireCount);

        ValueTask<IImportConcurrencyLease> AcquireGlobalAsync(CancellationToken _)
        {
            Interlocked.Increment(ref globalAcquireCount);
            return AcquireNoopGlobalAsync(CancellationToken.None);
        }
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
        var controller = new DownloadHostConcurrencyController(timeProvider: clock, maximumJitter: TimeSpan.Zero,
            delayAsync: (delay, token) =>
            {
                Assert.Equal(TimeSpan.FromMinutes(2), delay);
                waiting.TrySetResult();
                return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, token));
            });
        controller.DeferRequests(Origin(CurseForgeUri), TimeSpan.FromMinutes(2));
        controller.DeferRequests(Origin(CurseForgeUri), TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var pending = controller.AcquireAsync(CurseForgeUri, AcquireGlobal, false, cancellation.Token).AsTask();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, globalAcquires);
        Assert.Equal(0, controller.GetSnapshot(Origin(CurseForgeUri)).ActiveCount);
        Assert.Null(controller.TryAcquireAvailable(CurseForgeUri, () => new NoopLease()));
        await using (await controller.AcquireAsync(MojangUri, AcquireGlobal, false, CancellationToken.None))
            Assert.Equal(1, globalAcquires);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, controller.GetSnapshot(Origin(CurseForgeUri)).ActiveCount);

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
        var controller = new DownloadHostConcurrencyController(timeProvider: clock, maximumJitter: TimeSpan.Zero,
            delayAsync: (delay, _) => { clock.Advance(delay); return ValueTask.CompletedTask; });
        var pending = controller.AcquireAsync(CurseForgeUri, AcquireGlobal, false, CancellationToken.None).AsTask();
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
        controller = new DownloadHostConcurrencyController(timeProvider: clock, maximumJitter: TimeSpan.Zero,
            delayAsync: (delay, _) =>
            {
                waited += delay;
                clock.Advance(delay);
                controller!.DeferRequests(Origin(CurseForgeUri), TimeSpan.FromSeconds(80));
                return ValueTask.CompletedTask;
            });
        controller.DeferRequests(Origin(CurseForgeUri), TimeSpan.FromSeconds(80));
        var failure = await Assert.ThrowsAsync<DownloadCooldownWaitException>(() => controller.AcquireAsync(
            CurseForgeUri, _ => throw new InvalidOperationException("Cooldown must not occupy a global slot."),
            false, CancellationToken.None).AsTask());
        Assert.Equal(TimeSpan.FromSeconds(80), waited);
        Assert.Equal(TimeSpan.FromSeconds(80), failure.RetryAfter);
        Assert.Equal(DownloadFailureDisposition.SwitchSource, failure.Disposition);
        Assert.Equal(0, controller.GetSnapshot(Origin(CurseForgeUri)).ActiveCount);
    }

    private sealed class CountingLease(Action onDispose) : IImportConcurrencyLease
    {
        public void Dispose() => onDispose();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public void FailedOpportunisticGlobalAdmissionImmediatelyReturnsHostCapacity()
    {
        var controller = CreateController(new TestTimeProvider());

        var rejected = controller.TryAcquireAvailable(CurseForgeUri, () => null);

        Assert.Null(rejected);
        Assert.Equal(0, controller.GetSnapshot(Origin(CurseForgeUri)).ActiveCount);

        using var accepted = controller.TryAcquireAvailable(
            CurseForgeUri,
            () => new NoopLease());
        Assert.NotNull(accepted);
        Assert.Equal(1, controller.GetSnapshot(Origin(CurseForgeUri)).ActiveCount);
        accepted.Dispose();
        Assert.Equal(0, controller.GetSnapshot(Origin(CurseForgeUri)).ActiveCount);
    }

    private static DownloadHostConcurrencyController CreateController(TestTimeProvider clock) => new(
        timeProvider: clock,
        maximumJitter: TimeSpan.Zero,
        nextJitter: () => 0,
        delayAsync: static (_, _) => ValueTask.CompletedTask);

    private static async Task CreateHostAsync(DownloadHostConcurrencyController controller, Uri uri)
    {
        await using var lease = await controller.AcquireAsync(
            uri,
            AcquireNoopGlobalAsync,
            applyColdStartJitter: false,
            CancellationToken.None);
    }

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
