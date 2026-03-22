using System.IO;
using System.Text.Json;
using WardLock.Models;

namespace WardLock.Services;

public class AccountStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WardLock", "accounts.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public List<AuthAccount> Accounts { get; private set; } = [];

    public void Load()
    {
        if (!File.Exists(StorePath))
        {
            Accounts = [];
            return;
        }

        var json = File.ReadAllText(StorePath);
        Accounts = JsonSerializer.Deserialize<List<AuthAccount>>(json, JsonOpts) ?? [];
        // Sort by SortOrder on load
        Accounts = Accounts.OrderBy(a => a.SortOrder).ToList();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(Accounts, JsonOpts);
        File.WriteAllText(StorePath, json);
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
    /// Parse an otpauth:// URI (from QR code or manual paste).
    /// Format: otpauth://totp/Issuer:label?secret=BASE32&issuer=Issuer&algorithm=SHA1&digits=6&period=30
    /// </summary>
    public static AuthAccount ParseOtpAuthUri(string uri)
    {
        if (!uri.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only TOTP URIs are supported.");

        var uriObj = new Uri(uri);
        var path = Uri.UnescapeDataString(uriObj.AbsolutePath.TrimStart('/'));
        var query = System.Web.HttpUtility.ParseQueryString(uriObj.Query);

        var secret = query["secret"] ?? throw new ArgumentException("Missing secret parameter.");
        var issuer = query["issuer"] ?? string.Empty;
        var label = path;

        // If path is "Issuer:user@example.com", split it
        if (path.Contains(':'))
        {
            var parts = path.Split(':', 2);
            if (string.IsNullOrEmpty(issuer)) issuer = parts[0];
            label = parts[1];
        }

        var digits = int.TryParse(query["digits"], out var d) ? d : 6;
        var period = int.TryParse(query["period"], out var p) ? p : 30;
        var algo = (query["algorithm"]?.ToUpperInvariant()) switch
        {
            "SHA256" => OtpHashAlgorithm.Sha256,
            "SHA512" => OtpHashAlgorithm.Sha512,
            _ => OtpHashAlgorithm.Sha1
        };

        return new AuthAccount
        {
            Issuer = issuer,
            Label = label,
            EncryptedSecret = SecretVault.Encrypt(secret),
            Digits = digits,
            Period = period,
            Algorithm = algo
        };
    }
}
