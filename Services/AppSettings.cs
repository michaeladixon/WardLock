using System.IO;
using System.Text.Json;

namespace WardLock.Services;

public enum LockMethod
{
    None,           // No lock — app opens directly
    Password,       // App-level password (PBKDF2 hash)
    WindowsHello,   // Windows Hello: fingerprint, face, or PIN
    OAuthGoogle,
    OAuthMicrosoft,
    OAuthFacebook,
}

/// <summary>
/// Persists app-level settings (not secrets) to a JSON file.
/// </summary>
public static class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WardLock", "settings.json");

    private static Dictionary<string, string> _settings = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch
        {
            _settings = new();
        }
    }

    private static void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool IsWindowsHelloEnabled
    {
        get
        {
            EnsureLoaded();
            return _settings.TryGetValue("WindowsHelloEnabled", out var v) && v == "true";
        }
        set
        {
            EnsureLoaded();
            _settings["WindowsHelloEnabled"] = value ? "true" : "false";
            Save();
        }
    }

    public static bool MinimizeToTray
    {
        get
        {
            EnsureLoaded();
            return !_settings.TryGetValue("MinimizeToTray", out var v) || v != "false"; // default true
        }
        set
        {
            EnsureLoaded();
            _settings["MinimizeToTray"] = value ? "true" : "false";
            Save();
        }
    }

    /// <summary>
    /// Auto-lock timeout in minutes. 0 = disabled. Default: 5.
    /// </summary>
    public static int AutoLockTimeoutMinutes
    {
        get
        {
            EnsureLoaded();
            if (_settings.TryGetValue("AutoLockTimeoutMinutes", out var v) && int.TryParse(v, out var m))
                return m;
            return 5; // default 5 minutes
        }
        set
        {
            EnsureLoaded();
            _settings["AutoLockTimeoutMinutes"] = value.ToString();
            Save();
        }
    }

    /// <summary>
    /// Recently opened shared vault file paths (pipe-delimited).
    /// Stored as paths only — passwords are never persisted here.
    /// </summary>
    public static List<string> RecentVaultPaths
    {
        get
        {
            EnsureLoaded();
            if (_settings.TryGetValue("RecentVaultPaths", out var v) && !string.IsNullOrEmpty(v))
                return v.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            return [];
        }
        set
        {
            EnsureLoaded();
            // Keep at most 10, deduplicated
            var paths = value.Distinct().Take(10).ToList();
            _settings["RecentVaultPaths"] = string.Join('|', paths);
            Save();
        }
    }

    /// <summary>
    /// Vault paths the user has opted to auto-open on unlock (pipe-delimited).
    /// Passwords are DPAPI-cached separately in VaultPasswordCache.
    /// </summary>
    public static List<string> RememberedVaultPaths
    {
        get
        {
            EnsureLoaded();
            if (_settings.TryGetValue("RememberedVaultPaths", out var v) && !string.IsNullOrEmpty(v))
                return v.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            return [];
        }
        set
        {
            EnsureLoaded();
            var paths = value.Distinct().Take(10).ToList();
            _settings["RememberedVaultPaths"] = string.Join('|', paths);
            Save();
        }
    }

    public static void AddRememberedVaultPath(string path)
    {
        var paths = RememberedVaultPaths;
        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
            RememberedVaultPaths = paths;
        }
    }

    public static void RemoveRememberedVaultPath(string path)
    {
        var paths = RememberedVaultPaths;
        paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RememberedVaultPaths = paths;
    }

    public static void AddRecentVaultPath(string path)
    {
        var paths = RecentVaultPaths;
        paths.Remove(path); // Remove if already present (will re-add at top)
        paths.Insert(0, path);
        RecentVaultPaths = paths;
    }

    public static void RemoveRecentVaultPath(string path)
    {
        var paths = RecentVaultPaths;
        paths.Remove(path);
        RecentVaultPaths = paths;
    }

    public static LockMethod ActiveLockMethod
    {
        get
        {
            EnsureLoaded();
            if (_settings.TryGetValue("LockMethod", out var v) &&
                Enum.TryParse<LockMethod>(v, out var m))
                return m;

            // Migrate legacy WindowsHello setting
            if (_settings.TryGetValue("WindowsHelloEnabled", out var wh) && wh == "true")
                return LockMethod.WindowsHello;

            return LockMethod.None;
        }
        set
        {
            EnsureLoaded();
            _settings["LockMethod"] = value.ToString();
            Save();
        }
    }

    /// <summary>PBKDF2 hash of the app lock password: "base64salt:base64hash".</summary>
    public static string? LockPasswordHash
    {
        get { EnsureLoaded(); return _settings.TryGetValue("LockPasswordHash", out var v) ? v : null; }
        set { EnsureLoaded(); if (value is null) _settings.Remove("LockPasswordHash"); else _settings["LockPasswordHash"] = value; Save(); }
    }

    /// <summary>Subject identifier (sub) stored after successful OAuth setup.</summary>
    public static string? OAuthSub
    {
        get { EnsureLoaded(); return _settings.TryGetValue("OAuthSub", out var v) ? v : null; }
        set { EnsureLoaded(); if (value is null) _settings.Remove("OAuthSub"); else _settings["OAuthSub"] = value; Save(); }
    }

    /// <summary>Display name / email shown on the lock screen for OAuth.</summary>
    public static string? OAuthDisplayName
    {
        get { EnsureLoaded(); return _settings.TryGetValue("OAuthDisplayName", out var v) ? v : null; }
        set { EnsureLoaded(); if (value is null) _settings.Remove("OAuthDisplayName"); else _settings["OAuthDisplayName"] = value; Save(); }
    }
}
