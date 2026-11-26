/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// Renames the directory object represented by an open handle. A path replacement after validation therefore
/// cannot redirect the rename to a different directory (the usual path-based rename ABA window).
/// </summary>
internal static class WindowsDirectoryHandleMover
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileListDirectory = 0x00000001;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileRenameInfo = 3;

    public static void MoveOwnedDirectory(
        string sourceDirectory,
        string destinationDirectory,
        Func<bool> sourceOwnershipPredicate,
        Action? afterSourceHandleOpened = null)
    {
        ArgumentNullException.ThrowIfNull(sourceOwnershipPredicate);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Identity-safe directory moves require Windows.");

        using var ownedHandle = OpenDirectory(sourceDirectory);
        var ownedIdentity = ReadIdentity(ownedHandle);
        afterSourceHandleOpened?.Invoke();

        if (!sourceOwnershipPredicate())
            throw new InvalidOperationException("The source directory is no longer owned by this transaction.");

        using (var currentPathHandle = OpenDirectory(sourceDirectory))
        {
            if (ReadIdentity(currentPathHandle) != ownedIdentity)
                throw new InvalidOperationException("The source path changed while its ownership was being verified.");
        }

        RenameByHandle(ownedHandle, Path.GetFullPath(destinationDirectory));
    }

    internal static string CaptureProof(string path)
    {
        using var handle = OpenDirectory(path, requireDelete: false);
        var identity = ReadIdentity(handle);
        return $"directory-v2:{identity.VolumeSerialNumber:X8}:{identity.FileIndex:X16}";
    }

    internal static SafeFileHandle AcquireReadLease(string path) => OpenDirectory(path, requireDelete: false, shareDelete: false, requireList: true);

    internal readonly record struct CleanupFileSnapshot(string Identity, long Length, long LastWriteTimeUtcTicks);

    internal static CleanupFileSnapshot? CaptureCleanupFile(string path)
    {
        using var handle = OpenDirectory(path, requireDelete: false);
        return ReadCleanupFileSnapshot(handle);
    }

    internal static bool DeleteFileIfIdentityMatches(string path, string expectedProof, long length, long lastWriteTimeUtcTicks)
    {
        // Deny concurrent writers and renames until the verified handle has been deleted and closed.
        using var handle = OpenDirectory(path, requireDelete: true, shareDelete: false, shareWrite: false);
        var snapshot = ReadCleanupFileSnapshot(handle);
        if (snapshot is not { } current
            || !string.Equals(current.Identity, expectedProof, StringComparison.Ordinal)
            || current.Length != length || current.LastWriteTimeUtcTicks != lastWriteTimeUtcTicks) return false;
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(handle, 4, buffer, 1))
                throw new IOException("Unable to delete cleanup file.", new Win32Exception(Marshal.GetLastWin32Error()));
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static CleanupFileSnapshot? ReadCleanupFileSnapshot(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Unable to inspect cleanup file.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
        var identity = $"directory-v2:{info.VolumeSerialNumber:X8}:{((ulong)info.FileIndexHigh << 32) | info.FileIndexLow:X16}";
        var length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var fileTime = ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
        return new CleanupFileSnapshot(identity, length, DateTime.FromFileTimeUtc(fileTime).Ticks);
    }

    private static SafeFileHandle OpenDirectory(string path, bool requireDelete = true, bool shareDelete = true, bool requireList = false, bool shareWrite = true)
    {
        var handle = CreateFileW(
            Path.GetFullPath(path),
            (requireDelete ? DeleteAccess : 0) | FileReadAttributes | (requireList ? FileListDirectory : 0),
            ShareRead | (shareWrite ? ShareWrite : 0) | (shareDelete ? ShareDelete : 0),
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Failed to open directory: {path} (Win32 error {error})",
                new Win32Exception(error));
        }
        return handle;
    }

    private static DirectoryIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Failed to read directory identity. (Win32 error {error})",
                new Win32Exception(error));
        }
        return new DirectoryIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static void RenameByHandle(SafeFileHandle handle, string destinationDirectory)
    {
        var destinationBytes = System.Text.Encoding.Unicode.GetBytes(destinationDirectory);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        // FILE_RENAME_INFO.FileNameLength excludes the terminator, but FileName itself is a wide string.
        // Reserve and zero a terminator so the kernel never reads uninitialized memory past the supplied name.
        var bufferSize = checked(nameOffset + destinationBytes.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0; index < bufferSize; index++)
                Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, destinationBytes.Length);
            Marshal.Copy(destinationBytes, 0, buffer + nameOffset, destinationBytes.Length);

            if (!SetFileInformationByHandle(handle, FileRenameInfo, buffer, (uint)bufferSize))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException(
                    $"Failed to rename directory to: {destinationDirectory} (Win32 error {error})",
                    new Win32Exception(error));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private readonly record struct DirectoryIdentity(uint VolumeSerialNumber, ulong FileIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);
}
