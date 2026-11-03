/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.IO.Compression;
using Launcher.Application.Services;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.Diagnostics;

public sealed class SensitiveLogStreamTests : TestTempDirectory
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task ExportRedactsMultilineCredentialsAndKeepsFollowingDiagnostics(string newline)
    {
        Directory.CreateDirectory(TempRoot);
        var input = Path.Combine(TempRoot, "latest.log");
        var output = Path.Combine(TempRoot, "export.zip");
        await File.WriteAllTextAsync(input, string.Join(newline,
            "password=\"first-line", "synthetic-secret-suffix\"", "status=ok",
            "id_token=synthetic-id-secret", "Authorization=Basic synthetic-basic-secret"));
        var service = new LaunchDiagnosticExportService(NullLogger<LaunchDiagnosticExportService>.Instance);
        var result = await service.ExportAsync(new LaunchDiagnosticExportRequest(
            output, "Instance", "1.21.1", [new(LaunchDiagnosticType.MinecraftLatestLog, input)]));
        Assert.True(result.IsSuccess);
        using var archive = ZipFile.OpenRead(output);
        var entry = Assert.Single(archive.Entries, entry => entry.Name == "latest.log");
        using var reader = new StreamReader(entry.Open());
        var text = await reader.ReadToEndAsync();
        Assert.DoesNotContain("synthetic-", text);
        Assert.Contains("status=ok", text);
        Assert.Contains("password=<redacted>" + newline + "<redacted>" + newline, text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedOutputProtectsObserverTailAndFileWithoutMixingStreams(bool truncate)
    {
        var outputPath = Path.Combine(TempRoot, "captured.log");
        var observed = new List<string>();
        var capture = new LaunchOutputCapture(outputPath, [], maxLineCharacters: truncate ? 32 : 1024,
            lineObserver: line => { lock (observed) observed.Add(line); });
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        var firstLine = truncate ? new string('x', 50) : "password=\"first-line";
        startInfo.ArgumentList.Add("[Console]::Out.WriteLine('" + firstLine + "'); "
            + "[Console]::Error.WriteLine('stderr-ok'); "
            + "[Console]::Out.WriteLine('synthetic-secret-suffix\"'); [Console]::Out.WriteLine('status=ok')");
        using var process = Process.Start(startInfo)!;
        capture.Start(process);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var result = await capture.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(truncate, result.WasTruncated);
        Assert.DoesNotContain("synthetic-", result.StdOut);
        Assert.Contains("stderr-ok", result.StdErr);
        Assert.DoesNotContain(observed, line => line.Contains("synthetic-", StringComparison.Ordinal));
        Assert.DoesNotContain("synthetic-", await File.ReadAllTextAsync(outputPath));
        if (!truncate)
            Assert.Contains("status=ok", result.StdOut);
    }
}
