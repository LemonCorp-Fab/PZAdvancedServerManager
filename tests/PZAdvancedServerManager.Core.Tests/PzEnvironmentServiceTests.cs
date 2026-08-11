using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class PzEnvironmentServiceTests
{
    [Fact]
    public async Task InstallationLookupDoesNotWaitForModDiscovery()
    {
        var discovery = new BlockingDiscovery();
        var environment = new PzEnvironmentService(discovery);
        var modDiscovery = Task.Run(() => environment.GetMods());

        Assert.True(discovery.ModDiscoveryStarted.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            var installationLookup = Task.Run(() => environment.Installation);
            var completed = await Task.WhenAny(installationLookup, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(installationLookup, completed);
            Assert.Same(discovery.Installation, await installationLookup);
        }
        finally
        {
            discovery.AllowModDiscoveryToFinish.Set();
            await modDiscovery;
        }
    }

    private sealed class BlockingDiscovery : IPzEnvironmentDiscovery
    {
        public ManualResetEventSlim ModDiscoveryStarted { get; } = new(false);
        public ManualResetEventSlim AllowModDiscoveryToFinish { get; } = new(false);
        public PzInstallation Installation { get; } = new() { UserZomboidRoot = "test" };

        public PzInstallation DiscoverInstallation() => Installation;

        public IReadOnlyList<DiscoveredMod> DiscoverMods(PzInstallation installation, string targetVersion = PzasmConstants.DefaultTargetVersion)
        {
            ModDiscoveryStarted.Set();
            AllowModDiscoveryToFinish.Wait(TimeSpan.FromSeconds(10));
            return [];
        }
    }
}
