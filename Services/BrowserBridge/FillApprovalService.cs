using System.Security.Cryptography;

namespace WardLock.Services.BrowserBridge;

public enum ChallengeStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Unknown,
}

public enum ApprovalAttemptResult
{
    Approved,
    WrongNumber,
    Denied,       // wrong-number strikes exhausted
    NoChallenge,  // nothing pending (already expired/denied/superseded)
}

/// <summary>One in-flight number-matched fill approval (issue #1).</summary>
public sealed class FillChallenge
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    /// <summary>2-digit number shown in the browser popup, matched in the app.</summary>
    public string Number { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string AccountDisplayName { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    /// <summary>Extension client that opened the challenge; status/pickup is bound to it.</summary>
    public string ClientId { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }

    public ChallengeStatus Status { get; internal set; } = ChallengeStatus.Pending;
    public DateTime? ApprovedUtc { get; internal set; }
    public int WrongAttempts { get; internal set; }
    public bool Consumed { get; internal set; }
    /// <summary>Why a denied challenge was denied ("user", "wrong-number", "superseded", "locked").</summary>
    public string DenyReason { get; internal set; } = string.Empty;
}

/// <summary>
/// State machine for the number-matched code release (issue #1): the browser
/// popup shows a random 2-digit number, the user types it into the WardLock
/// window (out-of-band relative to the requesting surface), and only then is
/// the code released to the extension — one-shot, time-boxed.
///
/// At most one challenge is pending at a time; a new request supersedes the
/// old one. All calls arrive on the UI thread (bridge requests are dispatched
/// there), so no internal locking is needed.
/// </summary>
public sealed class FillApprovalService
{
    /// <summary>Seconds the user has to type the number.</summary>
    public const int EntryWindowSeconds = 60;
    /// <summary>Seconds an approved code waits for the extension to pick it up.</summary>
    public const int PickupWindowSeconds = 30;
    public const int MaxWrongAttempts = 3;

    private readonly Func<DateTime> _utcNow;
    private readonly List<FillChallenge> _finished = new(); // terminal challenges, for late status polls

    public FillApprovalService(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>The challenge currently awaiting the user's number entry, if any.</summary>
    public FillChallenge? Pending { get; private set; }

    /// <summary>Seconds left to enter the number, or 0 when nothing is pending.</summary>
    public int PendingSecondsRemaining
    {
        get
        {
            ExpirePending();
            return Pending == null
                ? 0
                : Math.Max(0, EntryWindowSeconds - (int)(_utcNow() - Pending.CreatedUtc).TotalSeconds);
        }
    }

    /// <summary>Start a challenge, superseding any pending one.</summary>
    public FillChallenge Begin(string accountId, string accountDisplayName, string domain, string clientId)
    {
        ExpirePending();
        if (Pending != null) Finish(Pending, ChallengeStatus.Denied, "superseded");

        Pending = new FillChallenge
        {
            Number = RandomNumberGenerator.GetInt32(0, 100).ToString("D2"),
            AccountId = accountId,
            AccountDisplayName = accountDisplayName,
            Domain = domain,
            ClientId = clientId,
            CreatedUtc = _utcNow(),
        };
        return Pending;
    }

    /// <summary>The user typed a number into the app; match it against the pending challenge.</summary>
    public ApprovalAttemptResult TryApprove(string entered)
    {
        ExpirePending();
        var challenge = Pending;
        if (challenge == null) return ApprovalAttemptResult.NoChallenge;

        if (entered.Trim() == challenge.Number)
        {
            challenge.Status = ChallengeStatus.Approved;
            challenge.ApprovedUtc = _utcNow();
            Pending = null;
            _finished.Add(challenge);
            TrimFinished();
            return ApprovalAttemptResult.Approved;
        }

        challenge.WrongAttempts++;
        if (challenge.WrongAttempts >= MaxWrongAttempts)
        {
            Finish(challenge, ChallengeStatus.Denied, "wrong-number");
            Pending = null;
            return ApprovalAttemptResult.Denied;
        }
        return ApprovalAttemptResult.WrongNumber;
    }

    /// <summary>Deny the pending challenge (user clicked Deny, vault locked, …).</summary>
    public void DenyPending(string reason)
    {
        if (Pending == null) return;
        Finish(Pending, ChallengeStatus.Denied, reason);
        Pending = null;
    }

    /// <summary>
    /// Status for the extension's poll. Only the client that opened the
    /// challenge may query it; anyone else sees Unknown.
    /// </summary>
    public ChallengeStatus GetStatus(string challengeId, string clientId)
    {
        ExpirePending();
        var challenge = Find(challengeId);
        if (challenge == null || challenge.ClientId != clientId) return ChallengeStatus.Unknown;

        if (challenge.Status == ChallengeStatus.Approved &&
            (challenge.Consumed || (_utcNow() - challenge.ApprovedUtc!.Value).TotalSeconds > PickupWindowSeconds))
            return ChallengeStatus.Expired;

        return challenge.Status;
    }

    /// <summary>
    /// One-shot pickup of an approved challenge: succeeds exactly once, within
    /// the pickup window, for the client that opened it.
    /// </summary>
    public FillChallenge? TryConsume(string challengeId, string clientId)
    {
        var challenge = Find(challengeId);
        if (challenge == null || challenge.ClientId != clientId) return null;
        if (challenge.Status != ChallengeStatus.Approved || challenge.Consumed) return null;
        if ((_utcNow() - challenge.ApprovedUtc!.Value).TotalSeconds > PickupWindowSeconds) return null;

        challenge.Consumed = true;
        return challenge;
    }

    private FillChallenge? Find(string challengeId)
        => Pending?.Id == challengeId ? Pending : _finished.FirstOrDefault(c => c.Id == challengeId);

    private void ExpirePending()
    {
        if (Pending == null) return;
        if ((_utcNow() - Pending.CreatedUtc).TotalSeconds <= EntryWindowSeconds) return;
        Finish(Pending, ChallengeStatus.Expired, string.Empty);
        Pending = null;
    }

    private void Finish(FillChallenge challenge, ChallengeStatus status, string denyReason)
    {
        challenge.Status = status;
        challenge.DenyReason = denyReason;
        _finished.Add(challenge);
        TrimFinished();
    }

    private void TrimFinished()
    {
        const int keep = 8;
        if (_finished.Count > keep) _finished.RemoveRange(0, _finished.Count - keep);
    }
}
