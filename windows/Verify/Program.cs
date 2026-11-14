using FivePointClicker;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: Verify <absolute-path-to-file-for-JSON-test>");
            return 2;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var fixture = new Fixture(args[0]);
        fixture.Window.ContentRendered += async (_, _) =>
        {
            try { await fixture.Run(); app.Shutdown(0); }
            catch (Exception ex) { Console.WriteLine(ex); app.Shutdown(1); }
        };
        fixture.Window.Show();
        return app.Run();
    }
}

internal sealed class Fixture(string configPath)
{
    private readonly List<(int X, int Y, int Button, bool Down)> events = [];
    private readonly Stopwatch watch = new();
    private ClickPoint[] points = [];
    private nint hwnd;
    private nint mouseHook;
    private MouseHook? callback;
    private volatile bool armed;
    private int blocked;
    private int otherMouseEvents;

    public Window Window { get; } = new()
    {
        Title = "FivePointClicker test receiver",
        Width = 650, Height = 370, WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Topmost = true, Background = System.Windows.Media.Brushes.White,
        Content = new TextBlock
        {
            Text = "FivePointClicker test receiver — automated mouse test in progress",
            Margin = new Thickness(18)
        }
    };

    public async Task Run()
    {
        hwnd = new WindowInteropHelper(Window).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(Receive);
        Window.Activate();
        callback = Guard;
        var hookReady = new TaskCompletionSource<uint>();
        var pump = new Thread(() =>
        {
            PeekMessageW(out _, 0, 0, 0, 0);
            mouseHook = SetWindowsHookExW(14, callback, GetModuleHandleW(null), 0);
            if (mouseHook == 0)
            {
                hookReady.SetException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                return;
            }
            hookReady.SetResult(GetCurrentThreadId());
            while (GetMessageW(out _, 0, 0, 0) > 0) { }
            UnhookWindowsHookEx(mouseHook);
        }) { IsBackground = true };
        pump.Start();
        uint hookThread = await hookReady.Task;
        try
        {
            var model = new MainViewModel();
            for (int i = 0; i < Settings.MaxPoints; i++)
            {
                var p = Window.PointToScreen(new Point(80 + i * 105, 95 + (i % 2) * 110));
                var row = model.Add(0, 0);
                row.X = ((int)p.X).ToString();
                row.Y = ((int)p.Y).ToString();
                SetCursorPos((int)p.X, (int)p.Y);
                Check(GetCursorPos(out var read) && read.X == (int)p.X && read.Y == (int)p.Y, $"读取 {row.Name} 坐标");
            }
            points = model.Snapshot().Points;
            Check(points.Length == 5, "新增并修改五点");
            var pixels = GetSystemMetrics(78) + "×" + GetSystemMetrics(79);
            Console.WriteLine($"Desktop pixels={pixels}; origin={GetSystemMetrics(76)},{GetSystemMetrics(77)}; monitors={GetSystemMetrics(80)}; DPI={GetDpiForWindow(hwnd)}; logical CPUs={Environment.ProcessorCount}");
            Console.WriteLine(string.Join(" ", points.Select(p => $"{p.Name}=({p.X},{p.Y})")));
            var baseline = new Settings { Points = points, Countdown = 0, RoundDelay = 0 };

            var mixed = baseline with
            {
                Mode = RunMode.指定轮数, Rounds = 40, PointDelay = 5, RoundDelay = 10,
                Points = [
                    points[0],
                    points[1] with { Button = ButtonKind.右键, Count = 2 },
                    points[2] with { Button = ButtonKind.中键, Kind = ClickKind.双击 },
                    points[4] with { Count = 3 },
                    points[3] with { Enabled = false }
                ]
            };
            ConfigStore.Write(configPath, mixed);
            Check(ConfigStore.Read(configPath) is var restored && restored.Points.SequenceEqual(mixed.Points)
                && (restored with { Points = mixed.Points }) == mixed, "JSON 保存与完整往返");
            File.Delete(configPath);
            Check(!File.Exists(configPath), "删除测试配置");

            armed = true;
            await Measure("mixed", mixed);
            foreach (int delay in new[] { 5, 1, 0 })
                await Measure($"performance {delay}ms", baseline with { Mode = RunMode.指定轮数, Rounds = 500, PointDelay = delay });

            await TestControls(baseline);
            Console.WriteLine("Begin 120-second run");
            await Measure("continuous 120 sec", baseline with { Mode = RunMode.指定时间, Seconds = 120, PointDelay = 1, RoundDelay = 10 });
            Check(blocked == 0, "全部输入命中接收窗口，防护无需拦截");
        }
        finally
        {
            armed = false;
            PostThreadMessageW(hookThread, 0x12, 0, 0);
            pump.Join();
            GC.KeepAlive(callback);
        }
    }

