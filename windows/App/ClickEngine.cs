using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FivePointClicker;

public readonly record struct RunStats(long Clicks, long Rounds, ClickPoint? Point, TimeSpan Elapsed);

public sealed class ClickRun : IDisposable
{
    private const int MaxActionsPerBatch = 10;
    private readonly CancellationTokenSource cancel = new();
    private readonly ManualResetEvent resume = new(true);
    private readonly AutoResetEvent scheduleChanged = new(false);
    private readonly object sync = new();
    private readonly object lifetime = new();
    private volatile bool stopRequested;
    private readonly Settings settings;
    private readonly ClickPoint[] points;
    private readonly Desktop desktop;
    private readonly Native.Input[][] sequences;
    private long clicks, rounds, activeTicks, segmentStart;
    private ClickPoint? currentPoint;
    private bool paused, disposed;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => finished.Task;
    public bool StopRequested => stopRequested;
    public long CountdownDeadline { get; }

    public ClickRun(Settings settings)
    {
        settings.Validate();
        this.settings = settings;
        points = settings.Points.Where(p => p.Enabled).ToArray();
        if (points.Length == 0)
            throw new ArgumentException("至少启用一个点击位置。");
        desktop = Desktop.Current;
        sequences = points.Select(BuildAction).ToArray();
        CountdownDeadline = Stopwatch.GetTimestamp() + settings.Countdown * Stopwatch.Frequency;
        new Thread(Work) { IsBackground = true, Name = "五点轮询引擎" }.Start();
    }

    public void SetPaused(bool value)
    {
        lock (lifetime)
        lock (sync)
        {
            if (disposed || cancel.IsCancellationRequested)
                return;
            paused = value;
            if (paused)
            {
                resume.Reset();
                StopClock();
            }
            else
                resume.Set();
            scheduleChanged.Set();
        }
    }

    public void Stop()
    {
        lock (lifetime)
        {
            if (!disposed && !Completion.IsCompleted)
            {
                stopRequested = true;
                cancel.Cancel();
            }
        }
    }

    public RunStats GetStats()
    {
        lock (sync)
        {
            long elapsed = activeTicks + (segmentStart == 0 ? 0 : Stopwatch.GetTimestamp() - segmentStart);
            return new RunStats(clicks, rounds, currentPoint,
                TimeSpan.FromSeconds((double)elapsed / Stopwatch.Frequency));
        }
    }

    private void StopClock()
    {
        if (segmentStart == 0)
            return;
        activeTicks += Stopwatch.GetTimestamp() - segmentStart;
        segmentStart = 0;
    }

    private Native.Input[] BuildAction(ClickPoint point)
    {
        var move = desktop.Move(point);
        var (down, up) = Native.Flags(point.Button);
        var pressed = new Native.Input(0, 0, down);
        var released = new Native.Input(0, 0, up);
        return point.Kind == ClickKind.双击
            ? [move, pressed, released, pressed, released]
            : [move, pressed, released];
    }

    private void Work()
    {
        Exception? failure = null;
        try { Execute(cancel.Token); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (sync)
                StopClock();
            if (failure is null)
                finished.SetResult();
            else
                finished.SetException(failure);
        }
    }

