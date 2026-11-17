/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;

namespace Launcher.Application.Services;

/// <summary>Stores instance-local icon paths independently of the instance directory name.</summary>
public static class InstanceIconPaths
{
    private const string Prefix = "instance:";

    public static string? ToStorage(string? source, string? instanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(instanceDirectory))
            return source;
        try
        {
            string path;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
                path = uri.LocalPath;
            else if (Path.IsPathFullyQualified(source))
                path = source;
            else
                return source;
            var relative = Path.GetRelativePath(Path.GetFullPath(instanceDirectory), Path.GetFullPath(path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative == ".")
                return source;
            return Prefix + relative.Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return source;
        }
    }

    public static string? Resolve(string? source, string? previousDirectory, string? currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(currentDirectory))
            return source;
        var stored = ToStorage(source, previousDirectory);
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return stored;
        try
        {
            var relative = stored[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || string.IsNullOrWhiteSpace(relative)) return null;
            var root = Path.GetFullPath(currentDirectory);
            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            var checkedRelative = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(checkedRelative) || checkedRelative == "." || checkedRelative == ".."
                || checkedRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            return new Uri(fullPath).AbsoluteUri;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return null;
        }
    }
}
