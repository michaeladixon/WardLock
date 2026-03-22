using System.Security.Cryptography;
using System.Text;

namespace WardLock.Services;

/// <summary>
/// Encrypts/decrypts TOTP secrets using Windows DPAPI (user-scoped).
/// Secrets are bound to the current Windows user profile — 
/// they can't be decrypted by another user or on another machine.
/// </summary>
public static class SecretVault
{
    public static string Encrypt(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipherBytes);
    }

    public static string Decrypt(string cipherText)
    {
        var cipherBytes = Convert.FromBase64String(cipherText);
        var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
