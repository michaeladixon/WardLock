using WardLock.Models;
using WardLock.Services;

namespace WardLock.Tests;

public class ValidationTests
{
    [Theory]
    [InlineData("jbsw y3dp-ehpk 3pxp", "JBSWY3DPEHPK3PXP")]
    [InlineData("  JBSWY3DPEHPK3PXP  ", "JBSWY3DPEHPK3PXP")]
    [InlineData("JBSWY3DPEHPK3PXP====", "JBSWY3DPEHPK3PXP====")]
    public void NormalizeSecret_accepts_display_formatting(string input, string expected)
        => Assert.Equal(expected, OtpAuthUri.NormalizeSecret(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("====")]
    [InlineData("JBSW0Y3DP")]   // 0 is not Base32
    [InlineData("JBSW1Y3DP")]   // 1 is not Base32
    [InlineData("JBSW8Y3DP")]   // 8 is not Base32
    [InlineData("A")]           // decodes to zero bytes
    public void NormalizeSecret_rejects_secrets_that_cannot_produce_codes(string input)
        => Assert.Throws<ArgumentException>(() => OtpAuthUri.NormalizeSecret(input));

    [Theory]
    [InlineData("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&period=0")]
    [InlineData("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&period=-30")]
    [InlineData("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&digits=0")]
    [InlineData("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&digits=11")]
    [InlineData("otpauth://totp/x?secret=")]
    [InlineData("otpauth://totp/x?secret=NOT-BASE32-!")]
    public void Parse_rejects_parameters_that_would_crash_code_generation(string uri)
        => Assert.Throws<ArgumentException>(() => OtpAuthUri.Parse(uri));

    [Fact]
    public void Parse_normalizes_grouped_lowercase_secret()
        => Assert.Equal("JBSWY3DPEHPK3PXP",
            OtpAuthUri.Parse("otpauth://totp/x?secret=jbsw%20y3dp%20ehpk%203pxp").Secret);

    [Theory]
    [InlineData("JBSW0Y3DP", 6, 30)]          // invalid Base32 (e.g. written by an older version)
    [InlineData("JBSWY3DPEHPK3PXP", 11, 30)]  // unsupported digits
    [InlineData("JBSWY3DPEHPK3PXP", 6, 0)]    // zero period
    public void GenerateCode_returns_empty_instead_of_throwing_for_bad_stored_accounts(string secret, int digits, int period)
    {
        var account = new AuthAccount { PlaintextSecret = secret, Digits = digits, Period = period };
        Assert.Equal(string.Empty, TotpGenerator.GenerateCodeAt(account, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Steam_account_with_zero_period_returns_empty()
    {
        var account = new AuthAccount { PlaintextSecret = "JBSWY3DPEHPK3PXP", Encoder = OtpEncoder.Steam, Digits = 5, Period = 0 };
        Assert.Equal(string.Empty, TotpGenerator.GenerateCodeAt(account, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SecondsRemaining_tolerates_nonpositive_period()
        => Assert.Equal(0, TotpGenerator.SecondsRemaining(0));

    [Theory]
    [InlineData(0, 6)]
    [InlineData(30, 0)]
    [InlineData(30, -1)]
    public void CodeWindow_returns_null_for_malformed_viewer_entries(int period, int width)
    {
        var window = new CodeWindow { StartStep = 0, Width = width, Codes = "123456" };
        Assert.Null(window.CodeAt(10, period));
    }
}
