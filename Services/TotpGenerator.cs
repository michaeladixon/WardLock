using OtpNet;
using System.Security.Cryptography;
using WardLock.Models;

namespace WardLock.Services;

public static class TotpGenerator
{
    /// <summary>
    /// Steam Guard's custom code alphabet (no 0/1/A/E/I/L/O/S/U/Z to avoid ambiguity).
    /// </summary>
    private const string SteamAlphabet = "23456789BCDFGHJKMNPQRTVWXY";

    public static string GenerateCode(AuthAccount account)
        => GenerateCodeAt(account, DateTimeOffset.UtcNow);

    public static string GenerateCodeAt(AuthAccount account, DateTimeOffset time)
    {
        // Viewer-role vault accounts hold no seed — look the code up in the
        // precomputed window. Empty when the window has lapsed (admin offline
        // too long); delivery paths treat empty as code-unavailable.
        if (account.CodeWindow != null)
            return account.CodeWindow.CodeAt(time.ToUnixTimeSeconds(), account.Period) ?? string.Empty;

        // Shared vault accounts hold plaintext secret in memory;
        // personal accounts need DPAPI decryption. If decryption fails, return an empty code
        // to avoid crashing the UI refresh timer thread; the caller can surface an error.
        string secret;
        try
        {
            secret = account.PlaintextSecret ?? SecretVault.Decrypt(account.EncryptedSecret);
        }
        catch (CryptographicException)
        {
            // Decryption failed (corrupt blob or different user). Return empty code so UI remains responsive.
            return string.Empty;
        }
        var secretBytes = Base32Encoding.ToBytes(secret);

        if (account.Encoder == OtpEncoder.Steam)
            return ComputeSteamCode(secretBytes, account.Period, time.ToUnixTimeSeconds());

        var mode = account.Algorithm switch
        {
            OtpHashAlgorithm.Sha256 => OtpHashMode.Sha256,
            OtpHashAlgorithm.Sha512 => OtpHashMode.Sha512,
            _ => OtpHashMode.Sha1
        };

        var totp = new Totp(secretBytes, step: account.Period, mode: mode, totpSize: account.Digits);
        return totp.ComputeTotp(time.UtcDateTime);
    }

    /// <summary>
    /// Seconds remaining before current code expires.
    /// </summary>
    public static int SecondsRemaining(int period = 30)
    {
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return period - (int)(epoch % period);
    }

    /// <summary>
    /// Steam Guard code: standard RFC 6238 HMAC-SHA1 truncation, but the 31-bit value
    /// is encoded as 5 characters over Steam's custom alphabet instead of decimal digits.
    /// </summary>
    private static string ComputeSteamCode(byte[] secretBytes, int period, long unixSeconds)
    {
        var counter = (ulong)(unixSeconds / period);
        var counterBytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counterBytes, counter);

        using var hmac = new HMACSHA1(secretBytes);
        var hash = hmac.ComputeHash(counterBytes);

        var offset = hash[^1] & 0x0f;
        var fullCode = ((hash[offset] & 0x7f) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];

        Span<char> code = stackalloc char[5];
        for (int i = 0; i < code.Length; i++)
        {
            code[i] = SteamAlphabet[fullCode % SteamAlphabet.Length];
            fullCode /= SteamAlphabet.Length;
        }
        return new string(code);
    }
}
