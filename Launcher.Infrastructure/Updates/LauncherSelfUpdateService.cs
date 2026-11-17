using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Launcher.Application;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Updates;

public sealed class LauncherSelfUpdateService : ILauncherSelfUpdateService
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);
    private readonly HttpClient httpClient;
    private readonly ILogger<LauncherSelfUpdateService>? logger;
    private readonly string baseDirectory;
    private readonly string? currentExecutablePath;
    private readonly int currentProcessId;
    private readonly Func<ProcessStartInfo, bool> startProcess;
    private readonly LauncherUpdateCacheCleaner cacheCleaner;

    public LauncherSelfUpdateService(
        HttpClient? httpClient = null,
        ILogger<LauncherSelfUpdateService>? logger = null,
        LauncherUpdateCacheCleaner? cacheCleaner = null)
        : this(
            httpClient,
            logger,
            AppContext.BaseDirectory,
            Environment.ProcessPath,
            Environment.ProcessId,
            StartProcess,
            cacheCleaner)
    {
    }

    public LauncherSelfUpdateService(
        HttpClient? httpClient,
        ILogger<LauncherSelfUpdateService>? logger,
        string baseDirectory,
        string? currentExecutablePath,
        int currentProcessId,
        Func<ProcessStartInfo, bool> startProcess,
        LauncherUpdateCacheCleaner? cacheCleaner = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? OfficialUpdateHttp.CreateClient(DefaultRequestTimeout));
        this.logger = logger;
        this.baseDirectory = baseDirectory;
        this.currentExecutablePath = currentExecutablePath;
        this.currentProcessId = currentProcessId;
        this.startProcess = startProcess;
        this.cacheCleaner = cacheCleaner ?? new LauncherUpdateCacheCleaner(baseDirectory);
    }

    public async Task<LauncherSelfUpdateStartResult> StartUpdateAsync(
        LauncherUpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        using var diagnosticOperation = DownloadDiagnostics.BeginOperation(logger, "LauncherUpdate");
        if (!update.CanAutoInstall)
            return LauncherSelfUpdateStartResult.Failed("The update asset metadata is incomplete.");
        if (string.IsNullOrWhiteSpace(currentExecutablePath))
            return LauncherSelfUpdateStartResult.Failed("Current launcher executable path is unavailable.");

        string? downloadPath = null;
        try
        {
            cacheCleaner.CleanupStaleCache(currentExecutablePath);
            downloadPath = await DownloadUpdateExecutableAsync(
                update, update.EffectiveDownloadUrls, cancellationToken).ConfigureAwait(false);
            var logDirectory = Path.Combine(baseDirectory, LauncherApplicationIdentity.StorageDirectoryName, "log");
            Directory.CreateDirectory(logDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = downloadPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (DownloadDiagnostics.Capture().TryGetValue("OperationId", out var operationId) && operationId is string id)
                startInfo.Environment["BLOCKHELM_UPDATE_OPERATION_ID"] = id;
            startInfo.ArgumentList.Add("--apply-update");
            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add(currentProcessId.ToString());
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(downloadPath);
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(currentExecutablePath);
            startInfo.ArgumentList.Add("--log-dir");
            startInfo.ArgumentList.Add(logDirectory);
            startInfo.ArgumentList.Add("--restart");

            if (!startProcess(startInfo))
            {
                cacheCleaner.CleanupCachedUpdater(downloadPath);
                return LauncherSelfUpdateStartResult.Failed("Failed to start update apply mode.");
            }
            logger?.LogInformation(
                "Launcher update apply mode started. Version={Version}",
                update.Version);
            return LauncherSelfUpdateStartResult.Success(downloadPath);
        }
        catch (OperationCanceledException)
        {
            if (downloadPath is not null)
                cacheCleaner.CleanupCachedUpdater(downloadPath);
            throw;
        }
        catch (Exception exception)
        {
            if (downloadPath is not null)
                cacheCleaner.CleanupCachedUpdater(downloadPath);
            logger?.LogWarning(exception, "Launcher self update verification or startup failed. ErrorType={ErrorType} HResult={HResult}", exception.GetType().Name, exception.HResult);
            return LauncherSelfUpdateStartResult.Failed("The launcher update could not be verified or started.");
        }
    }

    private async Task<string> DownloadUpdateExecutableAsync(
        LauncherUpdateInfo update,
        IReadOnlyList<LauncherUpdateDownloadUrl> downloadUrls,
        CancellationToken cancellationToken)
    {
        Exception? lastUnavailableException = null;
        foreach (var source in downloadUrls.OrderBy(url => url.Priority))
        {
            if (!TryValidateDownloadUrl(source.Url, update.DownloadFileName, out var uri, out var fileName))
                throw SafeExceptionLog.Describe(new UpdateSecurityException("The update contains an invalid download URL."), "ComponentValidation", ("Reason", "The update contains an invalid download URL."));
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DefaultRequestTimeout);
                return await DownloadFromSourceAsync(update, fileName, uri, source.Name, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastUnavailableException = new UpdateSourceUnavailableException(
                    "The update executable source timed out.", exception);
                logger?.LogWarning(
                    exception,
                    "Launcher update executable source timed out. Source={Source} Host={Host} ErrorType={ErrorType}",
                    source.Name, uri.Host, exception.GetType().Name);
            }
            catch (UpdateSourceUnavailableException exception)
            {
                lastUnavailableException = exception;
                logger?.LogWarning(
                    exception,
                    "Launcher update executable source unavailable. Source={Source} Host={Host} ErrorType={ErrorType}",
                    source.Name, uri.Host, exception.GetType().Name);
            }
        }
        throw new UpdateSourceUnavailableException("All official update executable sources were unavailable.", lastUnavailableException);
    }

    private async Task<string> DownloadFromSourceAsync(
        LauncherUpdateInfo update,
        string fileName,
        Uri uri,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var updateDirectory = Path.Combine(
            baseDirectory, LauncherApplicationIdentity.StorageDirectoryName, "cache", "updates", SanitizePathSegment(update.Version));
        Directory.CreateDirectory(updateDirectory);
        var downloadPath = Path.Combine(updateDirectory, fileName);
        var temporaryPath = Path.Combine(updateDirectory, $".{fileName}.{Guid.NewGuid():N}.download");
        try
        {
            using var response = await OfficialUpdateHttp.SendAsync(
                httpClient, uri, OfficialUpdateUriKind.Executable, cancellationToken, logger).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != update.SizeBytes)
                throw SafeExceptionLog.Describe(new UpdateSecurityException("The update executable Content-Length does not match the manifest."), "UpdateContentLengthMismatch", ("ExpectedSize", update.SizeBytes), ("ActualSize", contentLength));

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long totalBytes = 0;
            using var heartbeat = new Timer(_ => logger?.LogInformation("Launcher update download still running. Source={Source} DownloadedBytes={DownloadedBytes} ExpectedBytes={ExpectedBytes}", sourceName, Interlocked.Read(ref totalBytes), update.SizeBytes), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            while (true)
            {
                int read;
                try
                {
                    read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    throw new UpdateSourceUnavailableException("The update executable response could not be read.", exception);
                }
                if (read == 0) break;
                totalBytes += read;
            if (totalBytes > update.SizeBytes)
                    throw SafeExceptionLog.Describe(new UpdateSecurityException("The update executable exceeded the declared size."), "ComponentValidation", ("Reason", "The update executable exceeded the declared size."));
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (totalBytes != update.SizeBytes)
                throw SafeExceptionLog.Describe(new UpdateSecurityException("The update executable size does not match the manifest."), "UpdateSizeMismatch", ("ExpectedSize", update.SizeBytes), ("ActualSize", totalBytes));
            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw SafeExceptionLog.Describe(new UpdateSecurityException("The update executable SHA-256 does not match the manifest."), "UpdateHashMismatch", ("ExpectedHash", update.Sha256), ("ActualHash", actualHash), ("HashAlgorithm", "SHA256"));

            await output.DisposeAsync().ConfigureAwait(false);
            File.Move(temporaryPath, downloadPath, overwrite: true);
            logger?.LogInformation(
                "Launcher update executable downloaded and verified. Version={Version} Source={Source} SizeBytes={SizeBytes}",
                update.Version, sourceName, totalBytes);
            return downloadPath;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static bool TryValidateDownloadUrl(
        string? downloadUrl,
        string? downloadFileName,
        out Uri downloadUri,
        out string fileName)
    {
        downloadUri = default!;
        fileName = string.Empty;
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var parsedUri))
            return false;
        try
        {
            OfficialUpdateHttp.ValidateInitialUri(parsedUri, OfficialUpdateUriKind.Executable);
        }
        catch (UpdateSecurityException)
        {
            return false;
        }
        var candidate = downloadFileName;
        if (string.IsNullOrWhiteSpace(candidate)) candidate = Path.GetFileName(parsedUri.LocalPath);
        if (string.IsNullOrWhiteSpace(candidate) || !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(Path.GetFileName(candidate), candidate, StringComparison.Ordinal))
            return false;
        downloadUri = parsedUri;
        fileName = candidate;
        return true;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool StartProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    }
}
