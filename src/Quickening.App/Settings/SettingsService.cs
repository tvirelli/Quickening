using System.Text.Json;

namespace Quickening.App.Settings;

/// <summary>
/// Loads/saves AppSettings as a small JSON file in the app's own data
/// directory (the same directory App.xaml.cs already uses for logs/the
/// SQLite store) - deliberately not Windows.Storage.ApplicationData, which
/// requires package identity this unpackaged app doesn't have.
/// </summary>
public sealed class SettingsService
{
    private readonly string _filePath;

    public SettingsService(string filePath)
    {
        _filePath = filePath;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or unreadable settings file just means falling back
            // to defaults for this run - not worth crashing startup over.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort - a failed save just means this preference change
            // doesn't survive a restart this time, not worth surfacing an
            // error for a Settings toggle.
        }
    }
}
