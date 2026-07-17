// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace CaYaFix.Modules.Audio;

internal static class AudioPolicyConfig
{
    public static bool SetDefaultEndpoint(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 4_096)
        {
            return false;
        }

        var client = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            var console = client.SetDefaultEndpoint(deviceId, DefaultAudioRole.Console);
            var multimedia = client.SetDefaultEndpoint(deviceId, DefaultAudioRole.Multimedia);
            var communications = client.SetDefaultEndpoint(deviceId, DefaultAudioRole.Communications);
            return console == 0 && multimedia == 0 && communications == 0;
        }
        finally
        {
            Marshal.FinalReleaseComObject(client);
        }
    }

    public static bool SetDefaultEndpoint(string deviceId, DefaultAudioRole role)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 4_096 || !Enum.IsDefined(role)) return false;

        var client = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            return client.SetDefaultEndpoint(deviceId, role) == 0;
        }
        finally
        {
            Marshal.FinalReleaseComObject(client);
        }
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long period, out long minimumPeriod);
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DefaultAudioRole role);

        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}

internal enum DefaultAudioRole
{
    Console,
    Multimedia,
    Communications
}
