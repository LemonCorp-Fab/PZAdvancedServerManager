using Microsoft.Data.Sqlite;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class ServerWorldDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-world-data", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackupAndRestoreReplaceWorldButPreserveCurrentConfigurationByDefault()
    {
        var (store, location) = CreateFixture();
        WriteWorld(location, "original-world", "original-database", "configuration-v1");
        var backup = await store.CreateBackupAsync(location);

        WriteWorld(location, "changed-world", "changed-database", "configuration-v2");
        var result = await store.RestoreAsync(location, backup.Id, restoreConfiguration: false);

        Assert.Equal("original-world", File.ReadAllText(Path.Combine(location.WorldPath, "map.bin")));
        Assert.Equal("original-database", File.ReadAllText(location.DatabasePath));
        Assert.Equal("original-database-wal", File.ReadAllText(location.DatabasePath + "-wal"));
        Assert.Equal("configuration-v2", File.ReadAllText(location.ConfigurationFiles[0]));
        Assert.NotNull(result.SafetyBackup);
        Assert.Equal("pre-restore", result.SafetyBackup!.Reason);
        Assert.False(result.ConfigurationRestored);
        Assert.Equal(2, store.List(location.ProfileName).Count);
    }

    [Fact]
    public async Task RestoreCanIncludeTheArchivedServerConfiguration()
    {
        var (store, location) = CreateFixture();
        WriteWorld(location, "world-v1", "database-v1", "configuration-v1");
        var backup = await store.CreateBackupAsync(location);
        File.WriteAllText(location.ConfigurationFiles[0], "configuration-v2");

        var result = await store.RestoreAsync(location, backup.Id, restoreConfiguration: true);

        Assert.True(result.ConfigurationRestored);
        Assert.Equal("configuration-v1", File.ReadAllText(location.ConfigurationFiles[0]));
    }

    [Fact]
    public async Task FreshStartCreatesARecoverableBackupAndKeepsConfiguration()
    {
        var (store, location) = CreateFixture();
        WriteWorld(location, "world", "database", "configuration");

        var reset = await store.ResetAsync(location);

        Assert.False(Directory.Exists(location.WorldPath));
        Assert.False(File.Exists(location.DatabasePath));
        Assert.False(File.Exists(location.DatabasePath + "-wal"));
        Assert.Equal("configuration", File.ReadAllText(location.ConfigurationFiles[0]));
        Assert.NotNull(reset.SafetyBackup);
        Assert.Equal("pre-reset", reset.SafetyBackup!.Reason);

        await store.RestoreAsync(location, reset.SafetyBackup!.Id, restoreConfiguration: false);
        Assert.Equal("world", File.ReadAllText(Path.Combine(location.WorldPath, "map.bin")));
        Assert.Equal("database", File.ReadAllText(location.DatabasePath));
        Assert.Equal("database-wal", File.ReadAllText(location.DatabasePath + "-wal"));
    }

    [Fact]
    public async Task FreshStartCanExplicitlySkipTheSafetyBackup()
    {
        var (store, location) = CreateFixture();
        WriteWorld(location, "world", "database", "configuration");

        var reset = await store.ResetAsync(location, createSafetyBackup: false);

        Assert.Null(reset.SafetyBackup);
        Assert.Empty(store.List(location.ProfileName));
        Assert.False(Directory.Exists(location.WorldPath));
        Assert.False(File.Exists(location.DatabasePath));
        Assert.Equal("configuration", File.ReadAllText(location.ConfigurationFiles[0]));
    }

    [Fact]
    public async Task RestoreRejectsAnArchiveWhoseHashChanged()
    {
        var (store, location) = CreateFixture();
        WriteWorld(location, "world", "database", "configuration");
        var backup = await store.CreateBackupAsync(location);
        var archive = Directory.EnumerateFiles(store.EnsureBackupRoot(location.ProfileName), backup.Id + "*.zip").Single();
        await File.AppendAllTextAsync(archive, "tampered");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreAsync(location, backup.Id, restoreConfiguration: false));
    }

    [Fact]
    public void MissingOrEmptyUserDatabaseRequiresInitialAdminSetup()
    {
        var (store, location) = CreateFixture();

        Assert.Equal(InitialAdminAccountState.Required, store.InspectInitialAdminAccount(location).State);

        CreateUserDatabase(location.DatabasePath, includeAdmin: false);

        var status = store.InspectInitialAdminAccount(location);
        Assert.Equal(InitialAdminAccountState.Required, status.State);
        Assert.True(status.IsRequired);
    }

    [Fact]
    public void ExistingAdminAccountSkipsInitialPasswordSetup()
    {
        var (store, location) = CreateFixture();
        CreateUserDatabase(location.DatabasePath, includeAdmin: true);

        var status = store.InspectInitialAdminAccount(location);

        Assert.Equal(InitialAdminAccountState.Configured, status.State);
        Assert.True(status.IsConfigured);
    }

    [Fact]
    public void UnreadableUserDatabaseKeepsInitialAdminSetupOptional()
    {
        var (store, location) = CreateFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(location.DatabasePath)!);
        File.WriteAllText(location.DatabasePath, "not-a-sqlite-database");

        var status = store.InspectInitialAdminAccount(location);

        Assert.Equal(InitialAdminAccountState.Unknown, status.State);
        Assert.False(status.IsRequired);
    }

    private (ServerWorldDataStore Store, ServerWorldDataLocation Location) CreateFixture()
    {
        var userRoot = Path.Combine(_root, "Zomboid");
        var profile = "integration-server";
        var location = new ServerWorldDataLocation(
            profile,
            userRoot,
            Path.Combine(userRoot, "Saves", "Multiplayer", profile),
            Path.Combine(userRoot, "db", profile + ".db"),
            [
                Path.Combine(userRoot, "Server", profile + ".ini"),
                Path.Combine(userRoot, "Server", profile + "_SandboxVars.lua")
            ]);
        return (new ServerWorldDataStore(new ApplicationPaths(Path.Combine(_root, "manager-data"))), location);
    }

    private static void WriteWorld(ServerWorldDataLocation location, string world, string database, string configuration)
    {
        Directory.CreateDirectory(location.WorldPath);
        Directory.CreateDirectory(Path.GetDirectoryName(location.DatabasePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(location.ConfigurationFiles[0])!);
        File.WriteAllText(Path.Combine(location.WorldPath, "map.bin"), world);
        File.WriteAllText(location.DatabasePath, database);
        File.WriteAllText(location.DatabasePath + "-wal", database + "-wal");
        File.WriteAllText(location.ConfigurationFiles[0], configuration);
    }

    private static void CreateUserDatabase(string path, bool includeAdmin)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE whitelist (id INTEGER PRIMARY KEY, username TEXT NULL, password TEXT NULL, role INTEGER NOT NULL);" +
            (includeAdmin ? "INSERT INTO whitelist (username, password, role) VALUES ('AdMiN', 'hash', 1);" : string.Empty);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
