using WardLock.Models;
using WardLock.Services;

namespace WardLock.Tests;

public class OtpAuthUriTests
{
    [Fact]
    public void Parses_issuer_label_and_parameters()
    {
        var a = AccountStore.ParseOtpAuthUri(
            "otpauth://totp/ACME%20Co:john.doe@email.com?secret=HXDMVJECJJWSRB3HWIZR4IFUGFTMXBOZ&issuer=ACME%20Co&algorithm=SHA256&digits=8&period=60");

        Assert.Equal("ACME Co", a.Issuer);
        Assert.Equal("john.doe@email.com", a.Label);
        Assert.Equal(OtpHashAlgorithm.Sha256, a.Algorithm);
        Assert.Equal(8, a.Digits);
        Assert.Equal(60, a.Period);
        Assert.Equal(OtpEncoder.Default, a.Encoder);
        Assert.Equal("HXDMVJECJJWSRB3HWIZR4IFUGFTMXBOZ", SecretVault.Decrypt(a.EncryptedSecret));
    }

    [Fact]
    public void Defaults_to_sha1_six_digits_thirty_seconds()
    {
        var a = AccountStore.ParseOtpAuthUri("otpauth://totp/alice?secret=JBSWY3DPEHPK3PXP");
        Assert.Equal(OtpHashAlgorithm.Sha1, a.Algorithm);
        Assert.Equal(6, a.Digits);
        Assert.Equal(30, a.Period);
        Assert.Equal("alice", a.Label);
    }

    [Theory]
    [InlineData("otpauth://steam/Steam:alice?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/alice?secret=JBSWY3DPEHPK3PXP&encoder=steam")]
    public void Recognizes_steam_accounts(string uri)
    {
        var a = AccountStore.ParseOtpAuthUri(uri);
        Assert.Equal(OtpEncoder.Steam, a.Encoder);
        Assert.Equal(5, a.Digits);
        Assert.Equal("Steam", a.Issuer);
    }

    [Theory]
    [InlineData("otpauth://hotp/alice?secret=JBSWY3DPEHPK3PXP&counter=1")]
    [InlineData("https://example.com/?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/alice?issuer=x")]
    public void Rejects_unsupported_or_incomplete_uris(string uri)
        => Assert.Throws<ArgumentException>(() => AccountStore.ParseOtpAuthUri(uri));
}
