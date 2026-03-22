using OtpNet;
using WardLock.Models;

namespace WardLock.Services;

public static class TotpGenerator
{
    public static string GenerateCode(AuthAccount account)
    {
        // Shared vault accounts hold plaintext secret in memory;
        // personal accounts need DPAPI decryption
        var secret = account.PlaintextSecret ?? SecretVault.Decrypt(account.EncryptedSecret);
        var secretBytes = Base32Encoding.ToBytes(secret);

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
}
