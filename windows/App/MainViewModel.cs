using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace FivePointClicker;

public sealed class PointRow(ClickPoint point)
{
    public string Name { get; } = point.Name;
    public string X { get; set; } = point.X.ToString(CultureInfo.InvariantCulture);
    public string Y { get; set; } = point.Y.ToString(CultureInfo.InvariantCulture);
    public ButtonKind Button { get; set; } = point.Button;
    public ClickKind Kind { get; set; } = point.Kind;
    public string Count { get; set; } = point.Count.ToString(CultureInfo.InvariantCulture);
    public bool Enabled { get; set; } = point.Enabled;

    internal ClickPoint ToPoint() => new(Name, MainViewModel.Number(X, $"{Name} 点 X 坐标"),
        MainViewModel.Number(Y, $"{Name} 点 Y 坐标"), Button, Kind,
        MainViewModel.Number(Count, $"{Name} 点执行次数"), Enabled);
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private RunMode mode;
    private string notice = "F6 取点 · F8 开始 / 暂停 · F9 紧急停止";
    private string status = "待机";
    private RunStats stats;
    private ClickRun? active;
    private bool paused, stopping, applying, hotkeysReady;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Refresh([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public ObservableCollection<PointRow> Rows { get; } = [];
    public RunMode[] Modes { get; } = Enum.GetValues<RunMode>();
    public string PointDelay { get; set; } = "5";
    public string RoundDelay { get; set; } = "10";
    public string Countdown { get; set; } = "3";
    public string Rounds { get; set; } = "100";
    public string Seconds { get; set; } = "60";
    public string CaptureKey { get; set; } = "F6";
    public string ToggleKey { get; set; } = "F8";
    public string StopKey { get; set; } = "F9";

    public RunMode Mode
    {
        get => mode;
        set
        {
            mode = value;
            Refresh();
            Refresh(nameof(UsesRounds));
            Refresh(nameof(UsesSeconds));
        }
    }

    public bool UsesRounds => Mode == RunMode.指定轮数;
    public bool UsesSeconds => Mode == RunMode.指定时间;
    public bool CanEdit => !IsBusy && !applying;
    public bool IsBusy => Volatile.Read(ref active) is not null;
    public bool CanPause => IsBusy && !stopping;
    public string PauseLabel => paused ? "继续" : "暂停";
    public string Status { get => status; private set { if (status != value) { status = value; Refresh(); } } }
    public string Notice { get => notice; set { notice = value; Refresh(); } }
    public string CurrentPoint => stats.Point is { } point ? $"{point.Name}  ({point.X}, {point.Y})" : "—";
    public string Clicks => $"{stats.Clicks:N0}";
    public string CompletedRounds => $"{stats.Rounds:N0}";
    public string Elapsed => stats.Elapsed.ToString(@"hh\:mm\:ss\.fff");
    public string Rate => $"{(stats.Elapsed.TotalSeconds > 0 ? stats.Clicks / stats.Elapsed.TotalSeconds : 0):N1} 次/秒";

    public void Load(Settings settings)
    {
        Rows.Clear();
        foreach (var point in settings.Points)
            Rows.Add(new PointRow(point));
        PointDelay = settings.PointDelay.ToString();
        RoundDelay = settings.RoundDelay.ToString();
        Countdown = settings.Countdown.ToString();
        Rounds = settings.Rounds.ToString();
        Seconds = settings.Seconds.ToString();
        Mode = settings.Mode;
        CaptureKey = settings.CaptureKey;
        ToggleKey = settings.ToggleKey;
        StopKey = settings.StopKey;
        Refresh("");
    }

    public Settings Snapshot()
    {
        var settings = new Settings
        {
            Points = Rows.Select(row => row.ToPoint()).ToArray(),
            PointDelay = Number(PointDelay, "相邻点间隔"),
            RoundDelay = Number(RoundDelay, "轮间隔"),
            Countdown = Number(Countdown, "倒计时"),
            Mode = Mode,
            Rounds = UsesRounds ? Number(Rounds, "轮数") : ValidOrDefault(Rounds, int.MaxValue, 100),
            Seconds = UsesSeconds ? Number(Seconds, "运行秒数") : ValidOrDefault(Seconds, 86400, 60),
            CaptureKey = CaptureKey.Trim(), ToggleKey = ToggleKey.Trim(), StopKey = StopKey.Trim()
        };
        settings.Validate();
        return settings;
    }

    private static int ValidOrDefault(string value, int maximum, int fallback) =>
        int.TryParse(value, out int number) && number >= 1 && number <= maximum ? number : fallback;

    internal static int Number(string value, string field) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
        ? number : throw new ArgumentException($"{field}必须是整数。");

    public PointRow Add(int x, int y)
    {
        if (Rows.Count >= Settings.MaxPoints)
            throw new InvalidOperationException("已添加 5 个位置。");
        string name = Enumerable.Range('A', Settings.MaxPoints).Select(n => ((char)n).ToString())
            .First(candidate => Rows.All(row => row.Name != candidate));
        var row = new PointRow(new ClickPoint(name, x, y));
        Rows.Add(row);
        return row;
    }

    internal void SetApplying(bool value)
    {
        applying = value;
        Refresh(nameof(CanEdit));
    }

    internal void SetHotkeysReady() => hotkeysReady = true;

    public async Task StartAsync(Settings settings)
    {
        if (!CanEdit)
            return;
        if (!hotkeysReady)
            throw new InvalidOperationException("请先应用一组可用的全局快捷键，以启用紧急停止。");

        var run = new ClickRun(settings);
        Volatile.Write(ref active, run);
        stats = default;
        paused = stopping = false;
        UpdateControls();
        Tick();
        string outcome = "运行完成";
        try { await run.Completion; }
        catch (Exception ex) { outcome = ex.Message; }
        finally
        {
            stats = run.GetStats();
            Volatile.Write(ref active, null);
            try { run.Dispose(); }
            catch when (run.Completion.IsFaulted) { }
            if (stopping || run.StopRequested)
                outcome = run.Completion.IsFaulted ? outcome : "已停止";
            paused = stopping = false;
            Status = "待机";
            Notice = outcome;
            UpdateControls();
            RefreshStats();
        }
    }

    public void TogglePause()
    {
        if (active is null || stopping || active.Completion.IsCompleted)
            return;
        paused = !paused;
        active.SetPaused(paused);
        Refresh(nameof(PauseLabel));
        Tick();
    }

    public ClickRun? SignalEmergency()
    {
        var run = Volatile.Read(ref active);
        run?.Stop();
        return run;
    }

    public void MarkEmergencyStop(ClickRun run)
    {
        if (ReferenceEquals(active, run))
            Stop();
    }

    public void Stop()
    {
        if (active is null || active.Completion.IsCompleted)
            return;
        stopping = true;
        active.Stop();
        Status = "正在停止";
        UpdateControls();
    }

    public Task Completion => Volatile.Read(ref active)?.Completion ?? Task.CompletedTask;

    public void Tick()
    {
        if (active is null)
            return;
        double countdown = (double)(active.CountdownDeadline - Stopwatch.GetTimestamp()) / Stopwatch.Frequency;
        Status = stopping ? "正在停止" : paused ? "已暂停" :
            countdown > 0 ? $"准备 · {Math.Ceiling(countdown)} 秒" : "运行中";
        stats = active.GetStats();
        RefreshStats();
    }

    private void UpdateControls()
    {
        Refresh(nameof(IsBusy));
        Refresh(nameof(CanEdit));
        Refresh(nameof(CanPause));
        Refresh(nameof(PauseLabel));
    }

    private void RefreshStats()
    {
        Refresh(nameof(CurrentPoint));
        Refresh(nameof(Clicks));
        Refresh(nameof(CompletedRounds));
        Refresh(nameof(Elapsed));
        Refresh(nameof(Rate));
    }
}

