using System.Text.Json;
using System.Text.Json.Serialization;

namespace Headroom.Core.Settings;

/// <summary>
/// User preferences, stored as JSON under %LOCALAPPDATA%\Headroom.
/// </summary>
public sealed class AppSettings
{
    public const int DefaultRefreshIntervalMinutes = 5;

    /// <summary>Choices offered for automatic refresh; 0 means manual refresh only.</summary>
    public static IReadOnlyList<int> RefreshIntervalChoices { get; } = [0, 1, 2, 5, 10, 15, 30, 60];

    /// <summary>Ids of providers the user turned off; they are hidden and never polled.</summary>
    public List<string> DisabledProviders { get; set; } = [];

    /// <summary>Minutes between automatic refreshes; 0 turns automatic refresh off.</summary>
    public int RefreshIntervalMinutes { get; set; } = DefaultRefreshIntervalMinutes;

    /// <summary>Refresh data older than two minutes when the panel is opened.</summary>
    public bool RefreshOnOpen { get; set; } = true;

    [JsonIgnore]
    public TimeSpan? RefreshInterval => RefreshIntervalMinutes > 0 ? TimeSpan.FromMinutes(RefreshIntervalMinutes) : null;

    public bool IsProviderEnabled(string id) => !DisabledProviders.Contains(id, StringComparer.OrdinalIgnoreCase);

    public void SetProviderEnabled(string id, bool enabled)
    {
        DisabledProviders.RemoveAll(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            DisabledProviders.Add(id);
        }
    }

    public static AppSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        settings.DisabledProviders ??= [];
        if (settings.RefreshIntervalMinutes < 0)
        {
            settings.RefreshIntervalMinutes = DefaultRefreshIntervalMinutes;
        }

        return settings;
    }

    public string ToJson() => JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings);
}

/// <summary>Loads and saves <see cref="AppSettings"/>; a missing or corrupt file yields the defaults.</summary>
public sealed class SettingsStore(string path)
{
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Headroom", "settings.json");

    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            return File.Exists(Path) ? AppSettings.Parse(File.ReadAllText(Path)) : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Saves the settings; returns false if the file could not be written.</summary>
    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // Write to a temporary file first so a crash never leaves a truncated settings file.
            var temp = Path + ".tmp";
            File.WriteAllText(temp, settings.ToJson());
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
