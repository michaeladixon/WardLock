using WardLock.Services;

namespace WardLock.Tests;

public class DomainMatcherTests
{
    [Theory]
    [InlineData("github.com", "github.com")]
    [InlineData("GitHub.COM", "github.com")]
    [InlineData("  https://github.com/login?x=1  ", "github.com")]
    [InlineData("github.com.", "github.com")]
    [InlineData("github.com:443", "github.com")]
    public void Normalize_reduces_input_to_bare_lowercase_host(string input, string expected)
        => Assert.Equal(expected, DomainMatcher.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("https:///")]
    public void Normalize_rejects_unusable_input(string? input)
        => Assert.Null(DomainMatcher.Normalize(input));

    [Theory]
    [InlineData("github.com", "github.com")]
    [InlineData("gist.github.com", "github.com")]
    [InlineData("a.b.github.com", "github.com")]
    [InlineData("GITHUB.com.", "github.com")]
    public void Matches_domain_and_its_subdomains(string host, string domain)
        => Assert.True(DomainMatcher.Matches(host, domain));

    [Theory]
    [InlineData("github.com.evil.com", "github.com")]   // suffix-append phishing
    [InlineData("evilgithub.com", "github.com")]        // no label boundary
    [InlineData("github.co", "github.com")]
    [InlineData("com", "github.com")]
    [InlineData("github.com", "gist.github.com")]       // parent never matches child
    [InlineData("github.com@evil.com", "evil.com")]     // userinfo-shaped junk fails closed
    [InlineData(null, "github.com")]
    [InlineData("github.com", null)]
    public void Rejects_lookalikes_and_unrelated_hosts(string? host, string? domain)
        => Assert.False(DomainMatcher.Matches(host, domain));
}
