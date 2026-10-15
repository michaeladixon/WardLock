using OtpNet;
using WardLock.Models;

namespace WardLock.Services;

/// <summary>TOTP parameters parsed from an otpauth:// URI, with the secret in plaintext Base32.</summary>
public sealed record OtpParameters(
    string Issuer,
    string Label,
    string Secret,
    int Digits,
    int Period,
    OtpHashAlgorithm Algorithm,
    OtpEncoder Encoder);

/// <summary>
/// Parsing and validation for otpauth:// URIs and Base32 secrets. Every path
/// that creates an account (URI, manual entry, backup import, vault) goes
/// through here, so an account that can't produce a code is rejected up front
/// instead of throwing from the refresh timer on every tick and every launch.
/// </summary>
public static class OtpAuthUri
{
    // Otp.NET's supported range (it throws outside 1..10).
    public const int MinDigits = 1;
    public const int MaxDigits = 10;
    public const int MinPeriod = 1;
    public const int MaxPeriod = 3600;

    public static OtpParameters Parse(string uri)
    {
        var isSteamScheme = uri.StartsWith("otpauth://steam/", StringComparison.OrdinalIgnoreCase);
        if (!uri.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase) && !isSteamScheme)
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

        if (encoder == OtpEncoder.Steam) digits = 5;
        Validate(digits, period);

        return new OtpParameters(issuer, label, NormalizeSecret(secret), digits, period, algo, encoder);
    }

    /// <summary>
    /// Canonical Base32 secret: whitespace and hyphens removed (issuers often
    /// group the secret for display), upper-cased. Throws ArgumentException when
    /// the result isn't decodable Base32.
    /// </summary>
    public static string NormalizeSecret(string? secret)
    {
        var normalized = new string((secret ?? string.Empty)
            .Where(c => !char.IsWhiteSpace(c) && c != '-')
            .ToArray())
            .ToUpperInvariant();

        var body = normalized.TrimEnd('=');
        if (body.Length == 0)
            throw new ArgumentException("The secret is empty.");
        if (body.Any(c => c is not (>= 'A' and <= 'Z') and not (>= '2' and <= '7')))
            throw new ArgumentException("The secret isn't valid Base32 (only A–Z and 2–7 are allowed).");
        if (Base32Encoding.ToBytes(normalized).Length == 0)
            throw new ArgumentException("The secret is too short.");

        return normalized;
    }

    public static void Validate(int digits, int period)
    {
        if (digits is < MinDigits or > MaxDigits)
            throw new ArgumentException($"Unsupported code length {digits} (must be {MinDigits}–{MaxDigits}).");
        if (period is < MinPeriod or > MaxPeriod)
            throw new ArgumentException($"Unsupported period {period}s (must be {MinPeriod}–{MaxPeriod}).");
    }
}
