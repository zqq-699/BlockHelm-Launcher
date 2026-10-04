/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Text.Json;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Tests.Infrastructure.Persistence;

public sealed class AtomicJsonFileWriterTests : TestTempDirectory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public async Task WriteReplacesTheDestinationAndLeavesNoTemporaryFile()
    {
        var destination = Path.Combine(TempRoot, "payload.json");
        await AtomicJsonFileWriter.WriteAsync(destination, new Payload("first"), JsonOptions, CancellationToken.None);

        await AtomicJsonFileWriter.WriteAsync(destination, new Payload("second"), JsonOptions, CancellationToken.None);

        Assert.Equal("second", ReadPayload(destination).Name);
        Assert.Empty(Directory.GetFiles(TempRoot, "*.tmp"));
    }

    [Fact]
    public async Task WriteRetriesWhileTheDestinationIsTemporarilyHeldOpen()
    {
        // 网络盘与杀软会短暂持有目标句柄，使替换式重命名报 ACCESS_DENIED；
        // 用一个很快释放的独占句柄模拟这种瞬时占用。
        var destination = Path.Combine(TempRoot, "payload.json");
        await AtomicJsonFileWriter.WriteAsync(destination, new Payload("first"), JsonOptions, CancellationToken.None);

        using var blocker = OpenExclusively(destination);
        var retryCount = 0;

        await AtomicJsonFileWriter.WriteAsync(
            destination,
            new Payload("second"),
            JsonOptions,
            CancellationToken.None,
            maxMoveAttempts: 2,
            retryAsync: _ =>
            {
                retryCount++;
                blocker.Dispose();
                return Task.CompletedTask;
            });

        Assert.Equal(1, retryCount);
        Assert.Equal("second", ReadPayload(destination).Name);
        Assert.Empty(Directory.GetFiles(TempRoot, "*.tmp"));
    }

    [Fact]
    public async Task WriteSurfacesTheFailureAndCleansUpWhenTheDestinationStaysLocked()
    {
        var destination = Path.Combine(TempRoot, "payload.json");
        await AtomicJsonFileWriter.WriteAsync(destination, new Payload("first"), JsonOptions, CancellationToken.None);

        using (OpenExclusively(destination))
        {
            var exception = await Record.ExceptionAsync(() =>
                AtomicJsonFileWriter.WriteAsync(
                    destination,
                    new Payload("second"),
                    JsonOptions,
                    CancellationToken.None,
                    maxMoveAttempts: 2,
                    retryAsync: _ => Task.CompletedTask));

            Assert.True(exception is IOException or UnauthorizedAccessException, $"Unexpected exception: {exception}");
        }

        // 重试耗尽后调用方自行处理失败，但临时文件不能留在数据目录里。
        Assert.Equal("first", ReadPayload(destination).Name);
        Assert.Empty(Directory.GetFiles(TempRoot, "*.tmp"));
    }

    private static FileStream OpenExclusively(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static Payload ReadPayload(string path) =>
        JsonSerializer.Deserialize<Payload>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException("The payload could not be read back.");

    private sealed record Payload(string Name);
}
