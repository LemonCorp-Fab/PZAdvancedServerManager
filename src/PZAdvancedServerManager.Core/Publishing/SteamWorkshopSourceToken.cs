using System.Globalization;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Publishing;

public static class SteamWorkshopSourceToken
{
    private const string Prefix = "steam-workshop:";

    public static string Create(SteamWorkshopItemState state) =>
        $"{Prefix}{state.WorkshopId}:{state.ManifestId}:{state.TimeUpdated}";

    public static bool MatchesRemote(PackageModReference reference, ulong workshopId, long remoteUpdateTime)
    {
        if (!Directory.Exists(reference.PinnedSourceRoot) || string.IsNullOrWhiteSpace(reference.PinnedContentHash)) return false;
        if (!reference.SourceUpdateToken.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var values = reference.SourceUpdateToken[Prefix.Length..].Split(':', 3, StringSplitOptions.None);
        return values.Length == 3 &&
               ulong.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var tokenWorkshopId) &&
               tokenWorkshopId == workshopId &&
               !string.IsNullOrWhiteSpace(values[1]) &&
               long.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokenUpdateTime) &&
               tokenUpdateTime == remoteUpdateTime;
    }
}
