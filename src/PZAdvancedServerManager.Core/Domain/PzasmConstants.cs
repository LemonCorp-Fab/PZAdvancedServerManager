namespace PZAdvancedServerManager.Core.Domain;

public static class PzasmConstants
{
    public const string ProductName = "PZ Advanced Server Manager";
    public const string ProductShortName = "PZASM";
    public const string DataVendor = "LemonCorp";
    public const string DataFolder = "PZAdvancedServerManager";
    public const string ProjectFileExtension = ".pzasm.json";
    public const string ProjectZomboidSteamAppId = "108600";
    public const string ProjectZomboidDedicatedServerSteamAppId = "380870";
    public const string DefaultTargetVersion = "42.20.2";
    public const int CurrentProjectSchemaVersion = 4;
    public static readonly TimeSpan AutomationPollInterval = TimeSpan.FromSeconds(30);
}
