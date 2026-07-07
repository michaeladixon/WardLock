using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WardLock.Models;

namespace WardLock.Services;

/// <summary>
/// Manages shared vault files. Each vault is an AES-256-GCM encrypted .wardlock file
/// that can live on a network share, OneDrive, or SharePoint. Multiple team members
/// can open the same vault, and a FileSystemWatcher detects external changes.
/// 
/// Secrets are held in plaintext in memory (never DPAPI'd) because they need
/// to be re-encrypted with the vault password on save, not bound to one user's profile.
/// </summary>
public class SharedVaultService : IDisposable
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 600_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string FilePath { get; }
    public string VaultName { get; }
    public List<AuthAccount> Accounts { get; private set; } = [];
    public bool IsOpen { get; private set; }

    /// <summary>Tamper-evident sidecar audit log (issue #3).</summary>
    public VaultAuditLog AuditLog { get; }

    private string _password = string.Empty;
    private FileSystemWatcher? _watcher;
    private DateTime _lastWriteByUs = DateTime.MinValue;

    /// <summary>Fires when the vault file is modified externally (by a teammate).</summary>
    public event Action? ExternalChange;

    public SharedVaultService(string filePath)
    {
        FilePath = filePath;
        VaultName = Path.GetFileNameWithoutExtension(filePath);
        AuditLog = new VaultAuditLog(filePath);
    }

    /// <summary>
    /// Open an existing vault file. Decrypts with the given password and holds
    /// accounts in memory with plaintext secrets.
    /// </summary>
    public void Open(string password)
    {
        _password = password;
        Reload();
        StartWatching();
        IsOpen = true;
        AuditLog.TryAppend(AuditAction.VaultOpened);
    }

    /// <summary>
    /// Create a new empty vault file, encrypted with the given password.
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
        var payloadJson = File.ReadAllText(FilePath);
        var payload = JsonSerializer.Deserialize<ExportPayload>(payloadJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid vault file.");

        var salt = Convert.FromBase64String(payload.Salt);
        var nonce = Convert.FromBase64String(payload.Nonce);
        var tag = Convert.FromBase64String(payload.Tag);
        var cipherText = Convert.FromBase64String(payload.EncryptedData);

        var key = DeriveKey(_password, salt);
        var plainBytes = new byte[cipherText.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherText, tag, plainBytes);

        var plainJson = Encoding.UTF8.GetString(plainBytes);
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
            Domain = e.Domain
        }).ToList();
    }

    /// <summary>
    /// Add an account to this shared vault and persist to disk.
    /// Accepts a plaintext Base32 secret (NOT a DPAPI-encrypted one).
    /// </summary>
    public void AddAccount(string issuer, string label, string plaintextSecret,
        int digits = 6, int period = 30, OtpHashAlgorithm algorithm = OtpHashAlgorithm.Sha1,
        OtpEncoder encoder = OtpEncoder.Default, string? domain = null)
    {
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
        var account = Accounts.FirstOrDefault(a => a.Id == id);
        if (account == null) return;
        account.Domain = domain;
        SaveToDisk();
        AuditLog.TryAppend(AuditAction.DomainChanged, DisplayName(account), domain ?? "(cleared)");
    }

    /// <summary>Toggle the number-matched fill approval requirement on a vault account and persist.</summary>
    public void UpdateAccountApproval(string id, bool requireApproval)
    {
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
        var removed = Accounts.FirstOrDefault(a => a.Id == id);
        Accounts.RemoveAll(a => a.Id == id);
        SaveToDisk();
        if (removed != null)
            AuditLog.TryAppend(AuditAction.AccountRemoved, DisplayName(removed));
    }

    /// <summary>
    /// Encrypt and write the vault back to disk. Uses a new random salt/nonce each time.
    /// File locking prevents concurrent write corruption.
    /// </summary>
    private void SaveToDisk()
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
            Domain = a.Domain
        }).ToList();

        var plainJson = JsonSerializer.Serialize(exportAccounts, JsonOpts);
        var plainBytes = Encoding.UTF8.GetBytes(plainJson);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(_password, salt);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherText = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherText, tag);

        var payload = new ExportPayload
        {
            AccountCount = exportAccounts.Count,
            EncryptedData = Convert.ToBase64String(cipherText),
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag)
        };

        var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);

        // Write with file lock to prevent corruption from concurrent saves
        _lastWriteByUs = DateTime.UtcNow;
        using var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(fs, Encoding.UTF8);
        writer.Write(payloadJson);
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
        IsOpen = false;

        // Clear plaintext secrets from memory
        foreach (var account in Accounts)
            account.PlaintextSecret = null;
    }
}
