namespace WardLock.Models;

/// <summary>
/// Container for encrypted export files.
/// </summary>
public class ExportPayload
{
    public string Version { get; set; } = "1.0";
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public int AccountCount { get; set; }
    /// <summary>Base64-encoded AES-256-GCM ciphertext of the JSON account array.</summary>
    public string EncryptedData { get; set; } = string.Empty;
    /// <summary>Base64 salt used for PBKDF2 key derivation.</summary>
    public string Salt { get; set; } = string.Empty;
    /// <summary>Base64 nonce for AES-GCM.</summary>
    public string Nonce { get; set; } = string.Empty;
    /// <summary>Base64 auth tag for AES-GCM.</summary>
    public string Tag { get; set; } = string.Empty;
}

/// <summary>
/// Plaintext account record for export (secrets in cleartext Base32).
/// </summary>
public class ExportAccount
{
    public string Issuer { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty; // Base32 plaintext
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public string Algorithm { get; set; } = "SHA1";
    public int SortOrder { get; set; }
}