    private async Task Measure(string label, Settings settings)
    {
        events.Clear();
        int blockedBefore = Volatile.Read(ref blocked);
        int otherBefore = Volatile.Read(ref otherMouseEvents);
        var proc = Process.GetCurrentProcess();
        var cpuBefore = proc.TotalProcessorTime;
        if (settings.Seconds == 120)
            events.EnsureCapacity(200000);
        long baselineMemory = proc.PrivateMemorySize64;
        var memory = new List<string>();
        watch.Restart();
        using var run = new ClickRun(settings);
        while (!run.Completion.IsCompleted)
        {
            var delay = Task.Delay(settings.Seconds == 120 ? 15000 : 100);
            if (await Task.WhenAny(run.Completion, delay) == delay && settings.Seconds == 120)
            {
                proc.Refresh();
                memory.Add($"{watch.Elapsed.TotalSeconds:F0}s={proc.PrivateMemorySize64 / 1048576.0:F1}MiB");
            }
        }
        await run.Completion;
        double wall = watch.Elapsed.TotalSeconds;
        double cpu = (proc.TotalProcessorTime - cpuBefore).TotalSeconds / wall * 100;
        var stats = run.GetStats();
        await Task.Delay(200);
        string validation = VerifyEvents(settings, stats);
        Console.WriteLine($"{label}: clicks={stats.Clicks}; rounds={stats.Rounds}; engine_s={stats.Elapsed.TotalSeconds:F6}; wall_s={wall:F6}; clicks_s={stats.Clicks / stats.Elapsed.TotalSeconds:F2}; round_ms={stats.Elapsed.TotalMilliseconds / stats.Rounds:F4}; CPU_one_core_pct={cpu:F2}; CPU_machine_pct={cpu / Environment.ProcessorCount:F2}; {validation}");
        if (memory.Count > 0)
        {
            proc.Refresh();
            Console.WriteLine($"Private memory initial={baselineMemory / 1048576.0:F1}MiB, {string.Join(", ", memory)}, final={proc.PrivateMemorySize64 / 1048576.0:F1}MiB");
        }
        Console.WriteLine($"Guard rejected={blocked - blockedBefore}; other mouse events={otherMouseEvents - otherBefore}");
        Check(validation.StartsWith("missing=0; extra=0; mismatched=0"), label + " 次数、顺序、坐标与释放");
    }

    private string VerifyEvents(Settings settings, RunStats stats)
    {
        var cycle = settings.Points.Where(p => p.Enabled)
            .SelectMany(p => Enumerable.Repeat(p, p.Count * (p.Kind == ClickKind.双击 ? 2 : 1)))
            .ToArray();
        long expected = stats.Clicks * 2;
        int mismatch = 0, offsets = 0, maxOffset = 0, wrongButton = 0;
        string firstMismatch = "";
        for (int i = 0; i < Math.Min(events.Count, expected); i++)
        {
            var actual = events[i];
            var point = cycle[i / 2 % cycle.Length];
            int offset = Math.Max(Math.Abs(actual.X - point.X), Math.Abs(actual.Y - point.Y));
            maxOffset = Math.Max(maxOffset, offset);
            if (offset != 0) offsets++;
            if (actual.Button != (int)point.Button || actual.Down != (i % 2 == 0)) wrongButton++;
            if (offset != 0 || actual.Button != (int)point.Button || actual.Down != (i % 2 == 0))
            {
                if (mismatch++ == 0)
                    firstMismatch = $"; first mismatch at event {i}: expected {point.Name} ({point.X},{point.Y}), got {actual}";
            }
        }
        return $"missing={Math.Max(0, expected - events.Count)}; extra={Math.Max(0, events.Count - expected)}; mismatched={mismatch}; offset_events={offsets}; max_offset_px={maxOffset}; wrong_button_or_phase={wrongButton}{firstMismatch}";
    }

