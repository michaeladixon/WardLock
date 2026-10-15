using WardLock.Services.BrowserBridge;

namespace WardLock.Tests;

public class FillApprovalServiceTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private FillApprovalService Create() => new(() => _now);

    [Fact]
    public void Approved_code_is_released_once_to_the_requesting_client()
    {
        var svc = Create();
        var c = svc.Begin("acct", "ACME", "acme.com", "client-a");

        Assert.Equal(ApprovalAttemptResult.Approved, svc.TryApprove(c.Number));
        Assert.Null(svc.TryConsume(c.Id, "client-b"));
        Assert.NotNull(svc.TryConsume(c.Id, "client-a"));
        Assert.Null(svc.TryConsume(c.Id, "client-a"));
    }

    [Fact]
    public void Lock_revokes_an_approved_code_that_was_not_picked_up()
    {
        var svc = Create();
        var c = svc.Begin("acct", "ACME", "acme.com", "client-a");
        svc.TryApprove(c.Number);

        svc.RevokeAll("locked");

        Assert.Null(svc.TryConsume(c.Id, "client-a"));
        Assert.Equal(ChallengeStatus.Denied, svc.GetStatus(c.Id, "client-a"));
        Assert.Equal("locked", c.DenyReason);
    }

    [Fact]
    public void Lock_denies_the_pending_challenge()
    {
        var svc = Create();
        var c = svc.Begin("acct", "ACME", "acme.com", "client-a");

        svc.RevokeAll("locked");

        Assert.Null(svc.Pending);
        Assert.Equal(ApprovalAttemptResult.NoChallenge, svc.TryApprove(c.Number));
        Assert.Equal(ChallengeStatus.Denied, svc.GetStatus(c.Id, "client-a"));
    }

    [Fact]
    public void Three_wrong_numbers_deny_the_challenge()
    {
        var svc = Create();
        var c = svc.Begin("acct", "ACME", "acme.com", "client-a");
        var wrong = c.Number == "00" ? "01" : "00";

        Assert.Equal(ApprovalAttemptResult.WrongNumber, svc.TryApprove(wrong));
        Assert.Equal(ApprovalAttemptResult.WrongNumber, svc.TryApprove(wrong));
        Assert.Equal(ApprovalAttemptResult.Denied, svc.TryApprove(wrong));
        Assert.Equal(ApprovalAttemptResult.NoChallenge, svc.TryApprove(c.Number));
    }

    [Fact]
    public void Approved_code_expires_after_the_pickup_window()
    {
        var svc = Create();
        var c = svc.Begin("acct", "ACME", "acme.com", "client-a");
        svc.TryApprove(c.Number);

        _now = _now.AddSeconds(FillApprovalService.PickupWindowSeconds + 1);

        Assert.Null(svc.TryConsume(c.Id, "client-a"));
        Assert.Equal(ChallengeStatus.Expired, svc.GetStatus(c.Id, "client-a"));
    }
}
