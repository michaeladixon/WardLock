using System.Security.Cryptography;

namespace WardLock.Services;

/// <summary>
/// Manages an app-level password lock (separate from vault encryption passwords).
/// The password is stored as PBKDF2-SHA256(password, salt) in AppSettings — never plaintext.
/// </summary>
public static class PasswordLockService
{
    private const int SaltSize   = 16;
    private const int HashSize   = 32;
    private const int Iterations = 300_000;

    public static bool IsConfigured => !string.IsNullOrEmpty(AppSettings.LockPasswordHash);

    public static void Set(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt);
        AppSettings.LockPasswordHash = $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password)
    {
        var stored = AppSettings.LockPasswordHash;
        if (string.IsNullOrEmpty(stored)) return false;

        var parts = stored.Split(':');
        if (parts.Length != 2) return false;

        try
        {
            var salt         = Convert.FromBase64String(parts[0]);
            var expectedHash = Convert.FromBase64String(parts[1]);
            var actualHash   = Derive(password, salt);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }

    public static void Clear() => AppSettings.LockPasswordHash = null;

    private static byte[] Derive(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
}
