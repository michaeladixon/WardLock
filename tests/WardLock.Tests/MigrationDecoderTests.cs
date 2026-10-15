using System.IO;
using System.Text;
using WardLock.Services;

namespace WardLock.Tests;

public class MigrationDecoderTests
{
    // Minimal protobuf writer for building Google Authenticator export payloads.
    private static void Varint(List<byte> b, ulong v)
    {
        while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; }
        b.Add((byte)v);
    }

    private static void Bytes(List<byte> b, int field, byte[] value)
    {
        Varint(b, (ulong)(field << 3 | 2));
        Varint(b, (ulong)value.Length);
        b.AddRange(value);
    }

    private static void Int(List<byte> b, int field, ulong value)
    {
        Varint(b, (ulong)(field << 3));
        Varint(b, value);
    }

    private static byte[] Otp(string name, string issuer, int algorithm = 1, int digits = 1, int type = 2)
    {
        var b = new List<byte>();
        Bytes(b, 1, Encoding.ASCII.GetBytes("Hello!Þ"));
        Bytes(b, 2, Encoding.UTF8.GetBytes(name));
        Bytes(b, 3, Encoding.UTF8.GetBytes(issuer));
        Int(b, 4, (ulong)algorithm);
        Int(b, 5, (ulong)digits);
        Int(b, 6, (ulong)type);
        Bytes(b, 9, [1, 2, 3, 4, 5]); // unknown length-delimited field must be skipped exactly
        return b.ToArray();
    }

    private static string Uri(params byte[][] otps)
    {
        var payload = new List<byte>();
        foreach (var otp in otps) Bytes(payload, 1, otp);
        Int(payload, 2, 1); // version
        return "otpauth-migration://offline?data=" + System.Uri.EscapeDataString(Convert.ToBase64String(payload.ToArray()));
    }

    [Fact]
    public void Decodes_totp_entries_with_issuer_digits_and_algorithm()
    {
        var (uris, unsupported) = GoogleAuthMigrationDecoder.ParseMigrationUri(
            Uri(Otp("alice@example.com", "ACME", algorithm: 2, digits: 2)));

        Assert.Equal(0, unsupported);
        var p = OtpAuthUri.Parse(Assert.Single(uris));
        Assert.Equal("ACME", p.Issuer);
        Assert.Equal("alice@example.com", p.Label);
        Assert.Equal(8, p.Digits);
        Assert.Equal(WardLock.Models.OtpHashAlgorithm.Sha256, p.Algorithm);
    }

    [Fact]
    public void Skips_hotp_and_md5_entries_and_counts_them()
    {
        var (uris, unsupported) = GoogleAuthMigrationDecoder.ParseMigrationUri(
            Uri(Otp("a", "X"), Otp("b", "Y", type: 1), Otp("c", "Z", algorithm: 4)));

        Assert.Single(uris);
        Assert.Equal(2, unsupported);
    }

    [Theory]
    [InlineData("Sv////8P")]          // field 9, length-delimited, length = 0xFFFFFFFF
    [InlineData("CgUBAg==")]          // otp_parameters claims 5 bytes, only 2 present
    [InlineData("CP////////////8B")]  // over-long varint
    public async Task Hostile_payloads_fail_fast_instead_of_hanging(string b64)
    {
        var uri = "otpauth-migration://offline?data=" + System.Uri.EscapeDataString(b64);
        var parse = Task.Run(() => GoogleAuthMigrationDecoder.ParseMigrationUri(uri));

        var finished = await Task.WhenAny(parse, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(finished == parse, "decoder did not terminate on a malformed payload");
        await Assert.ThrowsAsync<FormatException>(() => parse);
    }
}
