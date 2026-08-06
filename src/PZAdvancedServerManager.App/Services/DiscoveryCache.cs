using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Services;

public sealed class DiscoveryCache(PzDiscoveryService discovery)
{
    private readonly object _sync = new();
    private PzInstallation? _installation;
    private IReadOnlyList<DiscoveredMod>? _mods;
    private string _targetVersion = string.Empty;

    public PzInstallation Installation
    {
        get
        {
            lock (_sync) return _installation ??= discovery.DiscoverInstallation();
        }
    }

    public IReadOnlyList<DiscoveredMod> GetMods(string targetVersion = "42.20.2", bool refresh = false)
    {
        lock (_sync)
        {
            if (refresh || _mods is null || !_targetVersion.Equals(targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                _mods = discovery.DiscoverMods(Installation, targetVersion);
                _targetVersion = targetVersion;
            }
            return _mods;
        }
    }

    public void Invalidate()
    {
        lock (_sync) { _installation = null; _mods = null; _targetVersion = string.Empty; }
    }
}
