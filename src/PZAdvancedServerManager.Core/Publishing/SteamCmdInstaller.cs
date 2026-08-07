using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Publishing;

public sealed class SteamCmdInstaller
{
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const string WindowsDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const string LinuxDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly ApplicationPaths _paths;
    private readonly HttpClient _httpClient;

    public SteamCmdInstaller(ApplicationPaths paths) : this(paths, SharedHttpClient) { }

    public SteamCmdInstaller(ApplicationPaths paths, HttpClient httpClient)
    {
        _paths = paths;
        _httpClient = httpClient;
    }

    public SteamCmdStatus GetStatus()
    {
        var executable = _paths.SteamCmdExecutable;
        if (!File.Exists(executable)) return new SteamCmdStatus(false, executable, _paths.SteamCmdRoot, null, 0);
        var info = new FileInfo(executable);
        return new SteamCmdStatus(true, executable, _paths.SteamCmdRoot, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
    }

    public async Task<SteamCmdInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.ToolsRoot);
            var archiveExtension = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
            var archivePath = Path.Combine(_paths.ToolsRoot, $"steamcmd-download-{Guid.NewGuid():N}{archiveExtension}");
            var stagingRoot = Path.Combine(_paths.ToolsRoot, $"steamcmd-stage-{Guid.NewGuid():N}");
            try
            {
                await DownloadAsync(OperatingSystem.IsWindows() ? WindowsDownloadUrl : LinuxDownloadUrl, archivePath, cancellationToken);
                Directory.CreateDirectory(stagingRoot);
                if (OperatingSystem.IsWindows()) ExtractZip(archivePath, stagingRoot);
                else ExtractTarGzip(archivePath, stagingRoot);

                var stagedExecutable = Path.Combine(stagingRoot, OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");
                if (!File.Exists(stagedExecutable)) throw new InvalidDataException("L'archive SteamCMD ne contient pas l'exécutable attendu.");
                Directory.CreateDirectory(_paths.SteamCmdRoot);
                SafeFileTree.CopyDirectory(stagingRoot, _paths.SteamCmdRoot);
                if (!OperatingSystem.IsWindows()) EnsureExecutable(_paths.SteamCmdExecutable);

                var bootstrap = await BootstrapAsync(_paths.SteamCmdExecutable, cancellationToken);
                if (bootstrap.ExitCode != 0 && File.Exists(_paths.SteamCmdExecutable))
                {
                    await Task.Delay(500, cancellationToken);
                    var retry = await BootstrapAsync(_paths.SteamCmdExecutable, cancellationToken);
                    bootstrap = new SteamCmdResult(retry.ExitCode, string.Join(Environment.NewLine, bootstrap.StandardOutput, retry.StandardOutput), string.Join(Environment.NewLine, bootstrap.StandardError, retry.StandardError));
                }
                return new SteamCmdInstallResult(_paths.SteamCmdExecutable, bootstrap.ExitCode == 0, bootstrap.CombinedOutput);
            }
            finally
            {
                if (File.Exists(archivePath)) File.Delete(archivePath);
                SafeFileTree.DeleteScopedDirectory(_paths.ToolsRoot, stagingRoot);
            }
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
            throw new InvalidDataException("L'archive SteamCMD dépasse la taille maximale autorisée.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaximumArchiveBytes) throw new InvalidDataException("L'archive SteamCMD dépasse la taille maximale autorisée.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ExtractZip(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = ResolveArchiveEntry(destination, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
        }
    }

    private static void ExtractTarGzip(string archivePath, string destination)
    {
        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                throw new InvalidDataException("Lien symbolique refusé dans l'archive SteamCMD.");
            var target = ResolveArchiveEntry(destination, entry.Name);
            if (entry.EntryType is TarEntryType.Directory) Directory.CreateDirectory(target);
            else if (entry.DataStream is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = File.Create(target)) entry.DataStream.CopyTo(output);
                if (!OperatingSystem.IsWindows())
                {
                    ApplyUnixMode(target, entry.Mode);
                }
            }
        }
    }

    private static string ResolveArchiveEntry(string destination, string relative)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Chemin refusé dans l'archive SteamCMD.");
        return target;
    }

    [UnsupportedOSPlatform("windows")]
    private static void EnsureExecutable(string path)
    {
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

    [UnsupportedOSPlatform("windows")]
    private static void ApplyUnixMode(string path, UnixFileMode mode) => File.SetUnixFileMode(path, mode);

    private static async Task<SteamCmdResult> BootstrapAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            start.ArgumentList.Add("+quit");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("SteamCMD n'a pas pu démarrer.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new SteamCmdResult(process.ExitCode, await output, await error);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new SteamCmdResult(-1, string.Empty, exception.Message);
        }
    }
}

public sealed record SteamCmdStatus(bool Installed, string ExecutablePath, string Root, DateTimeOffset? UpdatedAt, long ExecutableBytes);
public sealed record SteamCmdInstallResult(string ExecutablePath, bool Bootstrapped, string Output);
