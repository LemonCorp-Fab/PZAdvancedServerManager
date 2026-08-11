using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Pz;

public interface IPzEnvironmentDiscovery
{
    PzInstallation DiscoverInstallation();
    IReadOnlyList<DiscoveredMod> DiscoverMods(PzInstallation installation, string targetVersion = PzasmConstants.DefaultTargetVersion);
}

public sealed class PzEnvironmentService(IPzEnvironmentDiscovery discovery)
{
    private readonly object _installationSync = new();
    private readonly object _modsSync = new();
    private PzInstallation? _installation;
    private IReadOnlyList<DiscoveredMod>? _mods;
    private string _targetVersion = string.Empty;

    public PzInstallation Installation
    {
        get { lock (_installationSync) return _installation ??= discovery.DiscoverInstallation(); }
    }

    public IReadOnlyList<DiscoveredMod> GetMods(string targetVersion = PzasmConstants.DefaultTargetVersion, bool refresh = false)
    {
        lock (_modsSync)
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
        lock (_installationSync)
        {
            _installation = null;
        }
        lock (_modsSync)
        {
            _mods = null;
            _targetVersion = string.Empty;
        }
    }
}
