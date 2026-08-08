using System.Globalization;
using System.Text.RegularExpressions;

namespace PZAdvancedServerManager.Core.Pz;

public enum StructuredSettingKind
{
    Boolean,
    Integer,
    Decimal,
    Text,
    LongText,
    Secret
}

public sealed record StructuredSettingOption(string Value, string Label);

public sealed record StructuredServerSetting(
    string Key,
    string Value,
    string Category,
    string Description,
    StructuredSettingKind Kind,
    double? Minimum,
    double? Maximum,
    IReadOnlyList<StructuredSettingOption> Options,
    bool IsSecret = false);

public static partial class StructuredServerSettings
{
    public static IReadOnlyList<StructuredServerSetting> ParseIni(string content)
    {
        var results = new List<StructuredServerSetting>();
        var comments = new List<string>();
        foreach (var raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                comments.Add(line.TrimStart('#', ' '));
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                if (line.Length > 0) comments.Clear();
                continue;
            }
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..];
            var description = string.Join(" ", comments.Where(x => x.Length > 0));
            comments.Clear();
            results.Add(Create(key, value, CategorizeIni(key), description));
        }
        return results.DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static StructuredServerSetting Create(string key, string value, string category, string description)
    {
        var secret = IsSecret(key);
        var options = ParseOptions(description);
        var (minimum, maximum) = ParseRange(description);
        var kind = secret ? StructuredSettingKind.Secret
            : bool.TryParse(value, out _) ? StructuredSettingKind.Boolean
            : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? StructuredSettingKind.Integer
            : double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? StructuredSettingKind.Decimal
            : value.Length > 100 || key.Contains("Message", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Logs", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Filter", StringComparison.OrdinalIgnoreCase) ? StructuredSettingKind.LongText
            : StructuredSettingKind.Text;
        return new StructuredServerSetting(key, value, category, description, kind, minimum, maximum, options, secret);
    }

    public static string ValidateAndFormat(StructuredServerSetting setting, string submitted, string currentValue)
    {
        submitted ??= string.Empty;
        if (setting.IsSecret && string.IsNullOrEmpty(submitted)) return currentValue;
        return setting.Kind switch
        {
            StructuredSettingKind.Boolean when bool.TryParse(submitted, out var boolean) => boolean.ToString().ToLowerInvariant(),
            StructuredSettingKind.Integer when long.TryParse(submitted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) && InRange(integer, setting) => integer.ToString(CultureInfo.InvariantCulture),
            StructuredSettingKind.Decimal when double.TryParse(submitted.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && InRange(number, setting) => number.ToString("0.################", CultureInfo.InvariantCulture),
            StructuredSettingKind.Boolean => throw new InvalidDataException($"{setting.Key} doit être true ou false."),
            StructuredSettingKind.Integer or StructuredSettingKind.Decimal => throw new InvalidDataException($"{setting.Key} doit être compris entre {setting.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "−∞"} et {setting.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "+∞"}."),
            _ => submitted.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
        };
    }

    public static string CategorizeIni(string key)
    {
        if (key is "PublicName" or "PublicDescription" or "Public" or "ServerWelcomeMessage" or "DisplayUserName" or "ShowFirstAndLastName" or "UsernameDisguises" or "HideDisguisedUserName") return "Identité & visibilité";
        if (key is "WorkshopItems" or "Mods" or "Map") return "Contenu & cartes";
        if (key.Contains("Password", StringComparison.OrdinalIgnoreCase) || key is "Open" or "MaxAccountsPerUser" or "AllowNonAsciiUsername" or "DropOffWhiteListAfterDeath") return "Accès & comptes";
        if (key.Contains("Port", StringComparison.OrdinalIgnoreCase) || key.Contains("Queue", StringComparison.OrdinalIgnoreCase) || key is "UPnP" or "PingLimit" or "MaxPacketsPerSecond" or "server_browser_announced_ip" or "DenyLoginOnOverloadedServer") return "Réseau & performances";
        if (key.StartsWith("PVP", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Safety", StringComparison.OrdinalIgnoreCase) || key.StartsWith("War", StringComparison.OrdinalIgnoreCase) || key is "ShowSafety" or "PlayerBumpPlayer") return "PvP & sécurité joueur";
        if (key.Contains("Safehouse", StringComparison.OrdinalIgnoreCase) || key.Contains("SafeHouse", StringComparison.OrdinalIgnoreCase) || key.Contains("Safezone", StringComparison.OrdinalIgnoreCase) || key.Contains("Sledgehammer", StringComparison.OrdinalIgnoreCase)) return "Abris & factions";
        if (key.StartsWith("Voice", StringComparison.OrdinalIgnoreCase) || key.Contains("Chat", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Discord", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Webhook", StringComparison.OrdinalIgnoreCase) || key.StartsWith("BadWord", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GoodWord", StringComparison.OrdinalIgnoreCase)) return "Chat, voix & Discord";
        if (key.StartsWith("AntiCheat", StringComparison.OrdinalIgnoreCase) || key is "SteamVAC" or "DoLuaChecksum" or "ClientCommandFilter" or "ClientActionLogs" or "PerkLogs") return "Sécurité & journalisation";
        if (key.StartsWith("Backup", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Save", StringComparison.OrdinalIgnoreCase) || key is "ResetID" or "ServerPlayerID" or "Seed" or "MultiplayerStatisticsPeriod") return "Sauvegardes & monde";
        if (key.Contains("Spawn", StringComparison.OrdinalIgnoreCase) || key.Contains("Respawn", StringComparison.OrdinalIgnoreCase) || key.Contains("Sleep", StringComparison.OrdinalIgnoreCase) || key is "MaxPlayers" or "PauseEmpty" or "NoFire" or "AllowCoop" or "Faction" or "FactionDaySurvivedToCreate" or "FactionPlayersRequiredForTag") return "Session & gameplay";
        if (key.StartsWith("DisableRadio", StringComparison.OrdinalIgnoreCase) || key.Contains("Admin", StringComparison.OrdinalIgnoreCase) || key.Contains("Scoreboard", StringComparison.OrdinalIgnoreCase) || key is "RCONPort" or "RCONPassword") return "Administration";
        return "Avancé & compatibilité";
    }

    private static bool IsSecret(string key) => key.Contains("Password", StringComparison.OrdinalIgnoreCase) || key.Contains("Token", StringComparison.OrdinalIgnoreCase) || key.Contains("Webhook", StringComparison.OrdinalIgnoreCase);

    private static (double? Minimum, double? Maximum) ParseRange(string description)
    {
        var match = RangeRegex().Match(description);
        if (!match.Success) return (null, null);
        return (ParseNumber(match.Groups[1].Value), ParseNumber(match.Groups[2].Value));
    }

    private static IReadOnlyList<StructuredSettingOption> ParseOptions(string description)
    {
        var results = new List<StructuredSettingOption>();
        foreach (Match match in OptionRegex().Matches(description))
        {
            var value = match.Groups[1].Value;
            var label = match.Groups[2].Value.Trim(' ', '.', ';');
            if (label.Length > 80) label = label[..80].TrimEnd() + "…";
            if (results.All(x => x.Value != value)) results.Add(new StructuredSettingOption(value, label));
        }
        return results;
    }

    private static double? ParseNumber(string value) => double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static bool InRange(double value, StructuredServerSetting setting) => (setting.Minimum is null || value >= setting.Minimum) && (setting.Maximum is null || value <= setting.Maximum);

    [GeneratedRegex(@"Minimum\s*=\s*(-?\d+(?:[.,]\d+)?)\s+Maximum\s*=\s*(-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"(?<!\S)(-?\d+)\s*=\s*(.*?)(?=\s+-?\d+\s*=|$)")]
    private static partial Regex OptionRegex();
}
