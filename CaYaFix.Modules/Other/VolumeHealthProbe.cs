// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CaYaFix.Modules.Other;

internal static class VolumeHealthProbe
{
    private const uint FsctlIsVolumeDirty = 0x00090078;
    private const uint VolumeIsDirty = 0x00000001;

    public static bool IsDirty(string drive)
    {
        if (drive.Length != 2 || drive[1] != ':' || !char.IsAsciiLetter(drive[0]))
        {
            throw new ArgumentException("A drive-letter volume is required.", nameof(drive));
        }

        using var handle = CreateFile(
            $@"\\.\{char.ToUpperInvariant(drive[0])}:",
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!DeviceIoControl(
                handle,
                FsctlIsVolumeDirty,
                IntPtr.Zero,
                0,
                out var flags,
                sizeof(uint),
                out _,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (flags & VolumeIsDirty) != 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        out uint outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
