namespace WardLock.Models;

/// <summary>
/// Vault file format v2 (issue #3 viewer role — see docs/viewer-role.md).
/// Two independent random keys: K_master (admin slot) encrypts the full
/// payload with seeds; K_viewer (viewer slot) encrypts a seedless payload of
/// metadata plus precomputed code windows. K_viewer is additionally wrapped
/// under K_master so admin clients can refresh the windows without knowing
/// the viewer password.
/// </summary>
public class VaultFileV2
{
    public string Version { get; set; } = "2.0";
    public int AccountCount { get; set; }
    /// <summary>How far ahead the precomputed code windows reach.</summary>
    public int HorizonHours { get; set; } = 72;

    /// <summary>K_master wrapped under PBKDF2(admin password).</summary>
    public KeySlot Admin { get; set; } = new();
    /// <summary>K_viewer wrapped under PBKDF2(viewer password).</summary>
    public KeySlot Viewer { get; set; } = new();
    /// <summary>K_viewer wrapped under K_master (no KDF; salt unused).</summary>
    public KeySlot ViewerKeyForAdmin { get; set; } = new();

    /// <summary>ExportAccount[] JSON (seeds included) under K_master.</summary>
    public SealedBlob Payload { get; set; } = new();
    /// <summary>ViewerAccount[] JSON (no seeds) under K_viewer.</summary>
    public SealedBlob ViewerPayload { get; set; } = new();
    /// <summary>UTC time the viewer code windows were generated ("o" format).</summary>
    public string ViewerPayloadGeneratedAt { get; set; } = string.Empty;
}

/// <summary>A wrapped 256-bit key: AES-256-GCM over the raw key bytes.</summary>
public class KeySlot
{
    /// <summary>Base64 PBKDF2 salt; empty when the wrapping key is not password-derived.</summary>
    public string Salt { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string WrappedKey { get; set; } = string.Empty;

    public bool IsPresent => WrappedKey.Length > 0;
}

/// <summary>An AES-256-GCM encrypted JSON blob.</summary>
public class SealedBlob
{
    public string Nonce { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;

    public bool IsPresent => Data.Length > 0;
}

/// <summary>
/// One account as visible to the viewer role: display metadata plus a
/// precomputed code window — never the seed.
/// </summary>
public class ViewerAccount
{
    public string Issuer { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public string Encoder { get; set; } = "Default";
    public int SortOrder { get; set; }
    public string? Domain { get; set; }
    public bool RequireApproval { get; set; }

    /// <summary>RFC 6238 timestep of the first code in <see cref="Codes"/>.</summary>
    public long StartStep { get; set; }
    /// <summary>Width of one code in <see cref="Codes"/> (Digits, or 5 for Steam).</summary>
    public int CodeWidth { get; set; } = 6;
    /// <summary>Concatenated fixed-width codes covering the horizon.</summary>
    public string Codes { get; set; } = string.Empty;
}
