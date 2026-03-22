using System.Text.Json.Serialization;

namespace WardLock.Models;

public class AuthAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Issuer { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string EncryptedSecret { get; set; } = string.Empty;
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public OtpHashAlgorithm Algorithm { get; set; } = OtpHashAlgorithm.Sha1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int SortOrder { get; set; }

    // ── Shared vault in-memory fields (never serialized) ──

    /// <summary>
    /// Plaintext Base32 secret, only populated for shared vault accounts.
    /// When set, TotpGenerator uses this instead of DPAPI decryption.
    /// </summary>
    [JsonIgnore]
    public string? PlaintextSecret { get; set; }

    /// <summary>
    /// Name of the shared vault this account belongs to, or null for personal accounts.
    /// </summary>
    [JsonIgnore]
    public string? VaultName { get; set; }
}

public enum OtpHashAlgorithm
{
    Sha1,
    Sha256,
    Sha512
}
