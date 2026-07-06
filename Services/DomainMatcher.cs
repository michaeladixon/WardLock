namespace WardLock.Services;

/// <summary>
/// Domain matching for browser-fill (issue #1, Tier 2). An account stores its
/// registrable domain (eTLD+1, e.g. "github.com"); a page hostname matches only
/// when it equals that domain or is a subdomain of it. Matching is anchored at
/// label boundaries — never substring — so "github.com.evil.com" can NOT match
/// a "github.com" account. This is what makes browser fill phishing-resistant.
/// </summary>
public static class DomainMatcher
{
    /// <summary>
    /// Normalize user/browser input to a bare lowercase hostname:
    /// trims whitespace, strips scheme, path, port, and trailing dots.
    /// Returns null for empty or unusable input.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var s = input.Trim().ToLowerInvariant();

        // Allow pasting a full URL ("https://github.com/login")
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];

        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];

        s = s.TrimEnd('.');

        if (s.Length == 0 || !s.Contains('.')) return null;
        return s;
    }

    /// <summary>
    /// True when <paramref name="pageHost"/> is the account's domain itself or a
    /// subdomain of it. Both inputs are normalized before comparison.
    /// </summary>
    public static bool Matches(string? pageHost, string? accountDomain)
    {
        var host = Normalize(pageHost);
        var domain = Normalize(accountDomain);
        if (host == null || domain == null) return false;

        return host == domain
            || host.EndsWith("." + domain, StringComparison.Ordinal);
    }
}
