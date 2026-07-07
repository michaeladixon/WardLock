using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WardLock.Models;

namespace WardLock.Services;

/// <summary>Session role of an open shared vault (docs/viewer-role.md).</summary>
public enum VaultRole
{
    /// <summary>Full access: seeds in memory, can modify and re-encrypt the vault.</summary>
    Admin,
    /// <summary>Codes only: precomputed windows, no seeds, cryptographically read-only.</summary>
    Viewer,
}

/// <summary>
/// Manages shared vault files. Each vault is an AES-256-GCM encrypted .wardlock file
/// that can live on a network share, OneDrive, or SharePoint. Multiple team members
/// can open the same vault, and a FileSystemWatcher detects external changes.
///
/// Two formats (docs/viewer-role.md):
/// - v1: single password, PBKDF2 → key → payload. Everyone is an admin.
/// - v2: admin slot wraps K_master (seeds payload); viewer slot wraps K_viewer
///   (seedless payload of metadata + precomputed code windows). The password
///   given to Open() determines the role by which slot it unwraps.
///
/// Admin sessions hold secrets in plaintext in memory (never DPAPI'd) because
/// they re-encrypt with the vault keys on save, not bound to one user's profile.
/// Viewer sessions never hold seeds at all.
/// </summary>
public class SharedVaultService : IDisposable
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 600_000;

    /// <summary>Precomputed code coverage for viewers. 72h ⇒ a vault survives a weekend with no admin online.</summary>
    public const int DefaultHorizonHours = 72;
    /// <summary>Admins regenerate windows once they are older than horizon / this factor.</summary>
    private const int RefreshDivisor = 3;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string FilePath { get; }
    public string VaultName { get; }
    public List<AuthAccount> Accounts { get; private set; } = [];
    public bool IsOpen { get; private set; }

    /// <summary>Role of this session, decided by which key slot the password unwrapped.</summary>
    public VaultRole Role { get; private set; } = VaultRole.Admin;
    public bool IsViewer => Role == VaultRole.Viewer;

    /// <summary>True when a viewer password is configured on the vault file (v2).</summary>
    public bool HasViewerAccess => _viewerSlot != null;

    public int HorizonHours { get; private set; } = DefaultHorizonHours;

    /// <summary>Tamper-evident sidecar audit log (issue #3).</summary>
    public VaultAuditLog AuditLog { get; }

    private string _password = string.Empty;
    private byte[]? _masterKey;             // v2 admin sessions
    private byte[]? _viewerKey;             // v2 admin sessions with viewer access configured
    private KeySlot? _viewerSlot;           // carried through admin saves verbatim
    private DateTime _viewerGeneratedAt = DateTime.MinValue;
    private FileSystemWatcher? _watcher;
    private Timer? _refreshTimer;
    private DateTime _lastWriteByUs = DateTime.MinValue;
    private readonly object _ioLock = new();

    /// <summary>Fires when the vault file is modified externally (by a teammate).</summary>
    public event Action? ExternalChange;

    public SharedVaultService(string filePath)
    {
        FilePath = filePath;
        VaultName = Path.GetFileNameWithoutExtension(filePath);
        AuditLog = new VaultAuditLog(filePath);
    }

    /// <summary>
    /// Open an existing vault file. Decrypts with the given password; the slot
    /// it unwraps (admin or viewer) determines this session's role.
    /// </summary>
    public void Open(string password)
    {
        _password = password;
        Reload();
        StartWatching();
        IsOpen = true;
        AuditLog.TryAppend(AuditAction.VaultOpened, string.Empty, IsViewer ? "viewer" : "admin");

        if (Role == VaultRole.Admin && HasViewerAccess)
        {
            RefreshViewerWindowsIfStale();
            _refreshTimer = new Timer(_ => RefreshViewerWindowsIfStale(), null,
                TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        }
    }

    /// <summary>
    /// Create a new empty vault file, encrypted with the given password (v1
    /// format; upgrades to v2 when a viewer password is first set).
    /// </summary>
    public static SharedVaultService CreateNew(string filePath, string password)
    {
        var service = new SharedVaultService(filePath);
        service._password = password;
        service.Accounts = [];
        service.SaveToDisk();
        service.StartWatching();
        service.IsOpen = true;
        service.AuditLog.TryAppend(AuditAction.VaultCreated);
        return service;
    }

    /// <summary>
    /// Re-read the vault file from disk and decrypt. Called on first open
    /// and when the FileSystemWatcher fires.
    /// </summary>
    public void Reload()
    {
        lock (_ioLock)
        {
            var fileJson = File.ReadAllText(FilePath);
            using var doc = JsonDocument.Parse(fileJson);
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : "1.0";

            if (version?.StartsWith("2.") == true)
                ReloadV2(fileJson);
            else
                ReloadV1(fileJson);
        }
    }

    private void ReloadV1(string fileJson)
    {
        var payload = JsonSerializer.Deserialize<ExportPayload>(fileJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid vault file.");

        var salt = Convert.FromBase64String(payload.Salt);
        var nonce = Convert.FromBase64String(payload.Nonce);
        var tag = Convert.FromBase64String(payload.Tag);
        var cipherText = Convert.FromBase64String(payload.EncryptedData);

        var key = DeriveKey(_password, salt);
        var plainBytes = new byte[cipherText.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherText, tag, plainBytes);

        Role = VaultRole.Admin;
        _masterKey = null;
        _viewerKey = null;
        _viewerSlot = null;
        LoadAdminAccounts(Encoding.UTF8.GetString(plainBytes));
    }

    private void ReloadV2(string fileJson)
    {
        var file = JsonSerializer.Deserialize<VaultFileV2>(fileJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid vault file.");

        HorizonHours = file.HorizonHours > 0 ? file.HorizonHours : DefaultHorizonHours;
        _viewerSlot = file.Viewer.IsPresent ? file.Viewer : null;
        _viewerGeneratedAt = DateTime.TryParse(file.ViewerPayloadGeneratedAt, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var g) ? g : DateTime.MinValue;

        // Try the admin slot first; fall back to the viewer slot.
        try
        {
            _masterKey = UnwrapKey(file.Admin, DeriveKey(_password, Convert.FromBase64String(file.Admin.Salt)));
            Role = VaultRole.Admin;
        }
        catch (AuthenticationTagMismatchException)
        {
            if (_viewerSlot == null) throw;

            var viewerKdf = DeriveKey(_password, Convert.FromBase64String(_viewerSlot.Salt));
            var viewerKey = UnwrapKey(_viewerSlot, viewerKdf); // throws AuthenticationTagMismatch on wrong password
            Role = VaultRole.Viewer;
            _masterKey = null;
            _viewerKey = viewerKey;
            LoadViewerAccounts(file, viewerKey);
            return;
        }

        _viewerKey = _viewerSlot != null ? UnwrapKey(file.ViewerKeyForAdmin, _masterKey!) : null;
        LoadAdminAccounts(Encoding.UTF8.GetString(OpenBlob(file.Payload, _masterKey!)));
    }

    private void LoadAdminAccounts(string plainJson)
    {
        var exported = JsonSerializer.Deserialize<List<ExportAccount>>(plainJson, JsonOpts) ?? [];

        Accounts = exported.Select(e => new AuthAccount
        {
            Id = Guid.NewGuid().ToString(),
            Issuer = e.Issuer,
            Label = e.Label,
            PlaintextSecret = e.Secret, // held in memory, not DPAPI
            VaultName = VaultName,
            Digits = e.Digits,
            Period = e.Period,
            Algorithm = Enum.TryParse<OtpHashAlgorithm>(e.Algorithm, true, out var a) ? a : OtpHashAlgorithm.Sha1,
            Encoder = Enum.TryParse<OtpEncoder>(e.Encoder, true, out var enc) ? enc : OtpEncoder.Default,
            SortOrder = e.SortOrder,
            CreatedAt = DateTime.UtcNow,
            Domain = e.Domain,
            RequireApproval = e.RequireApproval
        }).ToList();
    }

    private void LoadViewerAccounts(VaultFileV2 file, byte[] viewerKey)
    {
        var viewerJson = Encoding.UTF8.GetString(OpenBlob(file.ViewerPayload, viewerKey));
        var viewerAccounts = JsonSerializer.Deserialize<List<ViewerAccount>>(viewerJson, JsonOpts) ?? [];

        Accounts = viewerAccounts.Select(e => new AuthAccount
        {
            Id = Guid.NewGuid().ToString(),
            Issuer = e.Issuer,
            Label = e.Label,
            PlaintextSecret = null, // the whole point
            VaultName = VaultName,
            Digits = e.Digits,
            Period = e.Period,
            Encoder = Enum.TryParse<OtpEncoder>(e.Encoder, true, out var enc) ? enc : OtpEncoder.Default,
            SortOrder = e.SortOrder,
            CreatedAt = DateTime.UtcNow,
            Domain = e.Domain,
            RequireApproval = e.RequireApproval,
            CodeWindow = new CodeWindow { StartStep = e.StartStep, Width = e.CodeWidth, Codes = e.Codes }
        }).ToList();
    }

    // ── Viewer access management (admin only) ──

    /// <summary>
    /// Set or rotate the viewer password. Always generates a fresh K_viewer,
    /// so rotation immediately locks out holders of the old password (their
    /// hoarded codes age out with the horizon). Upgrades v1 files to v2.
    /// </summary>
    public void SetViewerPassword(string viewerPassword)
    {
        EnsureAdmin();

        _masterKey ??= RandomNumberGenerator.GetBytes(KeySize); // v1 → v2 upgrade
        _viewerKey = RandomNumberGenerator.GetBytes(KeySize);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        _viewerSlot = WrapKey(_viewerKey, DeriveKey(viewerPassword, salt), salt);

        SaveToDisk();
        AuditLog.TryAppend(AuditAction.ViewerAccessChanged, string.Empty, "viewer password set/rotated");
        if (_refreshTimer == null && IsOpen)
            _refreshTimer = new Timer(_ => RefreshViewerWindowsIfStale(), null,
                TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    /// <summary>Remove viewer access entirely; the file saves back to v1.</summary>
    public void RemoveViewerAccess()
    {
        EnsureAdmin();
        _viewerSlot = null;
        _viewerKey = null;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        SaveToDisk();
        AuditLog.TryAppend(AuditAction.ViewerAccessChanged, string.Empty, "viewer access removed");
    }

    /// <summary>Rewrite the file when the viewers' precomputed windows are aging out.</summary>
    private void RefreshViewerWindowsIfStale()
    {
        try
        {
            if (!IsOpen || Role != VaultRole.Admin || _viewerSlot == null) return;
            if ((DateTime.UtcNow - _viewerGeneratedAt).TotalHours < (double)HorizonHours / RefreshDivisor) return;
            SaveToDisk();
        }
        catch
        {
            // Share unreachable etc. — next timer tick or save retries.
        }
    }

    private void EnsureAdmin()
    {
        if (Role != VaultRole.Admin)
            throw new InvalidOperationException("This vault is open as viewer — codes only.");
    }

    /// <summary>
    /// Add an account to this shared vault and persist to disk.
    /// Accepts a plaintext Base32 secret (NOT a DPAPI-encrypted one).
    /// </summary>
    public void AddAccount(string issuer, string label, string plaintextSecret,
        int digits = 6, int period = 30, OtpHashAlgorithm algorithm = OtpHashAlgorithm.Sha1,
        OtpEncoder encoder = OtpEncoder.Default, string? domain = null)
    {
        EnsureAdmin();
        var account = new AuthAccount
        {
            Issuer = issuer,
            Label = label,
            PlaintextSecret = plaintextSecret,
            VaultName = VaultName,
            Digits = encoder == OtpEncoder.Steam ? 5 : digits,
            Period = period,
            Algorithm = algorithm,
            Encoder = encoder,
            SortOrder = Accounts.Count > 0 ? Accounts.Max(a => a.SortOrder) + 1 : 0,
            Domain = domain
        };

        Accounts.Add(account);
        SaveToDisk();
        AuditLog.TryAppend(AuditAction.AccountAdded, DisplayName(account));
    }

    /// <summary>Set or clear the browser-fill domain on a vault account and persist.</summary>
    public void UpdateAccountDomain(string id, string? domain)
    {
        EnsureAdmin();
        var account = Accounts.FirstOrDefault(a => a.Id == id);
        if (account == null) return;
        account.Domain = domain;
        SaveToDisk();
        AuditLog.TryAppend(AuditAction.DomainChanged, DisplayName(account), domain ?? "(cleared)");
    }

    /// <summary>Toggle the number-matched fill approval requirement on a vault account and persist.</summary>
    public void UpdateAccountApproval(string id, bool requireApproval)
    {
        EnsureAdmin();
        var account = Accounts.FirstOrDefault(a => a.Id == id);
        if (account == null) return;
        account.RequireApproval = requireApproval;
        SaveToDisk();
        AuditLog.TryAppend(AuditAction.ApprovalRequirementChanged, DisplayName(account),
            requireApproval ? "required" : "not required");
    }

    private static string DisplayName(AuthAccount a)
        => string.IsNullOrEmpty(a.Issuer) ? a.Label : $"{a.Issuer} ({a.Label})";

    /// <summary>
    /// Add an account from an otpauth:// URI to this shared vault.
    /// </summary>
    public AuthAccount AddAccountFromUri(string otpAuthUri)
    {
        EnsureAdmin();

        // Parse the URI to extract the secret in plaintext
        var isSteamScheme = otpAuthUri.StartsWith("otpauth://steam/", StringComparison.OrdinalIgnoreCase);
        if (!otpAuthUri.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase) && !isSteamScheme)
            throw new ArgumentException("Only TOTP URIs are supported.");

        var uriObj = new Uri(otpAuthUri);
        var path = Uri.UnescapeDataString(uriObj.AbsolutePath.TrimStart('/'));
        var query = System.Web.HttpUtility.ParseQueryString(uriObj.Query);

        var secret = query["secret"] ?? throw new ArgumentException("Missing secret parameter.");
        var issuer = query["issuer"] ?? string.Empty;
        var label = path;

        if (path.Contains(':'))
        {
            var parts = path.Split(':', 2);
            if (string.IsNullOrEmpty(issuer)) issuer = parts[0];
            label = parts[1];
        }

        var encoder = isSteamScheme || string.Equals(query["encoder"], "steam", StringComparison.OrdinalIgnoreCase)
            ? OtpEncoder.Steam
            : OtpEncoder.Default;
        if (encoder == OtpEncoder.Steam && string.IsNullOrEmpty(issuer))
            issuer = "Steam";

        var digits = int.TryParse(query["digits"], out var d) ? d : 6;
        var period = int.TryParse(query["period"], out var p) ? p : 30;
        var algo = (query["algorithm"]?.ToUpperInvariant()) switch
        {
            "SHA256" => OtpHashAlgorithm.Sha256,
            "SHA512" => OtpHashAlgorithm.Sha512,
            _ => OtpHashAlgorithm.Sha1
        };

        var account = new AuthAccount
        {
            Issuer = issuer,
            Label = label,
            PlaintextSecret = secret,
            VaultName = VaultName,
            Digits = encoder == OtpEncoder.Steam ? 5 : digits,
            Period = period,
            Algorithm = algo,
            Encoder = encoder,
            SortOrder = Accounts.Count > 0 ? Accounts.Max(a => a.SortOrder) + 1 : 0
        };

        Accounts.Add(account);
        SaveToDisk();
        AuditLog.TryAppend(AuditAction.AccountAdded, DisplayName(account));
        return account;
    }

    public void RemoveAccount(string id)
    {
        EnsureAdmin();
        var removed = Accounts.FirstOrDefault(a => a.Id == id);
        Accounts.RemoveAll(a => a.Id == id);
        SaveToDisk();
        if (removed != null)
            AuditLog.TryAppend(AuditAction.AccountRemoved, DisplayName(removed));
    }

    /// <summary>
    /// Encrypt and write the vault back to disk. v1 while no viewer password is
    /// configured; v2 (dual key slots + viewer code windows) once it is.
    /// Uses new random salts/nonces each time. File locking prevents
    /// concurrent write corruption.
    /// </summary>
    private void SaveToDisk()
    {
        EnsureAdmin();
        lock (_ioLock)
        {
            var exportAccounts = Accounts.Select((a, i) => new ExportAccount
            {
                Issuer = a.Issuer,
                Label = a.Label,
                Secret = a.PlaintextSecret ?? string.Empty,
                Digits = a.Digits,
                Period = a.Period,
                Algorithm = a.Algorithm.ToString(),
                Encoder = a.Encoder.ToString(),
                SortOrder = a.SortOrder > 0 ? a.SortOrder : i,
                Domain = a.Domain,
                RequireApproval = a.RequireApproval
            }).ToList();

            var plainBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(exportAccounts, JsonOpts));

            var payloadJson = _viewerSlot == null
                ? SerializeV1(plainBytes, exportAccounts.Count)
                : SerializeV2(plainBytes, exportAccounts.Count);

            // Write with file lock to prevent corruption from concurrent saves
            _lastWriteByUs = DateTime.UtcNow;
            using var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(fs, Encoding.UTF8);
            writer.Write(payloadJson);
        }
    }

    private string SerializeV1(byte[] plainBytes, int accountCount)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(_password, salt);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherText = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherText, tag);

        var payload = new ExportPayload
        {
            AccountCount = accountCount,
            EncryptedData = Convert.ToBase64String(cipherText),
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag)
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private string SerializeV2(byte[] plainBytes, int accountCount)
    {
        var adminSalt = RandomNumberGenerator.GetBytes(SaltSize);
        _viewerGeneratedAt = DateTime.UtcNow;

        var file = new VaultFileV2
        {
            AccountCount = accountCount,
            HorizonHours = HorizonHours,
            Admin = WrapKey(_masterKey!, DeriveKey(_password, adminSalt), adminSalt),
            Viewer = _viewerSlot!,
            ViewerKeyForAdmin = WrapKey(_viewerKey!, _masterKey!, salt: null),
            Payload = SealBlob(plainBytes, _masterKey!),
            ViewerPayload = SealBlob(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(BuildViewerAccounts(), JsonOpts)),
                _viewerKey!),
            ViewerPayloadGeneratedAt = _viewerGeneratedAt.ToString("o")
        };
        return JsonSerializer.Serialize(file, JsonOpts);
    }

    /// <summary>
    /// Precompute every account's codes for the horizon (docs/viewer-role.md).
    /// Starts one step in the past to tolerate viewer clock skew.
    /// </summary>
    private List<ViewerAccount> BuildViewerAccounts()
    {
        var now = DateTimeOffset.UtcNow;
        return Accounts.Select((a, i) =>
        {
            var width = a.Encoder == OtpEncoder.Steam ? 5 : a.Digits;
            var startStep = now.ToUnixTimeSeconds() / a.Period - 1;
            var steps = (long)TimeSpan.FromHours(HorizonHours).TotalSeconds / a.Period + 1;

            var codes = new StringBuilder((int)steps * width);
            for (long s = 0; s < steps; s++)
            {
                var stepTime = DateTimeOffset.FromUnixTimeSeconds((startStep + s) * a.Period);
                codes.Append(TotpGenerator.GenerateCodeAt(a, stepTime).PadLeft(width, '0'));
            }

            return new ViewerAccount
            {
                Issuer = a.Issuer,
                Label = a.Label,
                Digits = a.Digits,
                Period = a.Period,
                Encoder = a.Encoder.ToString(),
                SortOrder = a.SortOrder > 0 ? a.SortOrder : i,
                Domain = a.Domain,
                RequireApproval = a.RequireApproval,
                StartStep = startStep,
                CodeWidth = width,
                Codes = codes.ToString()
            };
        }).ToList();
    }

    // ── AES-GCM helpers ──

    private static KeySlot WrapKey(byte[] key, byte[] wrappingKey, byte[]? salt)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherText = new byte[key.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(wrappingKey, TagSize);
        aes.Encrypt(nonce, key, cipherText, tag);
        return new KeySlot
        {
            Salt = salt == null ? string.Empty : Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            WrappedKey = Convert.ToBase64String(cipherText)
        };
    }

    private static byte[] UnwrapKey(KeySlot slot, byte[] wrappingKey)
    {
        var cipherText = Convert.FromBase64String(slot.WrappedKey);
        var key = new byte[cipherText.Length];
        using var aes = new AesGcm(wrappingKey, TagSize);
        aes.Decrypt(Convert.FromBase64String(slot.Nonce), cipherText,
            Convert.FromBase64String(slot.Tag), key);
        return key;
    }

    private static SealedBlob SealBlob(byte[] plainBytes, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherText = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherText, tag);
        return new SealedBlob
        {
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipherText)
        };
    }

    private static byte[] OpenBlob(SealedBlob blob, byte[] key)
    {
        var cipherText = Convert.FromBase64String(blob.Data);
        var plainBytes = new byte[cipherText.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(Convert.FromBase64String(blob.Nonce), cipherText,
            Convert.FromBase64String(blob.Tag), plainBytes);
        return plainBytes;
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(FilePath);
        var name = Path.GetFileName(FilePath);
        if (dir == null) return;

        _watcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: ignore our own writes (within 2 seconds)
        if ((DateTime.UtcNow - _lastWriteByUs).TotalSeconds < 2)
            return;

        // Small delay to let the write finish (network shares can be slow)
        Thread.Sleep(500);

        try
        {
            Reload();
            ExternalChange?.Invoke();
        }
        catch
        {
            // File might be mid-write by another user — ignore, next change will catch it
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        IsOpen = false;

        // Clear key material and plaintext secrets from memory
        if (_masterKey != null) CryptographicOperations.ZeroMemory(_masterKey);
        if (_viewerKey != null) CryptographicOperations.ZeroMemory(_viewerKey);
        _masterKey = null;
        _viewerKey = null;
        foreach (var account in Accounts)
            account.PlaintextSecret = null;
    }
}
