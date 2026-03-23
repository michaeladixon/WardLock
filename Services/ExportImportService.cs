using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WardLock.Models;

namespace WardLock.Services;

public static class ExportImportService
{
    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;   // AES-GCM standard
    private const int KeySize = 32;   // AES-256
    private const int Iterations = 600_000; // OWASP 2023 recommendation for PBKDF2-SHA256

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Export accounts to an AES-256-GCM encrypted file.
    /// Secrets are decrypted from DPAPI and re-encrypted with the user's password.
    /// </summary>
    public static void Export(List<AuthAccount> accounts, string filePath, string password)
    {
        // Build plaintext export records (decrypt DPAPI -> plaintext Base32)
        var exportAccounts = accounts.Select((a, i) => new ExportAccount
        {
            Issuer = a.Issuer,
            Label = a.Label,
            Secret = SecretVault.Decrypt(a.EncryptedSecret),
            Digits = a.Digits,
            Period = a.Period,
            Algorithm = a.Algorithm.ToString(),
            SortOrder = a.SortOrder > 0 ? a.SortOrder : i
        }).ToList();

        var plainJson = JsonSerializer.Serialize(exportAccounts, JsonOpts);
        var plainBytes = Encoding.UTF8.GetBytes(plainJson);

        // Derive key from password
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(password, salt);

        // Encrypt with AES-256-GCM
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherText = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherText, tag);

        // Build payload
        var payload = new ExportPayload
        {
            AccountCount = exportAccounts.Count,
            EncryptedData = Convert.ToBase64String(cipherText),
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag)
        };

        var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);
        File.WriteAllText(filePath, payloadJson);
    }

    /// <summary>
    /// Import accounts from an encrypted export file.
    /// Secrets are decrypted with the password and re-encrypted with DPAPI.
    /// </summary>
    public static List<AuthAccount> Import(string filePath, string password)
    {
        var payloadJson = File.ReadAllText(filePath);
        var payload = JsonSerializer.Deserialize<ExportPayload>(payloadJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid export file format.");

        var salt = Convert.FromBase64String(payload.Salt);
        var nonce = Convert.FromBase64String(payload.Nonce);
        var tag = Convert.FromBase64String(payload.Tag);
        var cipherText = Convert.FromBase64String(payload.EncryptedData);

        // Derive key and decrypt
        var key = DeriveKey(password, salt);
        var plainBytes = new byte[cipherText.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherText, tag, plainBytes); // Throws if password wrong

        var plainJson = Encoding.UTF8.GetString(plainBytes);
        var exportAccounts = JsonSerializer.Deserialize<List<ExportAccount>>(plainJson, JsonOpts)
            ?? throw new InvalidOperationException("Decrypted data is not valid.");

        // Convert back to AuthAccounts with DPAPI-encrypted secrets
        return exportAccounts.Select(e => new AuthAccount
        {
            Id = Guid.NewGuid().ToString(),
            Issuer = e.Issuer,
            Label = e.Label,
            EncryptedSecret = SecretVault.Encrypt(e.Secret),
            Digits = e.Digits,
            Period = e.Period,
            Algorithm = Enum.TryParse<OtpHashAlgorithm>(e.Algorithm, true, out var a) ? a : OtpHashAlgorithm.Sha1,
            SortOrder = e.SortOrder,
            CreatedAt = DateTime.UtcNow
        }).ToList();
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
    }
}
