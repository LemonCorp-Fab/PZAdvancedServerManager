using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Publishing;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class SteamWorkshopPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-steam-paths-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveItemRootFindsLinuxRuntimeHomeCache()
    {
        var paths = new ApplicationPaths(_root);
        var itemRoot = Path.Combine(paths.RuntimeHomeRoot, "Steam", "steamapps", "workshop", "content", "108600", "3780683663");
        Directory.CreateDirectory(itemRoot);

        var resolved = paths.ResolveSteamWorkshopItemRoot(paths.SteamCmdExecutable, 3780683663);

        Assert.True(string.Equals(Path.GetFullPath(itemRoot), resolved, PathComparison));
    }

    [Fact]
    public void ResolveItemRootRetainsExecutableAdjacentCacheCompatibility()
    {
        var paths = new ApplicationPaths(_root);
        var itemRoot = Path.Combine(paths.SteamCmdRoot, "steamapps", "workshop", "content", "108600", "42");
        Directory.CreateDirectory(itemRoot);

        var resolved = paths.ResolveSteamWorkshopItemRoot(paths.SteamCmdExecutable, 42);

        Assert.True(string.Equals(Path.GetFullPath(itemRoot), resolved, PathComparison));
    }

    [Fact]
    public void ResolveWorkshopRootSelectsCacheContainingRequestedItems()
    {
        var paths = new ApplicationPaths(_root);
        var executableRoot = Path.Combine(paths.SteamCmdRoot, "steamapps", "workshop");
        var runtimeRoot = Path.Combine(paths.RuntimeHomeRoot, "Steam", "steamapps", "workshop");
        Directory.CreateDirectory(Path.Combine(executableRoot, "content", "108600", "10"));
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "content", "108600", "10"));
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "content", "108600", "20"));

        var resolved = paths.ResolveSteamWorkshopRoot(paths.SteamCmdExecutable, [10, 20]);

        Assert.True(string.Equals(Path.GetFullPath(runtimeRoot), resolved, PathComparison));
    }

    [Fact]
    public void ImportedSnapshotTokenMatchesTheSameRemoteRevisionWithoutACache()
    {
        var paths = new ApplicationPaths(_root);
        var snapshot = Path.Combine(paths.SourcesRoot, "snapshot");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "mod.info"), "id=portable.mod");
        var reference = new PackageModReference
        {
            WorkshopId = 42,
            PinnedSourceRoot = snapshot,
            PinnedContentHash = "verified-hash",
            SourceUpdateToken = "steam-workshop:42:123456:1700000000"
        };

        Assert.True(SteamWorkshopSourceToken.MatchesRemote(reference, 42, 1700000000));
        Assert.False(SteamWorkshopSourceToken.MatchesRemote(reference, 42, 1700000001));
        Assert.False(SteamWorkshopSourceToken.MatchesRemote(reference, 43, 1700000000));
    }

    [Fact]
    public void CachePrunerOnlyRemovesManagerOwnedWorkshopItems()
    {
        var paths = new ApplicationPaths(_root);
        var managed = Path.Combine(paths.RuntimeHomeRoot, "Steam", "steamapps", "workshop", "content", "108600", "42");
        var externalSteamCmd = Path.Combine(_root, "external", "steamcmd.sh");
        var external = Path.Combine(Path.GetDirectoryName(externalSteamCmd)!, "steamapps", "workshop", "content", "108600", "42");
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(managed, "managed.bin"), new byte[128]);
        File.WriteAllBytes(Path.Combine(external, "external.bin"), new byte[256]);

        var result = new SteamWorkshopCachePruner(paths).RemoveItems([42]);

        Assert.Equal(1, result.Directories);
        Assert.Equal(128, result.Bytes);
        Assert.False(Directory.Exists(managed));
        Assert.True(File.Exists(Path.Combine(external, "external.bin")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
