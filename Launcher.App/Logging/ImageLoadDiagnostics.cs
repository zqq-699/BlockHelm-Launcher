/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows.Media.Imaging;
using Launcher.Application.Services;
using Serilog;

namespace Launcher.App.Logging;

/// <summary>Observes WPF-managed requests; never performs network or persistence work in the view.</summary>
internal static class ImageLoadDiagnostics
{
    public static void Observe(BitmapImage image, string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return;
        var requestId = Guid.NewGuid().ToString("N");
        image.DownloadFailed += (_, args) => Failed(source, args.ErrorException, requestId, "ImageDownload");
        image.DecodeFailed += (_, args) => Failed(source, args.ErrorException, requestId, "ImageDecode");
    }

    public static void Failed(string source, Exception exception, string? requestId = null, string stage = "ImageLoad")
    {
        var host = Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.Host : "LocalImage";
        Log.ForContext("RequestId", requestId ?? Guid.NewGuid().ToString("N"))
            .ForContext("Stage", stage)
            .Warning(exception, "Image loading failed. Host={Host} HttpStatus=Unavailable Transport=WPF", host);
    }
}
