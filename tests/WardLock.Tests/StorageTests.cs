using System.IO;
using WardLock.Models;
using WardLock.Services;

namespace WardLock.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wardlock-tests-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void AtomicFile_replaces_content_and_leaves_no_temp_files()
    {
        var path = Path.Combine(_dir, "data.json");
        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal(["data.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void AccountStore_round_trips_accounts()
    {
        var path = Path.Combine(_dir, "accounts.json");
        var store = new AccountStore(path);
        store.Add(AccountStore.ParseOtpAuthUri("otpauth://totp/ACME:alice?secret=JBSWY3DPEHPK3PXP"));

        var reloaded = new AccountStore(path);
        reloaded.Load();

        var account = Assert.Single(reloaded.Accounts);
        Assert.Equal("ACME", account.Issuer);
        Assert.Null(reloaded.LoadWarning);
    }

    [Theory]
    [InlineData("")]                       // truncated to zero bytes mid-write
    [InlineData("[{\"issuer\":\"ACME\",")] // torn write
    public void AccountStore_quarantines_unreadable_file_instead_of_overwriting_it(string damaged)
    {
        var path = Path.Combine(_dir, "accounts.json");
        File.WriteAllText(path, damaged);

        var store = new AccountStore(path);
        store.Load();

        Assert.Empty(store.Accounts);
        Assert.NotNull(store.LoadWarning);
        Assert.False(File.Exists(path));
        var quarantined = Assert.Single(Directory.GetFiles(_dir, "accounts.json.corrupt-*"));
        Assert.Equal(damaged, File.ReadAllText(quarantined));

        // A subsequent save must not touch the preserved copy.
        store.Add(AccountStore.ParseOtpAuthUri("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP"));
        Assert.Equal(damaged, File.ReadAllText(quarantined));
    }

    [Fact]
    public void Backup_round_trip_preserves_approval_requirement_and_steam_encoder()
    {
        var path = Path.Combine(_dir, "backup.wardlock");
        var steam = AccountStore.ParseOtpAuthUri("otpauth://steam/Steam:alice?secret=JBSWY3DPEHPK3PXP");
        steam.RequireApproval = true;
        steam.Domain = "steampowered.com";

        ExportImportService.Export([steam], path, "correct horse battery staple");
        var (accounts, skipped) = ExportImportService.Import(path, "correct horse battery staple");

        Assert.Equal(0, skipped);
        var restored = Assert.Single(accounts);
        Assert.True(restored.RequireApproval);
        Assert.Equal(OtpEncoder.Steam, restored.Encoder);
        Assert.Equal(5, restored.Digits);
        Assert.Equal("steampowered.com", restored.Domain);
    }

    [Fact]
    public void Backup_import_skips_entries_that_cannot_produce_codes()
    {
        var path = Path.Combine(_dir, "backup.wardlock");
        var good = AccountStore.ParseOtpAuthUri("otpauth://totp/good?secret=JBSWY3DPEHPK3PXP");
        var bad = new AuthAccount { Label = "bad", EncryptedSecret = SecretVault.Encrypt("NOT-BASE32-0189") };

        ExportImportService.Export([good, bad], path, "pw");
        var (accounts, skipped) = ExportImportService.Import(path, "pw");

        Assert.Equal(1, skipped);
        Assert.Equal("good", Assert.Single(accounts).Label);
    }
}
