using System.IO;
using System.Text;

namespace WardLock.Services;

/// <summary>
/// Crash-safe file replacement. The new content is written to a sibling temp
/// file, flushed to disk, then swapped in for the target, so a reader (or the
/// next launch after a crash, power loss, or dropped network share) sees
/// either the old file or the new one — never a truncated one.
///
/// An existing target is swapped with File.Replace (ReplaceFile), which keeps
/// its ACL, owner and attributes — important for a vault on a share whose
/// file permissions an admin has tightened. Requires create permission in the
/// folder and delete permission on the target.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
        => WriteAllBytes(path, Utf8NoBom.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        // Same directory as the target: the final rename never crosses volumes.
        var tmp = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            MoveWithRetry(tmp, fullPath);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// Moves an unreadable file aside (<c>name.corrupt-yyyyMMddHHmmss</c>) so it
    /// is preserved for recovery and the next save can't overwrite it.
    /// Returns the new path.
    /// </summary>
    public static string Quarantine(string path)
    {
        var target = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
        File.Move(path, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// A reader (antivirus, a teammate's client on a share) can hold the target
    /// open for a moment; retry the rename briefly instead of failing the save.
    /// </summary>
    private static void MoveWithRetry(string source, string destination)
    {
        const int attempts = 5;
        for (var i = 1; ; i++)
        {
            try
            {
                if (File.Exists(destination))
                    File.Replace(source, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(source, destination);
                return;
            }
            catch (IOException) when (i < attempts)
            {
                Thread.Sleep(100 * i);
            }
            catch (UnauthorizedAccessException) when (i < attempts)
            {
                Thread.Sleep(100 * i);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
