using System.IO;
using System.Text.Json;
using TradeFix.Common;

namespace TradeFix.Agent.Services;

public static class AgentSettingsStore
{
    private static string FilePath => Path.Combine(AppPaths.DataRoot("Agent"), "settings.json");

    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AgentSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // a corrupt settings file should not prevent startup; fall back to defaults.
        }

        return new AgentSettings();
    }

    public static void Save(AgentSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
