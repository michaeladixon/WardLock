using System.IO;
using System.Text.Json;
using WardLock.Models;

namespace WardLock.Services;

public class AccountStore
{
    private static readonly string DefaultStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WardLock", "accounts.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _storePath;

    public AccountStore(string? storePath = null)
    {
        _storePath = storePath ?? DefaultStorePath;
    }

    public List<AuthAccount> Accounts { get; private set; } = [];

    /// <summary>
    /// Set by <see cref="Load"/> when accounts.json was unreadable and has been
    /// moved aside; null after a normal load.
    /// </summary>
    public string? LoadWarning { get; private set; }

    public void Load()
    {
        LoadWarning = null;
        if (!File.Exists(_storePath))
        {
            Accounts = [];
            return;
        }

        List<AuthAccount>? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<List<AuthAccount>>(File.ReadAllText(_storePath), JsonOpts);
        }
        catch (JsonException)
        {
            // Never let the next Save() overwrite a damaged-but-maybe-recoverable
            // file: move it aside and start empty.
            var movedTo = AtomicFile.Quarantine(_storePath);
            LoadWarning = $"accounts.json was unreadable and was moved to {Path.GetFileName(movedTo)}. Restore a backup or contact support to recover it.";
            Accounts = [];
            return;
        }

        // Sort by SortOrder on load
        Accounts = (loaded ?? []).OrderBy(a => a.SortOrder).ToList();
    }

    public void Save()
    {
        AtomicFile.WriteAllText(_storePath, JsonSerializer.Serialize(Accounts, JsonOpts));
    }

    public void Add(AuthAccount account)
    {
        account.SortOrder = Accounts.Count > 0 ? Accounts.Max(a => a.SortOrder) + 1 : 0;
        Accounts.Add(account);
        Save();
    }

    public void AddRange(IEnumerable<AuthAccount> accounts)
    {
        var startOrder = Accounts.Count > 0 ? Accounts.Max(a => a.SortOrder) + 1 : 0;
        foreach (var account in accounts)
        {
            account.SortOrder = startOrder++;
            Accounts.Add(account);
        }
        Save();
    }

    public void Remove(string id)
    {
        Accounts.RemoveAll(a => a.Id == id);
        ReindexSortOrder();
        Save();
    }

    /// <summary>
    /// Move an account from one index to another (for drag-and-drop).
    /// </summary>
    public void Move(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Accounts.Count) return;
        if (toIndex < 0 || toIndex >= Accounts.Count) return;
        if (fromIndex == toIndex) return;

        var item = Accounts[fromIndex];
        Accounts.RemoveAt(fromIndex);
        Accounts.Insert(toIndex, item);
        ReindexSortOrder();
        Save();
    }

    private void ReindexSortOrder()
    {
        for (int i = 0; i < Accounts.Count; i++)
            Accounts[i].SortOrder = i;
    }

    /// <summary>
    /// Parse an otpauth:// URI into a personal account (secret DPAPI-encrypted).
    /// Throws ArgumentException for unsupported or invalid URIs.
    /// </summary>
    public static AuthAccount ParseOtpAuthUri(string uri)
    {
        var p = OtpAuthUri.Parse(uri);
        return new AuthAccount
        {
            Issuer = p.Issuer,
            Label = p.Label,
            EncryptedSecret = SecretVault.Encrypt(p.Secret),
            Digits = p.Digits,
            Period = p.Period,
            Algorithm = p.Algorithm,
            Encoder = p.Encoder
        };
    }
}
