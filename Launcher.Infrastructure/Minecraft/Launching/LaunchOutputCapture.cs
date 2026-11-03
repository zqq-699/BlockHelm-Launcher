/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

internal sealed record LaunchCapturedOutput(
    string StdOut,
    string StdErr,
    string? FilePath,
    bool WasTruncated);

internal sealed class LaunchOutputCapture
{
    private const int TailLineCount = 120;
    private const long DefaultMaxOutputBytes = 32L * 1024 * 1024;
    private const int DefaultMaxLineCharacters = 16 * 1024;

    private readonly string outputPath;
    private readonly IReadOnlyList<string> sensitiveValues;
    private readonly long maxOutputBytes;
    private readonly int maxLineCharacters;
    private readonly Action<string>? lineObserver;
    private readonly Channel<CapturedLine> channel = Channel.CreateBounded<CapturedLine>(
        new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly object tailLock = new();
    private readonly Queue<string> stdoutTail = new();
    private readonly Queue<string> stderrTail = new();
    private Task? completionTask;
    private int wasTruncated;

    public LaunchOutputCapture(
        string outputPath,
        IReadOnlyList<string> sensitiveValues,
        long maxOutputBytes = DefaultMaxOutputBytes,
        int maxLineCharacters = DefaultMaxLineCharacters,
        Action<string>? lineObserver = null)
    {
        this.outputPath = outputPath;
        this.sensitiveValues = sensitiveValues;
        this.maxOutputBytes = maxOutputBytes;
        this.maxLineCharacters = maxLineCharacters;
        this.lineObserver = lineObserver;
    }

    public void Start(Process process)
    {
        if (completionTask is not null)
            return;

        if (!process.StartInfo.RedirectStandardOutput && !process.StartInfo.RedirectStandardError)
        {
            completionTask = Task.CompletedTask;
            return;
        }

        completionTask = CaptureAsync(process);
    }

    public async Task<LaunchCapturedOutput> CompleteAsync()
    {
        if (completionTask is not null)
            await completionTask.ConfigureAwait(false);

        string stdout;
        string stderr;
        lock (tailLock)
        {
            stdout = string.Join(Environment.NewLine, stdoutTail);
            stderr = string.Join(Environment.NewLine, stderrTail);
        }

        var filePath = TryGetNonEmptyOutputPath();
        return new LaunchCapturedOutput(
            stdout,
            stderr,
            filePath,
            Volatile.Read(ref wasTruncated) != 0);
    }

    private async Task CaptureAsync(Process process)
    {
        var writerTask = WriteOutputAsync();
        var pumps = new List<Task>(2);
        if (process.StartInfo.RedirectStandardOutput)
            pumps.Add(PumpAsync(process.StandardOutput, "stdout", stdoutTail));
        if (process.StartInfo.RedirectStandardError)
            pumps.Add(PumpAsync(process.StandardError, "stderr", stderrTail));

        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        await writerTask.ConfigureAwait(false);
    }

    private async Task PumpAsync(StreamReader reader, string source, Queue<string> tail)
    {
        var redactor = new SensitiveLogText.LineRedactor(sensitiveValues);
        try
        {
            var buffer = new char[4096];
            var lineBuilder = new StringBuilder(Math.Min(maxLineCharacters, buffer.Length));
            var lineWasTruncated = false;
            var previousWasCarriageReturn = false;
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                    break;

                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\r')
                    {
                        await EmitLineAsync(source, tail, lineBuilder, lineWasTruncated, redactor).ConfigureAwait(false);
                        lineBuilder.Clear();
                        lineWasTruncated = false;
                        previousWasCarriageReturn = true;
                        continue;
                    }

                    if (character == '\n')
                    {
                        if (!previousWasCarriageReturn)
                        {
                            await EmitLineAsync(source, tail, lineBuilder, lineWasTruncated, redactor).ConfigureAwait(false);
                            lineBuilder.Clear();
                            lineWasTruncated = false;
                        }

                        previousWasCarriageReturn = false;
                        continue;
                    }

                    previousWasCarriageReturn = false;
                    if (lineBuilder.Length < maxLineCharacters)
                        lineBuilder.Append(character);
                    else
                        lineWasTruncated = true;
                }
            }

            if (lineBuilder.Length > 0 || lineWasTruncated)
                await EmitLineAsync(source, tail, lineBuilder, lineWasTruncated, redactor).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task EmitLineAsync(
        string source,
        Queue<string> tail,
        StringBuilder lineBuilder,
        bool lineWasTruncated,
        SensitiveLogText.LineRedactor redactor)
    {
        var line = lineBuilder.ToString();
        if (lineWasTruncated)
        {
            // Discarded input may contain a quote or only part of a known credential.
            // Its end cannot be reconstructed safely, so suppress the rest of this stream.
            redactor.SuppressRemainder();
            Interlocked.Exchange(ref wasTruncated, 1);
        }

        var redacted = redactor.RedactLine(line);
        lineObserver?.Invoke(redacted);
        lock (tailLock)
        {
            tail.Enqueue(redacted);
            while (tail.Count > TailLineCount)
                tail.Dequeue();
        }

        await channel.Writer.WriteAsync(new CapturedLine(source, redacted)).ConfigureAwait(false);
    }

    private async Task WriteOutputAsync()
    {
        StreamWriter? writer = null;
        var writeFailed = false;
        var writeLimitReached = false;
        long writtenBytes = 0;
        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (writeFailed || writeLimitReached)
                    continue;

                try
                {
                    if (writer is null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        writer = new StreamWriter(
                            new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }

                    var outputLine = $"[{line.Source}] {line.Text}";
                    var outputBytes = Encoding.UTF8.GetByteCount(outputLine)
                                      + Encoding.UTF8.GetByteCount(writer.NewLine);
                    if (writtenBytes + outputBytes > maxOutputBytes)
                    {
                        writeLimitReached = true;
                        Interlocked.Exchange(ref wasTruncated, 1);
                        var marker = $"[launcher] Captured output truncated at {maxOutputBytes} bytes.";
                        var markerBytes = Encoding.UTF8.GetByteCount(marker)
                                          + Encoding.UTF8.GetByteCount(writer.NewLine);
                        if (writtenBytes + markerBytes <= maxOutputBytes)
                        {
                            await writer.WriteLineAsync(marker).ConfigureAwait(false);
                            writtenBytes += markerBytes;
                        }

                        continue;
                    }

                    await writer.WriteLineAsync(outputLine).ConfigureAwait(false);
                    writtenBytes += outputBytes;
                }
                catch (IOException)
                {
                    writeFailed = true;
                }
                catch (UnauthorizedAccessException)
                {
                    writeFailed = true;
                }
            }
        }
        finally
        {
            if (writer is not null)
                await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string? TryGetNonEmptyOutputPath()
    {
        try
        {
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0
                ? outputPath
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record CapturedLine(string Source, string Text);
}
