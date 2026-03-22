using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WardLock.Services;

/// <summary>
/// Caches shared vault passwords using DPAPI (user-scoped) so vaults can
/// auto-reconnect on startup without re-prompting. The cached password is
/// bound to the current Windows user profile — another user or machine
/// cannot decrypt it.
///
/// Each vault is stored as a separate file keyed by a truncated SHA-256
/// hash of its canonical file path.
/// </summary>
public static class VaultPasswordCache
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WardLock", "vault-keys");

    /// <summary>
    /// Store a vault password, encrypted with DPAPI.
    /// </summary>
    public static void Store(string vaultPath, string password)
    {
        Directory.CreateDirectory(CacheDir);
        var key = GetKey(vaultPath);
        var encrypted = SecretVault.Encrypt(password);
        File.WriteAllText(Path.Combine(CacheDir, key), encrypted);
    }

    /// <summary>
    /// Try to load a cached vault password. Returns null if not cached
    /// or if DPAPI decryption fails (e.g. different user/machine).
    /// </summary>
    public static string? TryLoad(string vaultPath)
    {
        var file = Path.Combine(CacheDir, GetKey(vaultPath));
        if (!File.Exists(file)) return null;
        try
        {
            return SecretVault.Decrypt(File.ReadAllText(file));
        }
        catch
        {
            // DPAPI failure, corrupted file, etc. — treat as not cached
            return null;
        }
    }

    /// <summary>
    /// Remove a cached vault password.
    /// </summary>
    public static void Remove(string vaultPath)
    {
        var file = Path.Combine(CacheDir, GetKey(vaultPath));
        if (File.Exists(file))
            File.Delete(file);
    }

    /// <summary>
    /// Check if a password is cached for the given vault path.
    /// </summary>
    public static bool IsCached(string vaultPath)
        => File.Exists(Path.Combine(CacheDir, GetKey(vaultPath)));

    /// <summary>
    /// Derive a stable, filesystem-safe key from a vault path.
    /// Uses first 16 bytes of SHA-256 of the lowercased, trimmed path.
    /// </summary>
    private static string GetKey(string path)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                path.ToLowerInvariant().TrimEnd('\\', '/')))[..16]);
}
