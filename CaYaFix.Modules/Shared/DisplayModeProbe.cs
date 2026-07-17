// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using System.Text;

namespace CaYaFix.Modules.Shared;

/// <summary>
/// Enumerates and optionally applies Win32 display modes (CCD / EnumDisplaySettings).
/// Used to detect stuck/greyed resolution and restore the highest advertised mode.
/// </summary>
internal static class DisplayModeProbe
{
    private const int EnumCurrentSettings = -1;
    private const int CdsUpdateRegistry = 0x01;
    private const int CdsReset = 0x40000000;
    private const int DmPelsWidth = 0x80000;
    private const int DmPelsHeight = 0x100000;
    private const int DmBitsPerPel = 0x40000;
    private const int DmDisplayFrequency = 0x400000;
    private const int DisplayDeviceAttachedToDesktop = 0x1;

    public sealed record PathInfo(
        string Device,
        string Name,
        int CurrentWidth,
        int CurrentHeight,
        int ModeCount,
        int MaxWidth,
        int MaxHeight,
        int MaxFrequency);

    public static IReadOnlyList<PathInfo> EnumerateActivePaths()
    {
        var results = new List<PathInfo>();
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint index = 0; index < 16; index++)
        {
            if (!EnumDisplayDevices(null, index, ref device, 0))
            {
                break;
            }

            if ((device.StateFlags & DisplayDeviceAttachedToDesktop) == 0)
            {
                continue;
            }

            var deviceName = device.DeviceName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                continue;
            }

            var current = default(DEVMODE);
            current.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            var curW = 0;
            var curH = 0;
            if (EnumDisplaySettings(deviceName, EnumCurrentSettings, ref current))
            {
                curW = current.dmPelsWidth;
                curH = current.dmPelsHeight;
            }

            var unique = new HashSet<string>(StringComparer.Ordinal);
            var maxW = 0;
            var maxH = 0;
            var maxHz = 0;
            var maxPx = 0L;
            for (var mode = 0; mode < 512; mode++)
            {
                var dm = default(DEVMODE);
                dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
                if (!EnumDisplaySettings(deviceName, mode, ref dm))
                {
                    break;
                }

                var key = $"{dm.dmPelsWidth}x{dm.dmPelsHeight}";
                if (!unique.Add(key))
                {
                    // Still track higher refresh for the same resolution.
                    var pxSame = (long)dm.dmPelsWidth * dm.dmPelsHeight;
                    if (pxSame == maxPx && dm.dmDisplayFrequency > maxHz)
                    {
                        maxHz = dm.dmDisplayFrequency;
                    }

                    continue;
                }

                var px = (long)dm.dmPelsWidth * dm.dmPelsHeight;
                if (px > maxPx || (px == maxPx && dm.dmDisplayFrequency > maxHz))
                {
                    maxPx = px;
                    maxW = dm.dmPelsWidth;
                    maxH = dm.dmPelsHeight;
                    maxHz = dm.dmDisplayFrequency;
                }
            }

            results.Add(new PathInfo(
                deviceName,
                device.DeviceString ?? string.Empty,
                curW,
                curH,
                unique.Count,
                maxW,
                maxH,
                maxHz));
        }

        return results;
    }

    /// <summary>
    /// Applies the largest advertised mode on each active path. Returns a human-readable log.
    /// </summary>
    public static string ApplyHighestSupportedModes()
    {
        var log = new StringBuilder();
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        var appliedAny = false;
        for (uint index = 0; index < 16; index++)
        {
            if (!EnumDisplayDevices(null, index, ref device, 0))
            {
                break;
            }

            if ((device.StateFlags & DisplayDeviceAttachedToDesktop) == 0)
            {
                continue;
            }

            var deviceName = device.DeviceName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                continue;
            }

            DEVMODE best = default;
            var bestPx = 0L;
            var bestHz = 0;
            var found = false;
            for (var mode = 0; mode < 512; mode++)
            {
                var dm = default(DEVMODE);
                dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
                if (!EnumDisplaySettings(deviceName, mode, ref dm))
                {
                    break;
                }

                var px = (long)dm.dmPelsWidth * dm.dmPelsHeight;
                if (px > bestPx || (px == bestPx && dm.dmDisplayFrequency > bestHz))
                {
                    bestPx = px;
                    bestHz = dm.dmDisplayFrequency;
                    best = dm;
                    found = true;
                }
            }

            if (!found)
            {
                log.AppendLine($"{deviceName}: no modes");
                continue;
            }

            var current = default(DEVMODE);
            current.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            EnumDisplaySettings(deviceName, EnumCurrentSettings, ref current);
            if (current.dmPelsWidth == best.dmPelsWidth &&
                current.dmPelsHeight == best.dmPelsHeight &&
                current.dmDisplayFrequency == best.dmDisplayFrequency)
            {
                log.AppendLine(
                    $"{deviceName}: already {current.dmPelsWidth}x{current.dmPelsHeight}@{current.dmDisplayFrequency}");
                continue;
            }

            best.dmFields = DmPelsWidth | DmPelsHeight | DmBitsPerPel | DmDisplayFrequency;
            var code = ChangeDisplaySettingsEx(
                deviceName,
                ref best,
                IntPtr.Zero,
                CdsUpdateRegistry | CdsReset,
                IntPtr.Zero);
            appliedAny = true;
            log.AppendLine(
                $"{deviceName}: set {best.dmPelsWidth}x{best.dmPelsHeight}@{best.dmDisplayFrequency} => {code}");
        }

        if (!appliedAny && log.Length == 0)
        {
            log.AppendLine("no active display paths");
        }

        return log.ToString().TrimEnd();
    }

    public static bool IsSparseOrSubNative(PathInfo path)
    {
        var curPx = (long)path.CurrentWidth * path.CurrentHeight;
        var maxPx = (long)path.MaxWidth * path.MaxHeight;
        return path.ModeCount is > 0 and <= 3 ||
               (curPx > 0 && maxPx > 0 && maxPx >= curPx * 2 && path.ModeCount >= 4 && curPx <= 800L * 600L) ||
               (curPx > 0 && maxPx > curPx && path.ModeCount >= 6 &&
                (path.CurrentWidth < 1280 || path.CurrentHeight < 720) && path.MaxWidth >= 1920);
    }

    public static bool IsSubNativeWithModes(PathInfo path)
    {
        var curPx = (long)path.CurrentWidth * path.CurrentHeight;
        var maxPx = (long)path.MaxWidth * path.MaxHeight;
        return maxPx > curPx && path.ModeCount >= 4;
    }

    public static bool PathsLookHealthy(IReadOnlyList<PathInfo> paths)
    {
        if (paths.Count == 0) return true;
        foreach (var path in paths)
        {
            var curPx = (long)path.CurrentWidth * path.CurrentHeight;
            var maxPx = (long)path.MaxWidth * path.MaxHeight;
            if (path.ModeCount >= 4 && maxPx > 0 && curPx > 0 &&
                curPx < maxPx / 2 && path.CurrentWidth <= 1280)
            {
                return false;
            }
        }

        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string lpszDeviceName,
        ref DEVMODE lpDevMode,
        IntPtr hwnd,
        int dwflags,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
