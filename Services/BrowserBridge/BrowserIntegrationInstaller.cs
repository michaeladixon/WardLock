using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WardLock.Services.BrowserBridge;

/// <summary>
/// Registers WardLock.exe as the native messaging host for the WardLock browser
/// extension in Chrome, Edge and Firefox: writes the host manifests next to the
/// app settings and points HKCU registry keys at them. Per-user, no elevation.
///
/// Chromium and Firefox need different manifests: Chromium authorizes callers
/// by <c>allowed_origins</c> (chrome-extension://id/), Firefox by
/// <c>allowed_extensions</c> (the gecko add-on ID).
/// </summary>
public static class BrowserIntegrationInstaller
{
    public const string HostName = "com.wardlock.wardlock";

    /// <summary>
    /// Stable ID of the WardLock extension in Chrome/Edge, derived from the public
    /// key pinned in BrowserExtension/manifest.json. Must be kept in sync with that file.
    /// </summary>
    public const string ExtensionId = "hcbclfghekjpdgnbfnmfeaamigencjjf";

    /// <summary>
    /// Firefox add-on ID (browser_specific_settings.gecko.id in
    /// BrowserExtension/manifest.firefox.json). Must be kept in sync with that file.
    /// </summary>
    public const string FirefoxExtensionId = "wardlock@wardlock.app";

    /// <summary>
    /// The identity a Firefox-launched proxy presents in its hello. Firefox's
    /// moz-extension:// origins are random per install, so the add-on ID is used.
    /// </summary>
    public static string FirefoxOrigin(string extensionId) => "firefox:" + extensionId;

    private static readonly string HostDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WardLock");

    private static readonly string ChromiumManifestPath = Path.Combine(HostDir, HostName + ".json");
    private static readonly string FirefoxManifestPath = Path.Combine(HostDir, HostName + ".firefox.json");

    private static readonly (string KeyPath, string ManifestPath)[] Registrations =
    [
        (@"Software\Google\Chrome\NativeMessagingHosts\" + HostName, ChromiumManifestPath),
        (@"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName, ChromiumManifestPath),
        (@"Software\Mozilla\NativeMessagingHosts\" + HostName, FirefoxManifestPath),
    ];

    /// <summary>
    /// True when every browser's registration points at an existing manifest.
    /// Installs from before Firefox support report false, so the UI offers to
    /// re-run the (idempotent) install.
    /// </summary>
    public static bool IsInstalled()
    {
        foreach (var (keyPath, manifestPath) in Registrations)
        {
            if (!File.Exists(manifestPath)) return false;
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key?.GetValue(null) as string != manifestPath) return false;
        }
        return true;
    }

    /// <summary>
    /// Writes the host manifests and registry keys. Safe to re-run (e.g. after the
    /// app moved) — it simply overwrites with the current executable path.
    /// </summary>
    public static void Install()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine WardLock.exe path.");

        const string description = "WardLock browser integration — domain-verified TOTP fill";
        var options = new JsonSerializerOptions { WriteIndented = true };

        AtomicFile.WriteAllText(ChromiumManifestPath, JsonSerializer.Serialize(new
        {
            name = HostName,
            description,
            path = exePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
        }, options));

        AtomicFile.WriteAllText(FirefoxManifestPath, JsonSerializer.Serialize(new
        {
            name = HostName,
            description,
            path = exePath,
            type = "stdio",
            allowed_extensions = new[] { FirefoxExtensionId }
        }, options));

        foreach (var (keyPath, manifestPath) in Registrations)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(null, manifestPath);
        }
    }

    /// <summary>
    /// Identities allowed to talk to the pipe server, read back from the installed
    /// manifests so the app-side check can't drift from what the browsers enforce:
    /// Chromium <c>allowed_origins</c> as-is, Firefox <c>allowed_extensions</c> as
    /// <see cref="FirefoxOrigin"/>.
    /// </summary>
    public static HashSet<string> GetAllowedOrigins()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in ReadStringArray(ChromiumManifestPath, "allowed_origins"))
            allowed.Add(origin);
        foreach (var id in ReadStringArray(FirefoxManifestPath, "allowed_extensions"))
            allowed.Add(FirefoxOrigin(id));
        return allowed;
    }

    private static List<string> ReadStringArray(string manifestPath, string property)
    {
        try
        {
            if (!File.Exists(manifestPath)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
                return [];
            return array.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => s.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
