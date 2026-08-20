using System.Diagnostics;
using System.Text;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class SshRemoteServerService
{
    public async Task<string> ReadFileAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        ValidateRemoteIni(connection);
        var result = await RunAsync(connection, $"cat -- {Quote(connection.RemoteIniPath)}", null, cancellationToken);
        EnsureSuccess(result, "lecture de la configuration distante");
        return result.Output;
    }

    public async Task<string> ReadTailAsync(RemoteServerConnection connection, string path, int maximumLines = 240, CancellationToken cancellationToken = default)
    {
        if (maximumLines is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumLines));
        ValidateRemotePath(path);
        var result = await RunAsync(connection, $"if [ -f {Quote(path)} ]; then tail -n {maximumLines} -- {Quote(path)}; fi", null, cancellationToken);
        EnsureSuccess(result, "lecture du journal distant");
        return result.Output;
    }

    public async Task<string> WriteFileAsync(RemoteServerConnection connection, string content, CancellationToken cancellationToken = default)
    {
        ValidateRemoteIni(connection);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var target = connection.RemoteIniPath;
        var temporary = target + ".pzasm.tmp";
        var backup = target + $".pzasm.{timestamp}.bak";
        var directory = Path.GetDirectoryName(target)?.Replace('\\', '/') ?? ".";
        var backupPattern = Path.GetFileName(target) + ".pzasm.*.bak";
        var command = $"set -e; mkdir -p -- {Quote(directory)}; if [ -f {Quote(target)} ]; then cp -- {Quote(target)} {Quote(backup)}; fi; cat > {Quote(temporary)}; mv -- {Quote(temporary)} {Quote(target)}; (find {Quote(directory)} -maxdepth 1 -type f -name {Quote(backupPattern)} -printf '%T@ %p\\0' | sort -z -nr | tail -z -n +21 | cut -z -d ' ' -f 2- | xargs -0 -r rm --) 2>/dev/null || true";
        var result = await RunAsync(connection, command, content, cancellationToken);
        EnsureSuccess(result, "écriture de la configuration distante");
        return backup;
    }

    public async Task RunStartCommandAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connection.StartCommand))
            throw new InvalidOperationException("Aucune commande SSH de démarrage du jeu n'est définie. Configurez par exemple « systemctl start pzserver » ou laissez le superviseur relancer automatiquement Project Zomboid.");
        var result = await RunAsync(connection, connection.StartCommand, null, cancellationToken);
        EnsureSuccess(result, "démarrage distant de Project Zomboid");
    }

    public async Task TestAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(connection, "printf PZASM_OK", null, cancellationToken);
        EnsureSuccess(result, "test SSH");
        if (!result.Output.Contains("PZASM_OK", StringComparison.Ordinal))
            throw new IOException("La connexion SSH a répondu sans fournir le marqueur de contrôle attendu.");
    }

    private static async Task<SshResult> RunAsync(RemoteServerConnection connection, string command, string? input, CancellationToken cancellationToken)
    {
        Validate(connection);
        var start = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null
        };
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(connection.SshPort.ToString());
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("BatchMode=yes");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ConnectTimeout=8");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        if (!string.IsNullOrWhiteSpace(connection.SshPrivateKeyPath))
        {
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(connection.SshPrivateKeyPath);
        }
        start.ArgumentList.Add($"{connection.SshUser}@{connection.Host}");
        start.ArgumentList.Add(command);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Le client SSH n'a pas pu démarrer. Installez OpenSSH Client ou ajoutez ssh au PATH.");
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("La commande SSH distante a dépassé le délai de 30 secondes.");
        }
        return new SshResult(process.ExitCode, await output, await error);
    }

    private static void EnsureSuccess(SshResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        throw new InvalidOperationException($"Échec de {operation} : {detail.Trim()}");
    }

    private static void Validate(RemoteServerConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Host) || connection.Host.Any(char.IsControl)) throw new ArgumentException("Hôte SSH invalide.");
        if (string.IsNullOrWhiteSpace(connection.SshUser) || connection.SshUser.Any(char.IsControl)) throw new ArgumentException("Utilisateur SSH invalide.");
        if (connection.SshPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(connection.SshPort));
    }

    private static void ValidateRemoteIni(RemoteServerConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.RemoteIniPath) || connection.RemoteIniPath.Any(char.IsControl)) throw new ArgumentException("Chemin INI distant invalide.");
    }

    private static void ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) throw new ArgumentException("Chemin distant invalide.", nameof(path));
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private sealed record SshResult(int ExitCode, string Output, string Error);
}
