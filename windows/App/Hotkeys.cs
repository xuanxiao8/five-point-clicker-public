using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace FivePointClicker;

internal readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static Hotkey[] ParseAll(Settings config)
    {
        Hotkey[] keys = [Parse(config.CaptureKey), Parse(config.ToggleKey), Parse(config.StopKey)];
        if (keys.Distinct().Count() != keys.Length)
            throw new ArgumentException("三个全局快捷键必须不同。");
        return keys;
    }

    private static Hotkey Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("快捷键不能为空。");
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            uint flag = part.ToUpperInvariant() switch
            {
                "ALT" => 1, "CTRL" => 2, "SHIFT" => 4, "WIN" => 8, _ => 0
            };
            if (flag == 0 || (modifiers & flag) != 0)
                throw new ArgumentException($"快捷键格式无效：{text}");
            modifiers |= flag;
        }

        var name = parts[^1].ToUpperInvariant();
        uint key = 0;
        if (name.Length == 1 && (name[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            key = name[0];
        else if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out int number) &&
            number is >= 1 and <= 24 and not 12)
            key = (uint)(0x6F + number);
        else if (Enum.TryParse<Key>(parts[^1], true, out var special) &&
            special is Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown
                or Key.Up or Key.Down or Key.Left or Key.Right or Key.Space)
            key = (uint)KeyInterop.VirtualKeyFromKey(special);

        if (key == 0 || (key is < 0x70 or > 0x87 && modifiers == 0))
            throw new ArgumentException($"快捷键无效：{text}。可用 F1–F24（F12 保留），或 Ctrl/Alt/Shift/Win 加字母、数字或导航键。");
        return new Hotkey(modifiers, key);
    }
}

internal sealed class HotkeyService : IDisposable
{
    private const uint HotkeyMessage = 0x0312;
    private const uint WorkMessage = 0x8001;
    private const uint QuitMessage = 0x0012;

    private readonly ConcurrentQueue<Action> pending = new();
    private readonly Dictionary<Hotkey, int> registrations = new();
    private readonly Dictionary<int, int> actions = new();
    private readonly Action<int> onHotkey;
    private readonly Thread worker;
    private readonly uint threadId;
    private int nextId = 1;

    public HotkeyService(Action<int> handler)
    {
        onHotkey = handler;
        var ready = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = new Thread(() => Pump(ready)) { IsBackground = true, Name = "全局快捷键" };
        worker.Start();
        threadId = ready.Task.GetAwaiter().GetResult();
    }

    public Task ApplyAsync(Hotkey[] keys)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Enqueue(() =>
        {
            try { Apply(keys); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        if (!Native.PostThreadMessageW(threadId, WorkMessage, 0, 0))
            completion.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
        return completion.Task;
    }

    private void Apply(Hotkey[] keys)
    {
        var added = new List<Hotkey>();
        try
        {
            foreach (var key in keys.Reverse()) // Register emergency stop first.
            {
                if (registrations.ContainsKey(key))
                    continue;

                int id = nextId;
                do
                {
                    id = nextId;
                    nextId = nextId == 0xBFFF ? 1 : nextId + 1;
                } while (registrations.ContainsValue(id));

                if (!Native.RegisterHotKey(0, id, key.Modifiers | 0x4000, key.VirtualKey))
                {
                    int error = Marshal.GetLastWin32Error();
                    int index = Array.IndexOf(keys, key);
                    var role = new[] { "取点", "开始/暂停", "紧急停止" }[index];
                    throw new InvalidOperationException($"{role}快捷键注册失败（Windows 错误 {error}）。请检查是否已被系统或其他程序占用。");
                }

                registrations.Add(key, id);
                added.Add(key);
            }
        }
        catch
        {
            foreach (var key in added)
                Remove(key);
            throw;
        }

        foreach (var key in registrations.Keys.Except(keys).ToArray())
            Remove(key);
        actions.Clear();
        for (int index = 0; index < keys.Length; index++)
            actions.Add(registrations[keys[index]], index);
    }

    private void Remove(Hotkey key)
    {
        Native.UnregisterHotKey(0, registrations[key]);
        registrations.Remove(key);
    }

    private void Pump(TaskCompletionSource<uint> ready)
    {
        Native.PeekMessageW(out _, 0, 0, 0, 0);
        ready.SetResult(Native.GetCurrentThreadId());
        try
        {
            while (Native.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == HotkeyMessage && actions.TryGetValue((int)message.WParam, out int action))
                    onHotkey(action);
                else if (message.Id == WorkMessage)
                    while (pending.TryDequeue(out var job))
                        job();
            }
        }
        finally
        {
            foreach (int id in registrations.Values)
                Native.UnregisterHotKey(0, id);
        }
    }

    public void Dispose()
    {
        Native.PostThreadMessageW(threadId, QuitMessage, 0, 0);
        worker.Join();
    }
}
