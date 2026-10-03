/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Launcher.App.Services;

internal readonly record struct NativeMonitorBounds(int Left, int Top, int Right, int Bottom);

internal readonly record struct MaximizedWindowBounds(int X, int Y, int Width, int Height);

internal static class MaximizedWindowWorkArea
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        HwndSource? source = null;

        void AttachHook()
        {
            if (source is not null)
                return;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            source = HwndSource.FromHwnd(handle);
            source?.AddHook(WindowProcedure);
        }

        void OnSourceInitialized(object? sender, EventArgs e) => AttachHook();

        void OnClosed(object? sender, EventArgs e)
        {
            if (source is not null)
                source.RemoveHook(WindowProcedure);

            window.SourceInitialized -= OnSourceInitialized;
            window.Closed -= OnClosed;
        }

        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnClosed;
        AttachHook();
    }

    internal static MaximizedWindowBounds CalculateBounds(
        NativeMonitorBounds monitor,
        NativeMonitorBounds workArea)
    {
        return new MaximizedWindowBounds(
            workArea.Left - monitor.Left,
            workArea.Top - monitor.Top,
            Math.Max(0, workArea.Right - workArea.Left),
            Math.Max(0, workArea.Bottom - workArea.Top));
    }

    private static IntPtr WindowProcedure(
        IntPtr handle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var monitorHandle = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
            return IntPtr.Zero;

        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
            return IntPtr.Zero;

        var bounds = CalculateBounds(
            new NativeMonitorBounds(
                monitorInfo.Monitor.Left,
                monitorInfo.Monitor.Top,
                monitorInfo.Monitor.Right,
                monitorInfo.Monitor.Bottom),
            new NativeMonitorBounds(
                monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Top,
                monitorInfo.WorkArea.Right,
                monitorInfo.WorkArea.Bottom));
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.MaxPosition = new NativePoint(bounds.X, bounds.Y);
        minMaxInfo.MaxSize = new NativePoint(bounds.Width, bounds.Height);
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
}
