/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using CmlLib.Core.Files;
using CmlLib.Core.Tasks;
using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>Tracks logical bytes for one asset install, including assets verified in the cache.</summary>
internal sealed class AssetInstallProgress(IProgress<LauncherProgress> progress) : IUpdateTask
{
    private readonly object sync = new();
    private readonly Dictionary<string, Asset> assets = new(StringComparer.OrdinalIgnoreCase);
    private long totalBytes;
    private long availableBytes;
    private int lastPercent;

    public void Register(GameFile file)
    {
        lock (sync)
        {
            var asset = new Asset(file.Size);
            if (assets.TryAdd(file.Path!, asset))
                totalBytes += asset.Weight;
        }
    }

    public void ReportTransfer(GameFile file, int attempt, long bytes)
    {
        lock (sync)
        {
            if (!assets.TryGetValue(file.Path!, out var asset) || asset.Complete || attempt < asset.Attempt)
                return;

            var position = Math.Clamp(bytes, 0, asset.Size);
            if (attempt == asset.Attempt)
                position = Math.Max(asset.Position, position);
            asset.Attempt = attempt;
            UpdatePosition(asset, position);
        }
    }

    public ValueTask Execute(GameFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var asset = assets[file.Path!];
            asset.Complete = true;
            UpdatePosition(asset, asset.Weight);
        }
        return ValueTask.CompletedTask;
    }

    private void UpdatePosition(Asset asset, long position)
    {
        availableBytes += position - asset.Position;
        asset.Position = position;
        // Full completion is reported by the caller after the extractor and all update tasks succeed.
        var percent = (int)Math.Min(99, 100d * availableBytes / totalBytes);
        if (percent <= lastPercent)
            return;

        lastPercent = percent;
        progress.Report(new LauncherProgress(InstallProgressStages.DownloadingGameFiles, string.Empty, percent));
    }

    private sealed class Asset(long size)
    {
        public long Size { get; } = Math.Max(0, size);
        public long Weight => Math.Max(1, Size);
        public long Position { get; set; }
        public int Attempt { get; set; }
        public bool Complete { get; set; }
    }
}
