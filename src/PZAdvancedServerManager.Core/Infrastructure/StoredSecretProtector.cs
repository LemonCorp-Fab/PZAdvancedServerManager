using System.Security.Cryptography;
using System.Text;

namespace PZAdvancedServerManager.Core.Infrastructure;

public sealed class StoredSecretProtector
{
    private const string AesPrefix = "pzasm-secret:aes-v1:";
    private const string DpapiPrefix = "pzasm-secret:dpapi-v1:";
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("PZAdvancedServerManager.RemoteServerCredential.v1");
    private readonly byte[]? _aesKey;

    public StoredSecretProtector(ApplicationPaths paths, string? explicitKey = null)
    {
        var keyMaterial = explicitKey ?? ReadConfiguredKey();
        if (!string.IsNullOrWhiteSpace(keyMaterial))
        {
            if (keyMaterial.Length < 32)
                throw new InvalidOperationException("PZASM_DATA_ENCRYPTION_KEY must contain at least 32 characters.");
            _aesKey = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
            return;
        }

        if (!OperatingSystem.IsWindows())
            _aesKey = LoadOrCreateLocalKey(paths);
    }

    public bool IsProtected(string value) =>
        value.StartsWith(AesPrefix, StringComparison.Ordinal) ||
        value.StartsWith(DpapiPrefix, StringComparison.Ordinal);

    public string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || IsProtected(value)) return value;
        return _aesKey is not null ? ProtectAes(value) : ProtectDpapi(value);
    }

    public string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            if (value.StartsWith(AesPrefix, StringComparison.Ordinal)) return UnprotectAes(value[AesPrefix.Length..]);
            if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return UnprotectDpapi(value[DpapiPrefix.Length..]);
            return value;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidDataException("A stored server credential cannot be decrypted. Restore the original data-encryption key or a matching backup.", exception);
        }
    }

    private string ProtectAes(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_aesKey!, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Purpose);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, nonce.Length);
        ciphertext.CopyTo(payload, nonce.Length + tag.Length);
        CryptographicOperations.ZeroMemory(plaintext);
        return AesPrefix + Convert.ToBase64String(payload);
    }

    private string UnprotectAes(string payload)
    {
        if (_aesKey is null)
            throw new CryptographicException("This credential requires the configured portable encryption key.");
        var bytes = Convert.FromBase64String(payload);
        if (bytes.Length < 29) throw new CryptographicException("The encrypted credential is incomplete.");

        var nonce = bytes.AsSpan(0, 12);
        var tag = bytes.AsSpan(12, 16);
        var ciphertext = bytes.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_aesKey, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Purpose);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static string ProtectDpapi(string value)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credentials can be decrypted only by the Windows account that created them.");
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            var protectedBytes = ProtectedData.Protect(plaintext, Purpose, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(protectedBytes);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static string UnprotectDpapi(string payload)
    {
        if (!OperatingSystem.IsWindows())
            throw new CryptographicException("A Windows DPAPI credential cannot be opened on this platform.");
        var protectedBytes = Convert.FromBase64String(payload);
        var plaintext = ProtectedData.Unprotect(protectedBytes, Purpose, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static string? ReadConfiguredKey()
    {
        var keyFile = Environment.GetEnvironmentVariable("PZASM_DATA_ENCRYPTION_KEY_FILE");
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            if (!File.Exists(keyFile)) throw new FileNotFoundException("The configured data-encryption key file does not exist.", keyFile);
            return File.ReadAllText(keyFile).Trim();
        }
        return Environment.GetEnvironmentVariable("PZASM_DATA_ENCRYPTION_KEY")?.Trim();
    }

    private static byte[] LoadOrCreateLocalKey(ApplicationPaths paths)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The local key-file fallback is available only on Unix platforms.");
        var identityRoot = Path.Combine(paths.DataRoot, "identity");
        var keyPath = Path.Combine(identityRoot, "data-encryption.key");
        Directory.CreateDirectory(identityRoot);
        if (!File.Exists(keyPath))
        {
            var temporary = keyPath + ".tmp";
            File.WriteAllBytes(temporary, RandomNumberGenerator.GetBytes(32));
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try { File.Move(temporary, keyPath); }
            catch (IOException) when (File.Exists(keyPath)) { File.Delete(temporary); }
        }
        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var key = File.ReadAllBytes(keyPath);
        if (key.Length != 32) throw new InvalidDataException("The local data-encryption key is invalid.");
        return key;
    }
}
