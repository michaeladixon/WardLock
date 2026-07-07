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
    public OtpEncoder Encoder { get; set; } = OtpEncoder.Default;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int SortOrder { get; set; }

    /// <summary>
    /// Registrable domain (eTLD+1, e.g. "github.com") this account fills codes for
    /// in the browser extension. Null = never offered to the browser.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// When true, browser fills for this account require the number-matched
    /// out-of-band approval (2-digit challenge typed into the desktop app).
    /// </summary>
    public bool RequireApproval { get; set; }

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

    /// <summary>
    /// Precomputed code window for viewer-role vault accounts (no seed present).
    /// When set, TotpGenerator looks codes up here instead of computing them.
    /// </summary>
    [JsonIgnore]
    public CodeWindow? CodeWindow { get; set; }
}

/// <summary>
/// A viewer's precomputed codes: fixed-width concatenation indexed by RFC 6238
/// timestep. See docs/viewer-role.md.
/// </summary>
public class CodeWindow
{
    public long StartStep { get; init; }
    public int Width { get; init; }
    public string Codes { get; init; } = string.Empty;

    /// <summary>Code for the given unix time, or null when outside the window.</summary>
    public string? CodeAt(long unixSeconds, int period)
    {
        var index = unixSeconds / period - StartStep;
        if (index < 0 || (index + 1) * Width > Codes.Length) return null;
        return Codes.Substring((int)index * Width, Width);
    }
}

public enum OtpHashAlgorithm
{
    Sha1,
    Sha256,
    Sha512
}

/// <summary>
/// Output encoding of the truncated TOTP value.
/// Steam uses a 5-character code over a custom alphabet instead of decimal digits.
/// </summary>
public enum OtpEncoder
{
    Default,
    Steam
}
