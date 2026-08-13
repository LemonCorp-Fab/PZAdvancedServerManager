using PZAdvancedServerManager.Core.Infrastructure;

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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
