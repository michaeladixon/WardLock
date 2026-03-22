using System;
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
        if (string.IsNullOrWhiteSpace(cipherText))
            throw new CryptographicException("EncryptedSecret is empty or whitespace; expected DPAPI-protected Base64 data.");

        byte[] cipherBytes;
        try
        {
            cipherBytes = Convert.FromBase64String(cipherText);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("EncryptedSecret is not valid Base64.", ex);
        }

        if (cipherBytes.Length == 0)
            throw new CryptographicException("EncryptedSecret decodes to empty data; expected DPAPI-protected blob.");

        try
        {
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException("Failed to decrypt EncryptedSecret with DPAPI. Data may be corrupted or protected to a different user/scope.", ex);
        }
    }
}
