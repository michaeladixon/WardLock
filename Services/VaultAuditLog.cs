using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WardLock.Services;

/// <summary>
/// What happened to a shared vault, for the audit trail (issue #3).
/// </summary>
public enum AuditAction
{
    VaultCreated,
    VaultOpened,
    AccountAdded,
    AccountRemoved,
    DomainChanged,
    CodeCopied,
    CodeAutoTyped,
    CodeFilledInBrowser,
}

/// <summary>One audit record. Serialized as a single JSON line in the sidecar log.</summary>
public sealed class AuditEntry
{
    public long Seq { get; set; }
    /// <summary>UTC timestamp, round-trip ("o") format.</summary>
    public string Utc { get; set; } = string.Empty;
    /// <summary>Windows username of the member who performed the action.</summary>
    public string User { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    /// <summary>The affected account ("Issuer (Label)"), or empty for vault-level events.</summary>
    public string Target { get; set; } = string.Empty;
    /// <summary>Extra context (e.g. the new fill domain).</summary>
    public string Detail { get; set; } = string.Empty;
    /// <summary>Hex SHA-256 of the previous entry (chain link).</summary>
    public string Prev { get; set; } = string.Empty;
    /// <summary>Hex SHA-256 over this entry's fields + Prev.</summary>
    public string Hash { get; set; } = string.Empty;
}

/// <summary>
/// Append-only, tamper-evident audit log stored as a JSON-lines sidecar next to
/// the vault file ("team-vault.wardlock" → "team-vault.wardlock.log"). Each entry
/// embeds the previous entry's hash, so any edit, reorder, or truncation breaks
/// the chain and is detectable without a server.
///
/// Threat model (documented in README): entries are appended by cooperating
/// WardLock clients under the acting member's Windows username. A hostile member
/// with write access to the share can delete the whole log or fork it — the chain
/// makes silent *modification* evident, it does not make the log unforgeable.
/// </summary>
public sealed class VaultAuditLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Chain anchor for entry #1's Prev field.</summary>
    private const string GenesisSeed = "wardlock-audit-v1";

    public string LogPath { get; }

    public VaultAuditLog(string vaultFilePath)
    {
        LogPath = vaultFilePath + ".log";
    }

    /// <summary>
    /// Appends one entry under an exclusive file lock (retrying briefly, matching
    /// the vault file's own locking strategy) so concurrent teammates serialize
    /// and the hash chain stays linear. Returns false if the log is unreachable —
    /// vault operations proceed regardless; auditing must not break code access.
    /// </summary>
    public bool TryAppend(AuditAction action, string target = "", string detail = "")
    {
        try
        {
            using var stream = OpenExclusive();
            var last = ReadEntries(stream).LastOrDefault();

            var entry = new AuditEntry
            {
                Seq = (last?.Seq ?? 0) + 1,
                Utc = DateTime.UtcNow.ToString("o"),
                User = Environment.UserName,
                Action = action.ToString(),
                Target = target,
                Detail = detail,
                Prev = last?.Hash ?? GenesisHash(),
            };
            entry.Hash = ComputeHash(entry);

            stream.Seek(0, SeekOrigin.End);
            var line = JsonSerializer.Serialize(entry, JsonOpts) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>All entries plus chain verification in one read.</summary>
    public AuditReadResult Read()
    {
        List<AuditEntry> entries;
        try
        {
            if (!File.Exists(LogPath))
                return new AuditReadResult([], true, null);
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            entries = ReadEntries(stream);
        }
        catch (Exception ex)
        {
            return new AuditReadResult([], false, $"Log unreadable: {ex.Message}");
        }

        var expectedPrev = GenesisHash();
        long expectedSeq = 1;
        foreach (var e in entries)
        {
            if (e.Seq != expectedSeq)
                return new AuditReadResult(entries, false, $"Sequence break at entry #{e.Seq} (expected #{expectedSeq}) — entries removed or reordered.");
            if (e.Prev != expectedPrev)
                return new AuditReadResult(entries, false, $"Chain break at entry #{e.Seq} — a previous entry was modified or removed.");
            if (e.Hash != ComputeHash(e))
                return new AuditReadResult(entries, false, $"Entry #{e.Seq} was modified (hash mismatch).");
            expectedPrev = e.Hash;
            expectedSeq++;
        }
        return new AuditReadResult(entries, true, null);
    }

    /// <summary>Exports all entries to CSV (chain fields included for offline re-verification).</summary>
    public void ExportCsv(string csvPath)
    {
        var result = Read();
        var sb = new StringBuilder();
        sb.AppendLine("seq,utcTime,user,action,target,detail,prevHash,hash");
        foreach (var e in result.Entries)
        {
            sb.AppendLine(string.Join(',',
                e.Seq, Csv(e.Utc), Csv(e.User), Csv(e.Action),
                Csv(e.Target), Csv(e.Detail), Csv(e.Prev), Csv(e.Hash)));
        }
        File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
    }

    private static string Csv(string s) => '"' + s.Replace("\"", "\"\"") + '"';

    private FileStream OpenExclusive()
    {
        var deadline = Environment.TickCount64 + 2000;
        while (true)
        {
            try
            {
                return new FileStream(LogPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(50); // a teammate is appending — wait our turn
            }
        }
    }

    private static List<AuditEntry> ReadEntries(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var entries = new List<AuditEntry>();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line, JsonOpts);
                if (entry != null) entries.Add(entry);
            }
            catch (JsonException)
            {
                // Corrupt line — keep it out of the list; verification will flag
                // the chain break at the next parsable entry.
            }
        }
        return entries;
    }

    private static string ComputeHash(AuditEntry e)
    {
        var material = $"{e.Seq}|{e.Utc}|{e.User}|{e.Action}|{e.Target}|{e.Detail}|{e.Prev}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string GenesisHash()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GenesisSeed))).ToLowerInvariant();
}

/// <summary>Entries plus the outcome of hash-chain verification.</summary>
public sealed record AuditReadResult(List<AuditEntry> Entries, bool ChainIntact, string? Problem);