    private void Execute(CancellationToken token)
    {
        using var timer = new PrecisionWait(token, scheduleChanged);
        var gate = new[] { token.WaitHandle, resume };
        double limit = settings.Mode == RunMode.指定时间 ? settings.Seconds : double.PositiveInfinity;
        while (Stopwatch.GetTimestamp() < CountdownDeadline)
            timer.Delay(ToMilliseconds(CountdownDeadline - Stopwatch.GetTimestamp()));
        token.ThrowIfCancellationRequested();

        var batch = new Native.Input[MaxActionsPerBatch * 5];
        int index = 0, repetition = 0;
        long recheckDesktop = 0;
        double due = 0;
        while (true)
        {
            WaitHandle.WaitAny(gate);
            token.ThrowIfCancellationRequested();

            double elapsed;
            lock (sync)
            {
                if (paused)
                    continue;
                if (segmentStart == 0)
                    segmentStart = Stopwatch.GetTimestamp();
                elapsed = GetStats().Elapsed.TotalSeconds;
            }
            if (elapsed >= limit)
                return;
            if (due > elapsed)
            {
                timer.Delay((Math.Min(due, limit) - elapsed) * 1000);
                continue;
            }

            long now = Stopwatch.GetTimestamp();
            if (now >= recheckDesktop)
            {
                if (Desktop.Current != desktop)
                    throw new InvalidOperationException("显示器布局已变化，请检查坐标后重新启动。");
                foreach (var point in points)
                    desktop.Move(point);
                recheckDesktop = now + Stopwatch.Frequency / 4;
            }

            int oldIndex = index, oldRepetition = repetition;
            int eventCount = 0, clickCount = 0, delay = 0;
            bool roundEnded = false;
            ClickPoint pointSent = points[index];
            for (int action = 0; action < MaxActionsPerBatch; action++)
            {
                var sequence = sequences[index];
                pointSent = points[index];
                Array.Copy(sequence, 0, batch, eventCount, sequence.Length);
                eventCount += sequence.Length;
                clickCount += pointSent.Kind == ClickKind.双击 ? 2 : 1;

                if (++repetition == pointSent.Count)
                {
                    repetition = 0;
                    index++;
                    if (index == points.Length)
                    {
                        index = 0;
                        roundEnded = true;
                        delay = settings.RoundDelay;
                        break;
                    }
                    delay = settings.PointDelay;
                    if (delay > 0)
                        break;
                }
            }

            // Only commit progress once a complete batch has been inserted.
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (paused)
                {
                    index = oldIndex;
                    repetition = oldRepetition;
                    continue;
                }
                Send(batch, eventCount);
                clicks += clickCount;
                currentPoint = pointSent;
                if (roundEnded)
                    rounds++;
                if (roundEnded && settings.Mode == RunMode.指定轮数 && rounds >= settings.Rounds)
                    return;
                due = GetStats().Elapsed.TotalSeconds + delay / 1000.0;
            }

            if (delay == 0)
                Thread.Yield();
        }
    }

    private static double ToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static void Send(Native.Input[] batch, int count)
    {
        uint sent = Native.SendInput((uint)count, batch, Native.InputSize);
        if (sent == count)
            return;

        int error = Marshal.GetLastWin32Error();
        uint pressed = 0;
        for (int i = 0; i < sent; i++)
        {
            uint flag = batch[i].Flags;
            pressed |= (flag << 1) & Native.UpFlags;
            pressed &= ~(flag & Native.UpFlags);
        }
        bool released = true;
        if (pressed != 0)
        {
            Native.Input[] release = [new(0, 0, pressed)];
            released = false;
            for (int attempt = 0; attempt < 3 && !released; attempt++)
                released = Native.SendInput(1, release, Native.InputSize) == 1;
        }
        var suffix = released ? "" : " 自动补发释放失败，请手动按下并释放相应鼠标按键。";
        throw new InvalidOperationException($"SendInput 仅提交 {sent}/{count} 个事件（Windows 错误 {error}）。请检查桌面锁定及目标权限。{suffix}");
    }

    public void Dispose()
    {
        Stop();
        try { Completion.GetAwaiter().GetResult(); }
        finally
        {
            lock (lifetime)
            lock (sync)
            {
                if (!disposed)
                {
                    disposed = true;
                    scheduleChanged.Dispose();
                    resume.Dispose();
                    cancel.Dispose();
                }
            }
        }
    }
}

internal sealed class PrecisionWait : IDisposable
{
    private readonly CancellationToken token;
    private readonly WaitHandle[] signals;
    private readonly EventWaitHandle? timer;

    public PrecisionWait(CancellationToken token, WaitHandle changed)
    {
        this.token = token;
        var handle = Native.CreateWaitableTimerExW(0, 0, 2, 0x100002);
        if (!handle.IsInvalid)
            timer = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
        else
            handle.Dispose();
        signals = timer is null ? [token.WaitHandle, changed] : [token.WaitHandle, changed, timer];
    }

    public void Delay(double milliseconds)
    {
        if (milliseconds <= 0)
            return;
        long due = -(long)Math.Ceiling(milliseconds * 10000);
        if (timer is not null && Native.SetWaitableTimer(timer.SafeWaitHandle, ref due, 0, 0, 0, false))
            WaitHandle.WaitAny(signals);
        else
            WaitHandle.WaitAny([signals[0], signals[1]], (int)Math.Ceiling(milliseconds));
        token.ThrowIfCancellationRequested();
    }

    public void Dispose() => timer?.Dispose();
}


