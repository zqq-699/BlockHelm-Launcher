/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Launcher.Application.Services;

/// <summary>Safe, explicitly supplied diagnostic data. Arbitrary exception messages are never trusted.</summary>
public interface ISafeDiagnosticException
{
    string DiagnosticCode { get; }
    IReadOnlyDictionary<string, object?> DiagnosticFields { get; }
}

/// <summary>Correlates async operations without depending on a logging provider or external I/O.</summary>
public static class DownloadDiagnostics
{
    private sealed record Context(string OperationId, string? BranchId, string? FileId, string? RequestId, string? ParentRequestId = null);
    private static readonly AsyncLocal<Context?> Current = new();

    public static IDisposable BeginOperation(ILogger? logger, string operation, string? operationId = null)
    {
        if (Current.Value is not null)
            return logger?.BeginScope(new Dictionary<string, object?> { ["Operation"] = operation }) ?? EmptyScope.Instance;
        var previous = Current.Value;
        var context = new Context(operationId ?? Guid.NewGuid().ToString("N"), null, null, null);
        Current.Value = context;
        var scope = logger?.BeginScope(Fields(context));
        logger?.LogInformation("Download operation started. Operation={Operation} LauncherBuild={LauncherBuild} OS={OS} ProcessArchitecture={ProcessArchitecture} Framework={Framework}",
            operation, Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture, RuntimeInformation.FrameworkDescription);
        return new RestoreScope(previous, scope);
    }

    public static IDisposable BeginFile(ILogger? logger) => Current.Value?.FileId is not null
        ? logger?.BeginScope(Fields(Current.Value)) ?? EmptyScope.Instance : BeginChild(logger, "file");
    public static IDisposable BeginRequest(ILogger? logger) => BeginChild(logger, "request");
    public static IDisposable BeginBranch(ILogger? logger, string branch) => BeginChild(logger, "branch", branch);

    private static IDisposable BeginChild(ILogger? logger, string kind, string? name = null)
    {
        var previous = Current.Value;
        var context = previous ?? new Context(Guid.NewGuid().ToString("N"), null, null, null);
        var id = name ?? Guid.NewGuid().ToString("N");
        context = kind switch
        {
            "file" => context with { FileId = id, RequestId = null, ParentRequestId = null },
            "branch" => context with { BranchId = id, FileId = null, RequestId = null, ParentRequestId = null },
            _ => context with { RequestId = id, ParentRequestId = context.RequestId }
        };
        Current.Value = context;
        return new RestoreScope(previous, logger?.BeginScope(Fields(context)));
    }

    public static IReadOnlyDictionary<string, object?> Capture() => Current.Value is { } context
        ? Fields(context) : new Dictionary<string, object?>();

    public static IDisposable Restore(ILogger? logger, IReadOnlyDictionary<string, object?> fields)
    {
        var previous = Current.Value;
        if (fields.TryGetValue("OperationId", out var operation) && operation is string id)
            Current.Value = new Context(id, Value("BranchId"), Value("FileId"), Value("RequestId"), Value("ParentRequestId"));
        return new RestoreScope(previous, logger?.BeginScope(fields));
        string? Value(string key) => fields.TryGetValue(key, out var value) ? value as string : null;
    }

    public static async Task<T> RunStageAsync<T>(ILogger? logger, string stage, Func<Task<T>> action, bool quiet = false)
    {
        using var scope = logger?.BeginScope(new Dictionary<string, object?> { ["Stage"] = stage });
        var stopwatch = Stopwatch.StartNew();
        if (!quiet)
            logger?.LogInformation("Download stage started. Stage={Stage}", stage);
        using var timer = new Timer(_ => logger?.LogInformation("Download stage still running. Stage={Stage} DurationMs={DurationMs}", stage, stopwatch.ElapsedMilliseconds),
            null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        try
        {
            var result = await action().ConfigureAwait(false);
            if (!quiet)
                logger?.LogInformation("Download stage completed. Stage={Stage} DurationMs={DurationMs}", stage, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger?.LogInformation("Download stage canceled. Stage={Stage} DurationMs={DurationMs}", stage, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Download stage failed. Stage={Stage} DurationMs={DurationMs}", stage, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public static Task RunStageAsync(ILogger? logger, string stage, Func<Task> action) =>
        RunStageAsync(logger, stage, async () => { await action().ConfigureAwait(false); return true; });

    public static T RunStage<T>(ILogger? logger, string stage, Func<T> action)
    {
        using var scope = logger?.BeginScope(new Dictionary<string, object?> { ["Stage"] = stage });
        var stopwatch = Stopwatch.StartNew();
        logger?.LogInformation("Download stage started. Stage={Stage}", stage);
        try
        {
            var result = action();
            logger?.LogInformation("Download stage completed. Stage={Stage} DurationMs={DurationMs}", stage, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Download stage failed. Stage={Stage} DurationMs={DurationMs}", stage, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public static void RunStage(ILogger? logger, string stage, Action action) =>
        RunStage(logger, stage, () => { action(); return true; });

    private static Dictionary<string, object?> Fields(Context context) => new()
    {
        ["OperationId"] = context.OperationId, ["BranchId"] = context.BranchId,
        ["FileId"] = context.FileId, ["RequestId"] = context.RequestId, ["ParentRequestId"] = context.ParentRequestId
    };

    private sealed class RestoreScope(Context? previous, IDisposable? scope) : IDisposable
    {
        public void Dispose() { scope?.Dispose(); Current.Value = previous; }
    }
    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
