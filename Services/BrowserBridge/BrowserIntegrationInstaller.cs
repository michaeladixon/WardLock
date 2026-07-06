using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WardLock.Services.BrowserBridge;

/// <summary>
/// Registers WardLock.exe as a Chrome/Edge native messaging host for the
/// WardLock browser extension: writes the host manifest JSON next to the app
/// settings and points HKCU registry keys at it. Per-user, no elevation needed.
/// </summary>
public static class BrowserIntegrationInstaller
{
    public const string HostName = "com.wardlock.wardlock";

    /// <summary>
    /// Stable ID of the WardLock extension, derived from the public key pinned in
    /// BrowserExtension/manifest.json. Must be kept in sync with that file.
    /// </summary>
    public const string ExtensionId = "hcbclfghekjpdgnbfnmfeaamigencjjf";

    private static readonly string ManifestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WardLock", HostName + ".json");

    private static readonly string[] RegistryKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
    ];

    public static bool IsInstalled()
    {
        if (!File.Exists(ManifestPath)) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeys[0]);
        return key?.GetValue(null) as string == ManifestPath;
    }

    /// <summary>
    /// Writes the host manifest and registry keys. Safe to re-run (e.g. after the
    /// app moved) — it simply overwrites with the current executable path.
    /// </summary>
    public static void Install()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine WardLock.exe path.");

        var manifest = new
        {
            name = HostName,
            description = "WardLock browser integration — domain-verified TOTP fill",
            path = exePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        File.WriteAllText(ManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var keyPath in RegistryKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(null, ManifestPath);
        }
    }

    /// <summary>
    /// Extension origins allowed to talk to the pipe server. Read from the
    /// installed manifest so the app-side check (defense in depth on top of the
    /// browser's own allowed_origins enforcement) can't drift from what Chrome uses.
    /// </summary>
    public static HashSet<string> GetAllowedOrigins()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            return doc.RootElement.GetProperty("allowed_origins")
                .EnumerateArray()
                .Select(o => o.GetString())
                .Where(o => !string.IsNullOrEmpty(o))
                .Select(o => o!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }
}