    private async Task TestControls(Settings baseline)
    {
        events.Clear();
        using (var countdown = new ClickRun(baseline with { Countdown = 3 }))
        {
            await Task.Delay(300);
            countdown.Stop();
            await countdown.Completion;
            Check(countdown.GetStats().Clicks == 0 && events.Count == 0, "倒计时期间可取消且未点击");
        }

        using var run = new ClickRun(baseline with { PointDelay = 5 });
        await Task.Delay(280);
        run.SetPaused(true);
        var paused = run.GetStats();
        await Task.Delay(180);
        Check(paused.Clicks > 0 && run.GetStats().Clicks == paused.Clicks && run.GetStats().Elapsed == paused.Elapsed,
            "无限循环暂停期间计数和运行计时冻结");
        run.SetPaused(false);
        await Task.Delay(210);
        run.Stop();
        await run.Completion;
        await Task.Delay(170);
        Check(run.GetStats().Clicks > paused.Clicks && VerifyEvents(baseline, run.GetStats()).StartsWith("missing=0; extra=0; mismatched=0"),
            "继续后保持原有循环次序，停止后按键已释放");

        Console.WriteLine("Begin global hotkey verification");
        var front = new MainWindow(configPath) { ShowActivated = false, Top = -2000, Left = -2000, Width = 720, Height = 550 };
        front.Show();
        await Task.Delay(400);
        var vm = (MainViewModel)front.DataContext;
        vm.Load(baseline with { Countdown = 3 });
        Check(Window.Activate(), "测试接收窗口保持前台");
        SetCursorPos(points[0].X, points[0].Y);
        vm.Rows.Clear();
        Key(0x75);
        await Task.Delay(180);
        Check(vm.Rows.Count == 1 && vm.Rows[0].X == points[0].X.ToString() &&
            vm.Rows[0].Y == points[0].Y.ToString(), "后台全局 F6 捕获坐标");
        vm.Load(baseline with { Countdown = 0 });
        events.Clear();
        Key(0x77);
        await Task.Delay(150);
        Check(vm.IsBusy && events.Count > 0, "后台全局 F8 启动零间隔轮询");
        Key(0x77);
        await Task.Delay(80);
        int before = events.Count;
        await Task.Delay(80);
        Check(vm.Status == "已暂停" && events.Count == before, "后台全局 F8 暂停");
        Key(0x77);
        await Task.Delay(100);
        Check(vm.Status == "运行中" && events.Count > before, "后台全局 F8 恢复");
        var stopWatch = Stopwatch.StartNew();
        Key(0x78);
        while (vm.IsBusy && stopWatch.ElapsedMilliseconds < 2000)
            await Task.Delay(1);
        stopWatch.Stop();
        await Task.Delay(150);
        Check(!vm.IsBusy && vm.Notice == "已停止" && events.Count % 2 == 0 &&
            (GetAsyncKeyState(1) & 0x8000) == 0 && (GetAsyncKeyState(2) & 0x8000) == 0 &&
            (GetAsyncKeyState(4) & 0x8000) == 0, "后台全局 F9 停止且所有鼠标键释放");
        int stopped = events.Count;
        await Task.Delay(200);
        Check(stopped == events.Count, "紧急停止后没有新事件");
        Console.WriteLine($"F9 dispatch to UI settled: {stopWatch.Elapsed.TotalMilliseconds:F2} ms; zero-delay button events={events.Count}");
        front.Close();
        await Task.Delay(180);
        Check(ConfigStore.Read(configPath).Points.SequenceEqual(baseline.Points), "关闭窗口自动保存当前配置");
        var reopened = new MainWindow(configPath) { ShowActivated = false, Top = -2000, Left = -2000 };
        reopened.Show();
        await Task.Delay(350);
        var reopenedModel = (MainViewModel)reopened.DataContext;
        Check(reopenedModel.Snapshot().Points.SequenceEqual(baseline.Points) && !reopenedModel.IsBusy, "重新打开窗口恢复配置且不会自动点击");
        reopened.Close();
        await Task.Delay(150);
        File.Delete(configPath);
        using var other = new HotkeyService(_ => { });
        using var conflicting = new HotkeyService(_ => { });
        await other.ApplyAsync(Hotkey.ParseAll(new Settings()));
        try
        {
            await conflicting.ApplyAsync(Hotkey.ParseAll(new Settings()));
            throw new Exception("FAIL 未检测到热键冲突");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("注册失败"))
        {
            Console.WriteLine("PASS 系统全局快捷键冲突检测");
        }
        await conflicting.ApplyAsync(Hotkey.ParseAll(new Settings
        {
            CaptureKey = "Ctrl+Alt+F6", ToggleKey = "Ctrl+Alt+F8", StopKey = "Ctrl+Alt+F9"
        }));
        Console.WriteLine("PASS 自定义组合快捷键注册");
    }

    private nint Receive(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is not (>= 0x201 and <= 0x209) || GetMessageExtraInfo() != 0x46504331)
            return 0;
        int index = (message - 0x201) / 3;
        bool down = (message - 0x201) % 3 != 1;
        int coordinates = unchecked((int)lParam);
        var point = new NativePoint { X = (short)coordinates, Y = (short)(coordinates >> 16) };
        ClientToScreen(hwnd, ref point);
        events.Add((point.X, point.Y, index, down));
        handled = true;
        return 0;
    }

    private nint Guard(int code, nuint message, nint data)
    {
        if (code >= 0 && message is >= 0x200 and <= 0x209)
        {
            var input = Marshal.PtrToStructure<MouseData>(data);
            if (input.Extra != 0x46504331)
                Interlocked.Increment(ref otherMouseEvents);
            if (message != 0x200 && input.Extra == 0x46504331 && (!armed ||
                GetAncestor(WindowFromPoint(input.Point), 2) != hwnd))
            {
                Interlocked.Increment(ref blocked);
                if (blocked <= 5)
                    Console.WriteLine($"Guard intercepted {message:X} at ({input.Point.X},{input.Point.Y})");
                return 1;
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + label);
        Console.WriteLine("PASS " + label);
    }

    private static void Key(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData { public NativePoint Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public NativePoint Point; public uint Private; }
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private delegate nint MouseHook(int code, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, MouseHook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint p);
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint p);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint p);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}



