using System.IO;
using System.Text.Json;
using TradeFix.Common;

namespace TradeFix.Master.Services;

public static class SettingsStore
{
    private static string FilePath => Path.Combine(AppPaths.DataRoot("Master"), "settings.json");

    public static MasterSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<MasterSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // fall through to defaults; a corrupt settings file should not prevent startup.
        }

        return new MasterSettings();
    }

    public static void Save(MasterSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
