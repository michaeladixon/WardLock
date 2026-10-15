using WardLock.Models;
using WardLock.Services;

namespace WardLock.Tests;

/// <summary>
/// RFC 6238 Appendix B test vectors (8 digits, 30 s step), plus a Steam Guard
/// cross-check computed with an independent implementation of the same
/// algorithm (HMAC-SHA1 dynamic truncation, 5 chars over Steam's alphabet).
/// </summary>
public class TotpGeneratorTests
{
    private const string Sha1Seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private const string Sha256Seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA====";
    private const string Sha512Seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNA=";

    private static AuthAccount Account(string seed, OtpHashAlgorithm algorithm, int digits = 8) => new()
    {
        PlaintextSecret = seed,
        Algorithm = algorithm,
        Digits = digits,
        Period = 30,
    };

    [Theory]
    [InlineData(59L, "94287082", "46119246", "90693936")]
    [InlineData(1111111109L, "07081804", "68084774", "25091201")]
    [InlineData(1111111111L, "14050471", "67062674", "99943326")]
    [InlineData(1234567890L, "89005924", "91819424", "93441116")]
    [InlineData(2000000000L, "69279037", "90698825", "38618901")]
    [InlineData(20000000000L, "65353130", "77737706", "47863826")]
    public void Rfc6238_vectors(long unixSeconds, string sha1, string sha256, string sha512)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(sha1, TotpGenerator.GenerateCodeAt(Account(Sha1Seed, OtpHashAlgorithm.Sha1), t));
        Assert.Equal(sha256, TotpGenerator.GenerateCodeAt(Account(Sha256Seed, OtpHashAlgorithm.Sha256), t));
        Assert.Equal(sha512, TotpGenerator.GenerateCodeAt(Account(Sha512Seed, OtpHashAlgorithm.Sha512), t));
    }

    [Fact]
    public void Six_digit_code_is_low_order_digits_of_the_eight_digit_code()
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.Equal("287082", TotpGenerator.GenerateCodeAt(Account(Sha1Seed, OtpHashAlgorithm.Sha1, digits: 6), t));
    }

    [Theory]
    [InlineData(59L, "PV9M4")]
    [InlineData(1234567890L, "VHHQY")]
    public void Steam_codes_use_five_chars_from_steam_alphabet(long unixSeconds, string expected)
    {
        var account = Account(Sha1Seed, OtpHashAlgorithm.Sha1, digits: 5);
        account.Encoder = OtpEncoder.Steam;
        var code = TotpGenerator.GenerateCodeAt(account, DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(expected, code);
    }
}
