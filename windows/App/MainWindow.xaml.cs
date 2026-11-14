using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FivePointClicker;

public partial class MainWindow : Window
{
    private readonly MainViewModel model = new();
    private readonly HotkeyService hotkeys;
    private readonly DispatcherTimer timer;
    private readonly string settingsPath;
    private Settings registered = new();
    private bool canClose, closing;

    public MainWindow() : this(ConfigStore.DefaultPath) { }

    internal MainWindow(string settingsPath)
    {
        this.settingsPath = settingsPath;
        InitializeComponent();
        DataContext = model;
        ButtonColumn.ItemsSource = Enum.GetValues<ButtonKind>();
        KindColumn.ItemsSource = Enum.GetValues<ClickKind>();
        hotkeys = new HotkeyService(HandleHotkey);
        timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(125)
        };
        timer.Tick += (_, _) => model.Tick();
        timer.Start();
        Loaded += InitializeSettings;
        Closing += WindowClosing;
    }

    private async void InitializeSettings(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializeSettings;
        model.SetApplying(true);
        try
        {
            Settings initial = new();
            string? loadError = null;
            try
            {
                if (File.Exists(settingsPath))
                    initial = ConfigStore.Read(settingsPath);
            }
            catch (Exception ex) { loadError = $"上次配置加载失败：{ex.Message}"; }
            model.Load(initial);
            try
            {
                await Register(initial);
                if (loadError is not null)
                    model.Notice = loadError;
            }
            catch (Exception ex) { model.Notice = ex.Message; }
        }
        finally { model.SetApplying(false); }
    }

    private async Task Register(Settings settings)
    {
        await hotkeys.ApplyAsync(Hotkey.ParseAll(settings));
        registered = settings;
        model.SetHotkeysReady();
        model.Notice = $"{settings.CaptureKey} 取点 · {settings.ToggleKey} 开始 / 暂停 · {settings.StopKey} 紧急停止";
    }

    private void HandleHotkey(int action)
    {
        if (action == 2)
        {
            if (model.SignalEmergency() is { } stopped)
                Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => model.MarkEmergencyStop(stopped)));
        }
        else if (action == 1)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closing)
                    return;
                if (model.IsBusy)
                    model.TogglePause();
                else
                    StartClick(this, new RoutedEventArgs());
            }));
        }
        else if (Native.GetCursorPos(out var point))
        {
            Dispatcher.BeginInvoke(new Action(() => Add(point.X, point.Y)));
        }
    }

    private void Commit()
    {
        PointsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PointsGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private async void StartClick(object sender, RoutedEventArgs e)
    {
        if (!model.CanEdit || closing)
            return;
        try
        {
            Commit();
            var settings = model.Snapshot();
            if (!Hotkey.ParseAll(settings).SequenceEqual(Hotkey.ParseAll(registered)))
                throw new InvalidOperationException("快捷键有修改，请先应用键位。");
            model.Notice = $"按 {registered.StopKey} 可紧急停止。";
            await model.StartAsync(settings);
        }
        catch (Exception ex) { model.Notice = ex.Message; }
    }

    private void PauseClick(object sender, RoutedEventArgs e) => model.TogglePause();
    private void StopClick(object sender, RoutedEventArgs e) => model.Stop();
    private void AddClick(object sender, RoutedEventArgs e)
    {
        if (Native.GetCursorPos(out var point))
            Add(point.X, point.Y);
        else
            model.Notice = "读取当前鼠标位置失败。";
    }

    private void Add(int x, int y)
    {
        if (!model.CanEdit || closing)
            return;
        try
        {
            Commit();
            PointsGrid.SelectedItem = model.Add(x, y);
            model.Notice = $"已添加 ({x}, {y})。双击表格单元格可修改坐标。";
        }
        catch (Exception ex) { model.Notice = ex.Message; }
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
        Commit();
        if (PointsGrid.SelectedItem is PointRow row)
            model.Rows.Remove(row);
    }

    private void ClearClick(object sender, RoutedEventArgs e)
    {
        Commit();
        model.Rows.Clear();
    }

    private void UpClick(object sender, RoutedEventArgs e) => Move(-1);
    private void DownClick(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int offset)
    {
        Commit();
        int current = PointsGrid.SelectedIndex;
        int target = current + offset;
        if (current >= 0 && target >= 0 && target < model.Rows.Count)
        {
            model.Rows.Move(current, target);
            PointsGrid.SelectedIndex = target;
        }
    }

    private async void HotkeysClick(object sender, RoutedEventArgs e)
    {
        model.SetApplying(true);
        try
        {
            await Register(registered with
            {
                CaptureKey = model.CaptureKey.Trim(),
                ToggleKey = model.ToggleKey.Trim(),
                StopKey = model.StopKey.Trim()
            });
        }
        catch (Exception ex) { model.Notice = ex.Message; }
        finally { model.SetApplying(false); }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Commit();
            var settings = model.Snapshot();
            var dialog = new SaveFileDialog { Filter = "JSON 配置 (*.json)|*.json", FileName = "clicker.json" };
            if (dialog.ShowDialog(this) != true)
                return;
            ConfigStore.Write(dialog.FileName, settings);
            model.Notice = $"已保存 {Path.GetFileName(dialog.FileName)}。";
        }
        catch (Exception ex) { model.Notice = ex.Message; }
    }

    private async void LoadClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON 配置 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        model.SetApplying(true);
        try
        {
            var settings = ConfigStore.Read(dialog.FileName);
            await Register(settings);
            model.Load(settings);
            model.Notice = $"已加载 {Path.GetFileName(dialog.FileName)}。";
        }
        catch (Exception ex) { model.Notice = ex.Message; }
        finally { model.SetApplying(false); }
    }

    private void DeleteConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog { Filter = "JSON 配置 (*.json)|*.json" };
            if (dialog.ShowDialog(this) != true ||
                MessageBox.Show(this, $"删除此配置文件？\n{dialog.FileName}", "删除配置",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            File.Delete(dialog.FileName);
            model.Notice = $"已删除 {Path.GetFileName(dialog.FileName)}。";
        }
        catch (Exception ex) { model.Notice = ex.Message; }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (canClose)
            return;
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        model.Stop();
        try { await model.Completion; }
        catch { /* Engine errors already appear as run status. */ }
        try
        {
            Commit();
            var settings = model.Snapshot();
            ConfigStore.Write(settingsPath, settings with
            {
                CaptureKey = registered.CaptureKey, ToggleKey = registered.ToggleKey, StopKey = registered.StopKey
            });
        }
        catch (Exception ex)
        {
            if (MessageBox.Show(this, $"无法保存当前设置。是否仍要退出？\n{ex.Message}",
                "保存设置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                closing = false;
                return;
            }
        }
        timer.Stop();
        hotkeys.Dispose();
        canClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}


