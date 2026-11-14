using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FivePointClicker;

public enum ButtonKind { 左键, 右键, 中键 }
public enum ClickKind { 单击, 双击 }
public enum RunMode { 无限循环, 指定轮数, 指定时间 }

public sealed record ClickPoint(
    string Name, int X, int Y, ButtonKind Button = ButtonKind.左键,
    ClickKind Kind = ClickKind.单击, int Count = 1, bool Enabled = true);

public sealed record Settings
{
    public const int MaxPoints = 5;

    public ClickPoint[] Points { get; init; } = [];
    public int PointDelay { get; init; } = 5;
    public int RoundDelay { get; init; } = 10;
    public int Countdown { get; init; } = 3;
    public RunMode Mode { get; init; }
    public int Rounds { get; init; } = 100;
    public int Seconds { get; init; } = 60;
    public string CaptureKey { get; init; } = "F6";
    public string ToggleKey { get; init; } = "F8";
    public string StopKey { get; init; } = "F9";

    public void Validate()
    {
        if (Points is null || Points.Length > MaxPoints ||
            Points.Any(p => p is null || p.Name is null || p.Name.Length != 1 ||
                p.Name[0] < 'A' || p.Name[0] >= 'A' + MaxPoints ||
                p.Count is < 1 or > 10000 || !Enum.IsDefined(p.Button) || !Enum.IsDefined(p.Kind)) ||
            Points.Select(p => p.Name).Distinct().Count() != Points.Length)
            throw new ArgumentException("坐标列表无效。编号须唯一，每个位置的次数须为 1–10000。");
        if (PointDelay is < 0 or > 1000 || RoundDelay is < 0 or > 5000 ||
            Countdown is < 0 or > 30 || Rounds < 1 || Seconds is < 1 or > 86400 || !Enum.IsDefined(Mode))
            throw new ArgumentException("检查间隔、倒计时、轮数和运行秒数的取值。");
        Hotkey.ParseAll(this);
    }
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FivePointClicker", "settings.json");

    public static Settings Read(string path)
    {
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Format)
            ?? throw new InvalidDataException("JSON 配置为空。");
        settings.Validate();
        return settings;
    }

    public static void Write(string path, Settings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Format));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
