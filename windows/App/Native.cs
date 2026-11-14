using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FivePointClicker;

internal static class Native
{
    internal const uint MoveAbsolute = 0x8000 | 0x4000 | 0x0001;
    internal const uint UpFlags = 0x0004 | 0x0010 | 0x0040;
    internal static readonly nuint InputTag = 0x46504331;
    internal static readonly int InputSize = Marshal.SizeOf<Input>();

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Input(int x, int y, uint flags)
    {
        public readonly uint Type = 0;
        public readonly int X { get => Mouse.X; }
        public readonly int Y { get => Mouse.Y; }
        public readonly uint Flags { get => Mouse.Flags; }
        private readonly MouseInput Mouse = new(x, y, flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseInput(int x, int y, uint flags)
    {
        public readonly int X = x;
        public readonly int Y = y;
        public readonly uint Data = 0;
        public readonly uint Flags = flags;
        public readonly uint Time = 0;
        public readonly nuint Extra = InputTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")]
    internal static extern bool PeekMessageW(out Message message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    internal static extern int GetMessageW(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, nint callback, nint arg, bool resume);

    internal static (uint Down, uint Up) Flags(ButtonKind button) => button switch
    {
        ButtonKind.左键 => (0x0002, 0x0004),
        ButtonKind.右键 => (0x0008, 0x0010),
        ButtonKind.中键 => (0x0020, 0x0040),
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };
}

internal readonly record struct Desktop(int Left, int Top, int Width, int Height)
{
    public static Desktop Current => new(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77),
        Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));

    public Native.Input Move(ClickPoint point)
    {
        if (Width is < 1 or > 65536 || Height is < 1 or > 65536)
            throw new InvalidOperationException("虚拟桌面尺寸超出绝对坐标模式的逐像素映射范围。");
        if (Native.MonitorFromPoint(new Native.Point { X = point.X, Y = point.Y }, 0) == 0)
            throw new ArgumentException($"{point.Name} 点 ({point.X}, {point.Y}) 位于显示器之外。");

        return new Native.Input(Normalize(point.X - Left, Width),
            Normalize(point.Y - Top, Height), Native.MoveAbsolute);
    }

    private static int Normalize(int pixel, int size)
    {
        // The first normalized value in this pixel's bucket, rounded upward.
        return (int)((pixel * 65536L + size - 1) / size);
    }
}
