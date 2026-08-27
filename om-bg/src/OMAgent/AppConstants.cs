namespace OMAgent;

public static class OmConstants
{
    public const string ServiceName = "OMAgent";
    public const string DisplayName = "OM Background Worker";
    public const string Description = "Runs a persistent background script that starts with Windows and reports its status.";
    public const string DataRoot = @"C:\ProgramData\OMAgent";
    public const string LogsDir = @"C:\ProgramData\OMAgent\Logs";
    public const string ScriptPath = @"C:\ProgramData\OMAgent\worker.ps1";
    public const string ConfigPath = @"C:\ProgramData\OMAgent\config.json";
    public const string StatusPath = @"C:\ProgramData\OMAgent\status.json";
    public const string AppControlPath = @"C:\ProgramData\OMAgent\appcontrol.json";
}
