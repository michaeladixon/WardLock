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
    {
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
            return ComputeSteamCode(secretBytes, account.Period);

        var mode = account.Algorithm switch
        {
            OtpHashAlgorithm.Sha256 => OtpHashMode.Sha256,
            OtpHashAlgorithm.Sha512 => OtpHashMode.Sha512,
            _ => OtpHashMode.Sha1
        };

        var totp = new Totp(secretBytes, step: account.Period, mode: mode, totpSize: account.Digits);
        return totp.ComputeTotp();
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
    private static string ComputeSteamCode(byte[] secretBytes, int period)
    {
        var counter = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / period);
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

    /// <summary>
    /// Steam secrets come in two forms: Base32 (otpauth URIs, KeePassXC/Aegis exports)
    /// and Base64 (SDA maFile shared_secret). Normalizes either to Base32 for storage.
    /// Detection is case-sensitive: a random Base64 secret is all-uppercase [A-Z2-7]
    /// with negligible probability, so uppercase-only input is treated as Base32.
    /// </summary>
    public static string NormalizeSteamSecret(string raw)
    {
        var candidate = raw.Trim().Replace(" ", "");
        if (candidate.Length == 0)
            throw new ArgumentException("Secret is required.");

        if (System.Text.RegularExpressions.Regex.IsMatch(candidate, "^[A-Z2-7]+=*$"))
            return candidate.TrimEnd('=');

        try
        {
            var bytes = Convert.FromBase64String(candidate);
            return Base32Encoding.ToString(bytes).TrimEnd('=');
        }
        catch (FormatException)
        {
            // Not Base64 either; accept lowercase Base32 as a last resort
            var upper = candidate.ToUpperInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(upper, "^[A-Z2-7]+=*$"))
                return upper.TrimEnd('=');
            throw new ArgumentException("Steam secret must be Base32 or Base64.");
        }
    }
}
