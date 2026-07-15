using System.Text.Json;

namespace CodexMonitorV2;

internal sealed class SceneSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexMonitorV2",
        "scene-settings.json");

    public SceneViewSettings Day { get; set; } = SceneViewSettings.CreateDayDefault();
    public SceneViewSettings Night { get; set; } = SceneViewSettings.CreateNightDefault();

    public static SceneSettingsStore Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new SceneSettingsStore();
            SceneSettingsStore? store = JsonSerializer.Deserialize<SceneSettingsStore>(File.ReadAllText(SettingsPath));
            if (store is null) return new SceneSettingsStore();
            store.Day ??= SceneViewSettings.CreateDayDefault();
            store.Night ??= SceneViewSettings.CreateNightDefault();
            store.Day.Normalize(SceneViewSettings.CreateDayDefault());
            store.Night.Normalize(SceneViewSettings.CreateNightDefault());
            return store;
        }
        catch
        {
            return new SceneSettingsStore();
        }
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}

internal sealed class SceneViewSettings
{
    public bool UseBackgroundImage { get; set; } = true;
    public string? ImagePath { get; set; }
    public double Zoom { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }

    public static SceneViewSettings CreateDayDefault() => new()
    {
        UseBackgroundImage = true,
        Zoom = 1.00,
        PositionX = 0.20,
        PositionY = 0.0
    };

    public static SceneViewSettings CreateNightDefault() => new()
    {
        UseBackgroundImage = true,
        Zoom = 1.00,
        PositionX = 0.20,
        PositionY = 0.0
    };

    public void Reset(bool isDay)
    {
        SceneViewSettings defaults = isDay ? CreateDayDefault() : CreateNightDefault();
        UseBackgroundImage = true;
        ImagePath = null;
        Zoom = defaults.Zoom;
        PositionX = defaults.PositionX;
        PositionY = defaults.PositionY;
    }

    public void Normalize(SceneViewSettings defaults)
    {
        if (!double.IsFinite(Zoom)) Zoom = defaults.Zoom;
        if (!double.IsFinite(PositionX)) PositionX = defaults.PositionX;
        if (!double.IsFinite(PositionY)) PositionY = defaults.PositionY;
        Zoom = Math.Clamp(Zoom, 1.0, 1.6);
        PositionX = Math.Clamp(PositionX, -0.35, 0.35);
        PositionY = Math.Clamp(PositionY, -0.28, 0.28);
        if (!string.IsNullOrWhiteSpace(ImagePath) && !File.Exists(ImagePath)) ImagePath = null;
    }
}
