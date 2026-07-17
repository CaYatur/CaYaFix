// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace CaYaFix.Modules.Shared;

/// <summary>
/// Triggers the same graphics-driver reset as the interactive hotkey
/// Win+Ctrl+Shift+B (display may flash black briefly).
/// </summary>
internal static class GraphicsDriverHotkey
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkLwin = 0x5B;
    private const ushort VkB = 0x42;

    public static void TriggerWinCtrlShiftB()
    {
        var inputs = new[]
        {
            Key(VkLwin, down: true),
            Key(VkControl, down: true),
            Key(VkShift, down: true),
            Key(VkB, down: true),
            Key(VkB, down: false),
            Key(VkShift, down: false),
            Key(VkControl, down: false),
            Key(VkLwin, down: false)
        };

        var size = Marshal.SizeOf<INPUT>();
        var sent = SendInput((uint)inputs.Length, inputs, size);
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput delivered {sent}/{inputs.Length} events (Win+Ctrl+Shift+B).");
        }
    }

    private static INPUT Key(ushort virtualKey, bool down) =>
        new()
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = down ? 0u : KeyeventfKeyup,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
