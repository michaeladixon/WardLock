using System.Text;

namespace WardLock.Services;

/// <summary>
/// Decodes Google Authenticator "Export accounts" QR codes.
/// These carry a otpauth-migration://offline?data=&lt;base64-protobuf&gt; URI
/// containing one or more OTP accounts encoded in a protobuf payload.
/// </summary>
public static class GoogleAuthMigrationDecoder
{
    public static bool IsMigrationUri(string uri)
        => uri.StartsWith("otpauth-migration://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a migration URI and returns standard otpauth:// URIs for each account.
    /// </summary>
    public static List<string> ParseMigrationUri(string uri)
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

    private static List<string> ParsePayload(byte[] data)
    {
        var results = new List<string>();
        int pos = 0;
        while (pos < data.Length)
        {
            var tag = (int)ReadVarint(data, ref pos);
            var field = tag >> 3;
            var wire  = tag & 0x7;

            if (field == 1 && wire == 2)          // repeated OtpParameters
            {
                var len = (int)ReadVarint(data, ref pos);
                var msg = data[pos..(pos + len)];
                pos += len;
                var otp = ParseOtpParameters(msg);
                if (otp != null) results.Add(otp);
            }
            else
            {
                SkipField(data, ref pos, wire);
            }
        }
        return results;
    }

    private static string? ParseOtpParameters(byte[] data)
    {
        byte[]? secret  = null;
        string  name    = "";
        string  issuer  = "";
        int     digits  = 6;   // DIGIT_COUNT_SIX = 1
        int     type    = 2;   // OTP_TYPE_TOTP  = 2

        int pos = 0;
        while (pos < data.Length)
        {
            var tag   = (int)ReadVarint(data, ref pos);
            var field = tag >> 3;
            var wire  = tag & 0x7;

            switch (field)
            {
                case 1 when wire == 2:  // secret (bytes)
                    var sLen = (int)ReadVarint(data, ref pos);
                    secret = data[pos..(pos + sLen)];
                    pos += sLen;
                    break;

                case 2 when wire == 2:  // name / label
                    var nLen = (int)ReadVarint(data, ref pos);
                    name = Encoding.UTF8.GetString(data, pos, nLen);
                    pos += nLen;
                    break;

                case 3 when wire == 2:  // issuer
                    var iLen = (int)ReadVarint(data, ref pos);
                    issuer = Encoding.UTF8.GetString(data, pos, iLen);
                    pos += iLen;
                    break;

                case 5 when wire == 0:  // digit count  (1=6 digits, 2=8 digits)
                    digits = (int)ReadVarint(data, ref pos) == 2 ? 8 : 6;
                    break;

                case 6 when wire == 0:  // otp type  (1=HOTP, 2=TOTP)
                    type = (int)ReadVarint(data, ref pos);
                    break;

                case 7 when wire == 0:  // counter (HOTP only, skip for TOTP)
                    ReadVarint(data, ref pos);
                    break;

                default:
                    SkipField(data, ref pos, wire);
                    break;
            }
        }

        if (secret == null) return null;

        var secretB32 = ToBase32(secret);
        var otpType   = type == 1 ? "hotp" : "totp";

        // Build label: "Issuer:name" when both present, otherwise just name
        var labelRaw  = !string.IsNullOrEmpty(issuer) && !name.StartsWith(issuer)
                        ? $"{issuer}:{name}"
                        : name;
        var label     = Uri.EscapeDataString(labelRaw);

        var sb = new StringBuilder($"otpauth://{otpType}/{label}?secret={secretB32}");
        if (!string.IsNullOrEmpty(issuer))
            sb.Append($"&issuer={Uri.EscapeDataString(issuer)}");
        if (digits != 6)
            sb.Append($"&digits={digits}");

        return sb.ToString();
    }

    // ── Protobuf primitives ───────────────────────────────────────────────────

    private static long ReadVarint(byte[] data, ref int pos)
    {
        long result = 0;
        int  shift  = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static void SkipField(byte[] data, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(data, ref pos);                       break; // varint
            case 1: pos += 8;                                        break; // 64-bit
            case 2: pos += (int)ReadVarint(data, ref pos);           break; // length-delimited
            case 5: pos += 4;                                        break; // 32-bit
        }
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
