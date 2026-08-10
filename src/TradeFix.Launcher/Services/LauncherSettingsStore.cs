using System.IO;
using System.Text.Json;
using TradeFix.Common;

namespace TradeFix.Launcher.Services;

/// <summary>Mirrors AgentSettingsStore/MasterSettings' JSON-file-under-AppPaths pattern.</summary>
public static class LauncherSettingsStore
{
    private static string FilePath => Path.Combine(AppPaths.DataRoot("Launcher"), "settings.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<LauncherSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // a corrupt settings file should not prevent startup; fall back to "unset" (re-asks).
        }

        return new LauncherSettings();
    }

    public static void Save(LauncherSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
