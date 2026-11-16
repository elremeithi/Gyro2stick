using System.Runtime.InteropServices;

namespace Gyro2stick;

public enum CaptureTool
{
    Off,
    /// <summary>Xbox Game Bar: Win+Alt+PrtScn screenshot, Win+Alt+R start/stop recording.</summary>
    GameBar,
    /// <summary>NVIDIA overlay: Alt+F1 screenshot, Alt+F9 start/stop recording.</summary>
    Nvidia,
}

/// <summary>Buttons that can be combined with PS. Never reach the game while used in a combo.</summary>
public enum ChordButton
{
    None,
    Create,
    Options,
    TouchpadClick,
    Triangle,
    Circle,
    Cross,
    Square,
    L1,
    R1,
    L2,
    R2,
    L3,
    R3,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
}

/// <summary>Presses the capture tool's keyboard shortcuts.</summary>
static class CaptureShortcuts
{
    const ushort VkLWin = 0x5B, VkAlt = 0x12, VkSnapshot = 0x2C, VkR = 0x52, VkF1 = 0x70, VkF9 = 0x78;

    public static void Screenshot(CaptureTool tool)
    {
        if (tool == CaptureTool.GameBar) KeyInput.Tap(VkLWin, VkAlt, VkSnapshot);
        else if (tool == CaptureTool.Nvidia) KeyInput.Tap(VkAlt, VkF1);
    }

    public static void ToggleRecording(CaptureTool tool)
    {
        if (tool == CaptureTool.GameBar) KeyInput.Tap(VkLWin, VkAlt, VkR);
        else if (tool == CaptureTool.Nvidia) KeyInput.Tap(VkAlt, VkF9);
    }
}

/// <summary>Synthesised keyboard input (SendInput).</summary>
static class KeyInput
{
    /// <summary>Presses the keys in order, then releases them in reverse.</summary>
    public static void Tap(params ushort[] keys)
    {
        var inputs = new Input[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], up: false);
            inputs[inputs.Length - 1 - i] = Key(keys[i], up: true);
        }
        Send(inputs);
    }

    public static void Down(ushort vk) => Send(Key(vk, up: false));

    public static void Up(ushort vk) => Send(Key(vk, up: true));

    static void Send(params Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    static Input Key(ushort vk, bool up)
    {
        const uint ExtendedKey = 0x1, KeyUp = 0x2, ScanCode = 0x8;
        uint flags = up ? KeyUp : 0;
        if (IsExtended(vk)) flags |= ExtendedKey;
        // Games that read the keyboard through DirectInput (most older PC games) only see the hardware scan code,
        // so send it whenever the key has one. PrtScn / Pause and media keys keep the virtual-key path.
        ushort scan = vk is 0x2C or 0x13 or (>= 0xA6 and <= 0xB7) ? (ushort)0 : (ushort)MapVirtualKey(vk, 0);
        if (scan != 0) flags |= ScanCode;
        return new Input { type = 1 /* INPUT_KEYBOARD */, ki = new KeybdInput { wVk = vk, wScan = scan, dwFlags = flags } };
    }

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>Keys on the extended part of the keyboard (navigation block, arrows, Win, PrtScn, Apps).</summary>
    static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 // PgUp, PgDn, End, Home, arrows
        or 0x2C or 0x2D or 0x2E   // PrtScn, Insert, Delete
        or 0x5B or 0x5C or 0x5D   // LWin, RWin, Apps
        or 0x6F or 0x90           // numpad divide, NumLock
        or 0xA3 or 0xA5;          // right Ctrl, right Alt

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, Input[] inputs, int size);

    // INPUT with the keyboard member; the explicit offset/size cover the larger MOUSEINPUT in the union.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct Input
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KeybdInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeybdInput
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }
}
