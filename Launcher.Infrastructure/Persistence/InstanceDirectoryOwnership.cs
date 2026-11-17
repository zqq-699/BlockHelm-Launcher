/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;

namespace Launcher.Infrastructure.Persistence;

internal static class InstanceDirectoryOwnership
{
    private sealed record Snapshot(string Directory, string Proof, string SettingsDigest);
    private static readonly ConditionalWeakTable<GameInstance, Snapshot> Snapshots = new();
    internal static string SettingsPath(string directory) => Path.Combine(directory, LauncherApplicationIdentity.StorageDirectoryName, "instance-settings.json");
    internal static string Capture(string directory) => WindowsDirectoryHandleMover.CaptureProof(directory);
    internal static bool Matches(string directory, string? proof)
    {
        if (string.IsNullOrWhiteSpace(proof)) return false;
        try
        {
            if (proof.StartsWith("directory-v2:", StringComparison.Ordinal))
                return string.Equals(Capture(directory), proof, StringComparison.Ordinal);
            // Only legacy transaction readers may consult the retired ID.
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath(directory)));
            return json.RootElement.EnumerateObject().Any(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase)
                && p.Value.ValueKind == JsonValueKind.String && string.Equals(p.Value.GetString(), proof, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
    internal static string Digest(string directory)
    {
        var path = SettingsPath(directory);
        return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    }
    internal static void Observe(GameInstance instance)
    {
        Observe(instance, Capture(instance.InstanceDirectory), Digest(instance.InstanceDirectory));
    }
    internal static void Observe(GameInstance instance, string proof, string digest)
    {
        var snapshot = new Snapshot(MinecraftDirectoryPath.Normalize(instance.InstanceDirectory), proof, digest);
        lock (Snapshots)
        {
            Snapshots.Remove(instance);
            Snapshots.Add(instance, snapshot);
        }
    }
    internal static string EnsureCurrent(GameInstance instance)
    {
        if (!Snapshots.TryGetValue(instance, out var snapshot)
            || !MinecraftDirectoryPath.Equals(snapshot.Directory, instance.InstanceDirectory)
            || !Matches(instance.InstanceDirectory, snapshot.Proof)
            || snapshot.SettingsDigest != Digest(instance.InstanceDirectory))
            throw new GameInstanceMutationConflictException(instance.InstanceDirectory, instance.VersionName);
        return snapshot.Proof;
    }
    internal static bool IsObserved(GameInstance instance) => Snapshots.TryGetValue(instance, out _);
    internal static bool IsSameDirectorySnapshot(GameInstance previous, GameInstance current) =>
        Snapshots.TryGetValue(previous, out var oldSnapshot)
        && Snapshots.TryGetValue(current, out var newSnapshot)
        && MinecraftDirectoryPath.Equals(oldSnapshot.Directory, previous.InstanceDirectory)
        && MinecraftDirectoryPath.Equals(newSnapshot.Directory, current.InstanceDirectory)
        && MinecraftDirectoryPath.Equals(oldSnapshot.Directory, newSnapshot.Directory)
        && oldSnapshot.Proof == newSnapshot.Proof;
    internal static bool CanReuseSnapshot(GameInstance previous, GameInstance current, bool allowOlderRead = false)
    {
        if (!Snapshots.TryGetValue(previous, out var oldSnapshot)
            || !Snapshots.TryGetValue(current, out var newSnapshot)
            || !MinecraftDirectoryPath.Equals(oldSnapshot.Directory, previous.InstanceDirectory)
            || !MinecraftDirectoryPath.Equals(newSnapshot.Directory, current.InstanceDirectory)
            || !MinecraftDirectoryPath.Equals(oldSnapshot.Directory, newSnapshot.Directory)
            || oldSnapshot.Proof != newSnapshot.Proof)
            return false;
        if (oldSnapshot.SettingsDigest == newSnapshot.SettingsDigest)
            return true;
        if (!allowOlderRead)
            return false;
        // A scan can finish after a newer local save. Keep its still-current editor
        // rather than replacing it with the older read result.
        try { return Matches(previous.InstanceDirectory, oldSnapshot.Proof) && Digest(previous.InstanceDirectory) == oldSnapshot.SettingsDigest; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    internal static string? GetObservedProof(GameInstance instance) => Snapshots.TryGetValue(instance, out var snapshot) ? snapshot.Proof : null;
    internal static IDisposable AcquireReadLease(GameInstance instance)
    {
        var lease = WindowsDirectoryHandleMover.AcquireReadLease(instance.InstanceDirectory);
        try
        {
            Minecraft.MinecraftPathGuard.EnsureNoReparsePoints(instance.InstanceDirectory, instance.InstanceDirectory, "Instance directory");
            if (IsObserved(instance)) EnsureCurrent(instance);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
    internal static T? ReadLegacyMarker<T>(string json, JsonSerializerOptions options)
    {
        if (JsonNode.Parse(json) is not JsonObject node) return default;
        var legacy = node.FirstOrDefault(p => p.Key.Equals("instanceId", StringComparison.OrdinalIgnoreCase));
        if (legacy.Key is not null && !node.Any(p => p.Key.Equals("ownerProof", StringComparison.OrdinalIgnoreCase)))
            node["OwnerProof"] = legacy.Value?.DeepClone();
        return node.Deserialize<T>(options);
    }
}
