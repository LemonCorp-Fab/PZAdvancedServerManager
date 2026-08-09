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
    public const int SteamWorkshopTitleMaximumUtf8Bytes = 128;
    public const int SteamWorkshopDescriptionMaximumUtf8Bytes = 8000;
    public const int GeneratedWorkshopDescriptionMaximumUtf8Bytes = 7900;
    public const int CurrentProjectSchemaVersion = 5;
    public static readonly TimeSpan AutomationPollInterval = TimeSpan.FromSeconds(30);
}
