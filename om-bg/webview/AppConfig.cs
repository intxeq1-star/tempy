using System.Text.Json;

namespace OMAgent;

public sealed class AppConfig
{
    public string ScriptPath { get; set; } = OmConstants.ScriptPath;
    public int RunIntervalSeconds { get; set; } = 60;
    public int ScriptTimeoutSeconds { get; set; } = 0;
    public int HeartbeatSeconds { get; set; } = 15;
    public string LogsDir { get; set; } = OmConstants.LogsDir;
    public string AgentVersion { get; set; } = "1.0.0";

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(OmConstants.ConfigPath))
            {
                var json = File.ReadAllText(OmConstants.ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg is not null) return cfg;
            }
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(OmConstants.DataRoot);
            File.WriteAllText(OmConstants.ConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
