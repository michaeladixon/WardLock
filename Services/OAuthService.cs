using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace WardLock.Services;

/// <summary>
/// OAuth 2.0 + PKCE authentication for desktop apps (RFC 7636 / RFC 8252).
/// Opens the system browser, starts a localhost loopback listener to capture the
/// authorization code, then exchanges it for tokens and fetches the user identity.
///
/// Each provider requires a registered OAuth app with http://localhost redirect support:
///   Google   — "Desktop app" type in Google Cloud Console
///   Microsoft — "Mobile and desktop applications" platform in Azure AD app registration
///   Facebook  — "Desktop" app type; requires your app to be approved for Login
///
/// Set the client IDs below before shipping.
/// </summary>
public static class OAuthService
{
    // ── Configure these before shipping ──────────────────────────────────────
    public const string GoogleClientId    = "";   // Google Cloud Console → Credentials → OAuth 2.0 Client ID (Desktop app)
    public const string MicrosoftClientId = "";   // Azure portal → App registrations → Application (client) ID
    public const string FacebookClientId  = "";   // developers.facebook.com → App ID
    // ─────────────────────────────────────────────────────────────────────────

    public enum Provider { Google, Microsoft, Facebook }

    public record OAuthIdentity(Provider Provider, string Sub, string Email, string Name);

    private record ProviderConfig(
        string ClientId,
        string AuthEndpoint,
        string TokenEndpoint,
        string UserInfoEndpoint,
        string Scopes);

    private static readonly Dictionary<Provider, ProviderConfig> Configs = new()
    {
        [Provider.Google] = new(
            GoogleClientId,
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            "https://www.googleapis.com/oauth2/v3/userinfo",
            "openid profile email"),

        [Provider.Microsoft] = new(
            MicrosoftClientId,
            "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            "https://graph.microsoft.com/oidc/userinfo",
            "openid profile email"),

        [Provider.Facebook] = new(
            FacebookClientId,
            "https://www.facebook.com/v19.0/dialog/oauth",
            "https://graph.facebook.com/v19.0/oauth/access_token",
            "https://graph.facebook.com/me?fields=id,name,email",
            "public_profile,email"),
    };

    public static bool IsConfigured(Provider provider)
        => !string.IsNullOrWhiteSpace(Configs[provider].ClientId);

    /// <summary>
    /// Full PKCE authorization code flow. Opens the system browser, waits for the
    /// loopback callback (up to <paramref name="timeoutSeconds"/> seconds), then
    /// exchanges the code and returns the user identity — or null if cancelled/failed.
    /// </summary>
    public static async Task<OAuthIdentity?> AuthenticateAsync(
        Provider provider, int timeoutSeconds = 120, CancellationToken ct = default)
    {
        var config = Configs[provider];
        if (string.IsNullOrWhiteSpace(config.ClientId))
            throw new InvalidOperationException($"{provider} client ID is not configured.");

        var port        = GetFreePort();
        var redirectUri = $"http://localhost:{port}/";

        // PKCE
        var verifier   = GenerateCodeVerifier();
        var challenge  = GenerateCodeChallenge(verifier);
        var state      = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

        var authUrl = BuildAuthUrl(config, challenge, state, redirectUri);

        // Open browser
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        // Wait for loopback callback
        var code = await WaitForCodeAsync(port, state, redirectUri, timeoutSeconds, ct);
        if (code is null) return null;

        // Exchange code for tokens
        using var http   = new HttpClient();
        var tokenResp    = await ExchangeCodeAsync(http, config, code, verifier, redirectUri, ct);
        if (tokenResp is null) return null;

        // Fetch user info
        return await GetIdentityAsync(http, config, provider, tokenResp, ct);
    }

    // ── URL builders ─────────────────────────────────────────────────────────

    private static string BuildAuthUrl(ProviderConfig cfg, string challenge, string state, string redirectUri)
    {
        var q = HttpUtility.ParseQueryString(string.Empty);
        q["client_id"]             = cfg.ClientId;
        q["redirect_uri"]          = redirectUri;
        q["response_type"]         = "code";
        q["scope"]                 = cfg.Scopes;
        q["state"]                 = state;
        q["code_challenge"]        = challenge;
        q["code_challenge_method"] = "S256";
        return $"{cfg.AuthEndpoint}?{q}";
    }

    // ── Loopback listener ────────────────────────────────────────────────────

    private static async Task<string?> WaitForCodeAsync(
        int port, string expectedState, string redirectUri, int timeoutSeconds, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);

        try { listener.Start(); }
        catch { return null; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var context = await listener.GetContextAsync().WaitAsync(cts.Token);

            // Send a close-the-tab page
            var responseHtml = "<html><body style='font-family:sans-serif;text-align:center;margin-top:80px'>"
                             + "<h2>You can close this tab and return to WardLock.</h2></body></html>";
            var responseBytes = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType     = "text/html; charset=utf-8";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, cts.Token);
            context.Response.Close();

            var query = context.Request.QueryString;
            if (query["state"] != expectedState) return null;
            if (!string.IsNullOrEmpty(query["error"])) return null;

            return query["code"];
        }
        catch (OperationCanceledException) { return null; }
        finally { listener.Stop(); }
    }

    // ── Token exchange ───────────────────────────────────────────────────────

    private static async Task<JsonDocument?> ExchangeCodeAsync(
        HttpClient http, ProviderConfig cfg, string code,
        string verifier, string redirectUri, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]     = cfg.ClientId,
            ["code"]          = code,
            ["code_verifier"] = verifier,
            ["grant_type"]    = "authorization_code",
            ["redirect_uri"]  = redirectUri,
        });

        try
        {
            var resp = await http.PostAsync(cfg.TokenEndpoint, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json);
        }
        catch { return null; }
    }

    // ── User info ────────────────────────────────────────────────────────────

    private static async Task<OAuthIdentity?> GetIdentityAsync(
        HttpClient http, ProviderConfig cfg, Provider provider,
        JsonDocument tokenDoc, CancellationToken ct)
    {
        try
        {
            var root        = tokenDoc.RootElement;
            var accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;

            http.DefaultRequestHeaders.Remove("Authorization");
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            if (provider == Provider.Facebook)
                http.DefaultRequestHeaders.Remove("Authorization"); // Facebook uses access_token query param

            var resp = await http.GetAsync(cfg.UserInfoEndpoint, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var u = doc.RootElement;

            string sub   = TryGet(u, "sub", "id")     ?? accessToken[..16];
            string email = TryGet(u, "email")          ?? string.Empty;
            string name  = TryGet(u, "name", "login")  ?? email;

            return new OAuthIdentity(provider, sub, email, name);
        }
        catch { return null; }
    }

    private static string? TryGet(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    // ── PKCE helpers ─────────────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string GenerateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
