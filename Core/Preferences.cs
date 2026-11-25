using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QuotaMonitor;

internal enum DisplayMode { Bars, Rings, CompactPill }
internal enum ThemePreference { System, Light, Dark }
internal enum WidgetSurfaceStyle { Themed, HudGlass, CustomColor }
internal enum WidgetTextContrast { Auto, Light, Dark }
internal enum WidgetLayerMode { DesktopPinned, Normal, AlwaysOnTop }

internal sealed class Preferences
{
    public const int DefaultWidgetWidth = 228, DefaultWidgetHeight = 116;
    public const int MinWidgetWidth = 174, MinWidgetHeight = 40;
    public const int MaxWidgetWidth = 1000, MaxWidgetHeight = 800;
    private const int CurrentVersion = 5;
    private const string DefaultBackground = "#DFEAFC";
    private static readonly string[] DefaultOrder =
        ["chatgpt.7d", "chatgpt.5h", "ag.gemini.7d", "ag.claude.7d", "grok.7d"];

    public int AppearanceVersion { get; set; } = CurrentVersion;
    public DisplayMode Mode { get; set; }
    public ThemePreference ThemeMode { get; set; }
    public WidgetSurfaceStyle SurfaceStyle { get; set; }
    public WidgetLayerMode LayerMode { get; set; }
    public string CustomBackground { get; set; } = DefaultBackground;
    public WidgetTextContrast TextContrast { get; set; }
    public int Transparency { get; set; }
    public double WidgetScale { get; set; } = 1.0;
    public bool ShowCountdownOnWidget { get; set; } = true;
    public bool WidgetVisible { get; set; } = true;
    public bool WidgetLocked { get; set; }
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
    public int WidgetWidth { get; set; } = DefaultWidgetWidth;
    public int WidgetHeight { get; set; } = DefaultWidgetHeight;
    public List<string> WidgetBucketOrder { get; set; } = [.. DefaultOrder];
    public HashSet<string> HiddenBuckets { get; set; } = ["chatgpt.5h"];
    public int PollingIntervalMinutes { get; set; } = 5;
    public bool LowEnabled { get; set; } = true;
    public int LowThreshold { get; set; } = 20;
    public int CriticalThreshold { get; set; } = 10;
    public Dictionary<string, int> PerBucketLowThresholds { get; set; } = new();
    public bool FastEnabled { get; set; } = true;
    public int FastThreshold { get; set; } = 15;
    public bool ResetRecoveryEnabled { get; set; } = true;
    public bool QuietHoursEnabled { get; set; }
    public string QuietStart { get; set; } = "23:00";
    public string QuietEnd { get; set; } = "08:00";
    public bool GrokConnected { get; set; }

    internal static string BucketKey(WidgetItem item) => item switch
    {
        WidgetItem.Work => "chatgpt.7d", WidgetItem.Grok => "grok.7d",
        WidgetItem.AntigravityGemini => "ag.gemini.7d", WidgetItem.AntigravityClaude => "ag.claude.7d",
        _ => throw new ArgumentOutOfRangeException(nameof(item))
    };

    public static Preferences Load(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new();
            var settings = root.Deserialize<Preferences>()!;
            bool migrate = !root.TryGetProperty(nameof(AppearanceVersion), out _) || settings.AppearanceVersion < CurrentVersion;
            if (migrate) settings.Migrate(root);
            settings.Normalize();
            if (migrate || root.TryGetProperty("GeminiEnabled", out _) || root.TryGetProperty("GeminiHasReadUsage", out _))
            {
                // Keep the migrated values in memory even if the settings directory is read-only.
                try { settings.Save(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning(ex.Message); }
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private void Migrate(JsonElement root)
    {
        AppearanceVersion = CurrentVersion;
        ThemeMode = ThemePreference.System;
        LayerMode = WidgetLayerMode.DesktopPinned;
        if (root.TryGetProperty("Background", out var color) && color.ValueKind == JsonValueKind.String)
            CustomBackground = color.GetString() ?? DefaultBackground;
        if (!ValidColor(CustomBackground)) CustomBackground = DefaultBackground;
        SurfaceStyle = CustomBackground.Equals(DefaultBackground, StringComparison.OrdinalIgnoreCase)
            ? WidgetSurfaceStyle.Themed : WidgetSurfaceStyle.CustomColor;
        HiddenBuckets = ["chatgpt.5h"];
        if (root.TryGetProperty("HiddenWidgetItems", out var hidden) && hidden.ValueKind == JsonValueKind.Array)
            foreach (var item in hidden.EnumerateArray())
                if (Enum.TryParse<WidgetItem>(item.ToString(), true, out var kind) && Enum.IsDefined(kind))
                    HiddenBuckets.Add(BucketKey(kind));
    }

    private static bool ValidColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);

    private static bool ValidTime(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private void Normalize()
    {
        if (!Enum.IsDefined(Mode)) Mode = DisplayMode.Bars;
        if (!Enum.IsDefined(ThemeMode)) ThemeMode = ThemePreference.System;
        if (!Enum.IsDefined(SurfaceStyle)) SurfaceStyle = WidgetSurfaceStyle.Themed;
        if (!Enum.IsDefined(LayerMode)) LayerMode = WidgetLayerMode.DesktopPinned;
        if (!ValidColor(CustomBackground)) CustomBackground = DefaultBackground;
        if (!Enum.IsDefined(TextContrast)) TextContrast = WidgetTextContrast.Auto;
        Transparency = Math.Clamp(Transparency, 0, 100);
        if (WidgetScale is not (0.9 or 1.0 or 1.15 or 1.3)) WidgetScale = 1.0;
        WidgetWidth = Math.Clamp(WidgetWidth, MinWidgetWidth, MaxWidgetWidth);
        WidgetHeight = Math.Clamp(WidgetHeight, MinWidgetHeight, MaxWidgetHeight);
        LowThreshold = Math.Clamp(LowThreshold, 1, 100);
        CriticalThreshold = Math.Clamp(CriticalThreshold, 1, 50);
        FastThreshold = Math.Clamp(FastThreshold, 1, 100);
        if (PollingIntervalMinutes is not (1 or 3 or 5 or 10 or 15)) PollingIntervalMinutes = 5;
        if (!ValidTime(QuietStart)) QuietStart = "23:00";
        if (!ValidTime(QuietEnd)) QuietEnd = "08:00";
        static bool KeepBucket(string key) => !string.IsNullOrWhiteSpace(key) && !key.StartsWith("gemini.", StringComparison.Ordinal);
        HiddenBuckets ??= ["chatgpt.5h"];
        HiddenBuckets.RemoveWhere(k => !KeepBucket(k));
        WidgetBucketOrder = (WidgetBucketOrder ?? []).Where(KeepBucket).Union(DefaultOrder).ToList();
        PerBucketLowThresholds = (PerBucketLowThresholds ?? new()).Where(p => KeepBucket(p.Key))
            .ToDictionary(p => p.Key, p => Math.Clamp(p.Value, 1, 100));
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
