using System.Text;

namespace WardLock.Services;

/// <summary>
/// Decodes Google Authenticator "Export accounts" QR codes.
/// These carry a otpauth-migration://offline?data=&lt;base64-protobuf&gt; URI
/// containing one or more OTP accounts encoded in a protobuf payload.
/// The payload comes from a scanned image, so it is parsed as untrusted input:
/// every length is bounds-checked and malformed data throws FormatException.
/// </summary>
public static class GoogleAuthMigrationDecoder
{
    /// <summary>
    /// TOTP accounts as otpauth:// URIs, plus the number of entries WardLock
    /// can't use (HOTP counters, MD5) and therefore skipped.
    /// </summary>
    public sealed record MigrationResult(List<string> Uris, int Unsupported);

    public static bool IsMigrationUri(string uri)
        => uri.StartsWith("otpauth-migration://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a migration URI and returns standard otpauth:// URIs for each TOTP account.
    /// </summary>
    public static MigrationResult ParseMigrationUri(string uri)
    {
        // Extract the ?data= query parameter (base64-encoded protobuf)
        var query = new Uri(uri).Query.TrimStart('?');
        string? b64 = null;
        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && string.Equals(part[..eq], "data", StringComparison.OrdinalIgnoreCase))
            {
                // Uri.UnescapeDataString handles %2B→+ and %2F→/ so base64 is preserved
                b64 = Uri.UnescapeDataString(part[(eq + 1)..]);
                break;
            }
        }

        if (b64 == null)
            throw new ArgumentException("Migration URI is missing the 'data' parameter.");

        return ParsePayload(Convert.FromBase64String(b64));
    }

    // ── Protobuf parser ───────────────────────────────────────────────────────
    // MigrationPayload { repeated OtpParameters otp_parameters = 1; ... }
    // OtpParameters { bytes secret = 1; string name = 2; string issuer = 3;
    //   Algorithm algorithm = 4; DigitCount digits = 5; OtpType type = 6; int64 counter = 7; }

    private static MigrationResult ParsePayload(byte[] data)
    {
        var results = new List<string>();
        var unsupported = 0;
        int pos = 0;
        while (pos < data.Length)
        {
            var tag = ReadVarint(data, ref pos);
            var field = tag >> 3;
            var wire  = (int)(tag & 0x7);

            if (field == 1 && wire == 2)          // repeated OtpParameters
            {
                var msg = ReadLengthDelimited(data, ref pos);
                var otp = ParseOtpParameters(msg);
                if (otp != null) results.Add(otp); else unsupported++;
            }
            else
            {
                SkipField(data, ref pos, wire);
            }
        }
        return new MigrationResult(results, unsupported);
    }

    private static string? ParseOtpParameters(byte[] data)
    {
        byte[]? secret  = null;
        string  name    = "";
        string  issuer  = "";
        int     digits  = 6;   // DIGIT_COUNT_SIX = 1
        int     type    = 2;   // OTP_TYPE_TOTP  = 2
        long    algo    = 1;   // ALGORITHM_SHA1 = 1 (0 = unspecified → SHA1)

        int pos = 0;
        while (pos < data.Length)
        {
            var tag   = ReadVarint(data, ref pos);
            var field = tag >> 3;
            var wire  = (int)(tag & 0x7);

            switch (field)
            {
                case 1 when wire == 2:  // secret (bytes)
                    secret = ReadLengthDelimited(data, ref pos);
                    break;

                case 2 when wire == 2:  // name / label
                    name = Encoding.UTF8.GetString(ReadLengthDelimited(data, ref pos));
                    break;

                case 3 when wire == 2:  // issuer
                    issuer = Encoding.UTF8.GetString(ReadLengthDelimited(data, ref pos));
                    break;

                case 4 when wire == 0:  // algorithm (1=SHA1, 2=SHA256, 3=SHA512, 4=MD5)
                    algo = ReadVarint(data, ref pos);
                    break;

                case 5 when wire == 0:  // digit count  (1=6 digits, 2=8 digits)
                    digits = ReadVarint(data, ref pos) == 2 ? 8 : 6;
                    break;

                case 6 when wire == 0:  // otp type  (1=HOTP, 2=TOTP)
                    type = (int)ReadVarint(data, ref pos);
                    break;

                default:
                    SkipField(data, ref pos, wire);
                    break;
            }
        }

        // WardLock is TOTP-only, and MD5 isn't a supported HMAC.
        if (secret == null || secret.Length == 0 || type == 1) return null;
        var algorithm = algo switch
        {
            0 or 1 => null,
            2 => "SHA256",
            3 => "SHA512",
            _ => "unsupported"
        };
        if (algorithm == "unsupported") return null;

        var secretB32 = ToBase32(secret);

        // Build label: "Issuer:name" when both present, otherwise just name
        var labelRaw  = !string.IsNullOrEmpty(issuer) && !name.StartsWith(issuer)
                        ? $"{issuer}:{name}"
                        : name;
        var label     = Uri.EscapeDataString(labelRaw);

        var sb = new StringBuilder($"otpauth://totp/{label}?secret={secretB32}");
        if (!string.IsNullOrEmpty(issuer))
            sb.Append($"&issuer={Uri.EscapeDataString(issuer)}");
        if (digits != 6)
            sb.Append($"&digits={digits}");
        if (algorithm != null)
            sb.Append($"&algorithm={algorithm}");

        return sb.ToString();
    }

    // ── Protobuf primitives ───────────────────────────────────────────────────

    private static long ReadVarint(byte[] data, ref int pos)
    {
        long result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (pos >= data.Length)
                throw new FormatException("Truncated varint in migration payload.");
            byte b = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new FormatException("Malformed varint in migration payload.");
    }

    private static byte[] ReadLengthDelimited(byte[] data, ref int pos)
    {
        var len = ReadVarint(data, ref pos);
        if (len < 0 || len > data.Length - pos)
            throw new FormatException("Invalid field length in migration payload.");
        var value = data[pos..(pos + (int)len)];
        pos += (int)len;
        return value;
    }

    private static void SkipField(byte[] data, ref int pos, int wireType)
    {
        var skip = wireType switch
        {
            0 => 0,                                 // varint, consumed below
            1 => 8,                                 // 64-bit
            2 => -1,                                // length-delimited, consumed below
            5 => 4,                                 // 32-bit
            _ => throw new FormatException($"Unsupported wire type {wireType} in migration payload.")
        };

        if (wireType == 0) { ReadVarint(data, ref pos); return; }
        if (wireType == 2) { ReadLengthDelimited(data, ref pos); return; }
        if (skip > data.Length - pos)
            throw new FormatException("Truncated field in migration payload.");
        pos += skip;
    }

    // ── Base32 encoding ───────────────────────────────────────────────────────

    private static string ToBase32(byte[] bytes)
    {
        const string Alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bitsLeft = 0;
        foreach (byte b in bytes)
        {
            buffer    = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Alpha[(buffer >> bitsLeft) & 31]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(Alpha[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }
}
