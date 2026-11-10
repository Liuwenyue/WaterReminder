namespace WaterReminder.Services;

/// <summary>
/// Timing rules for the after-work phone push. A successful publish — a 2xx PushWard API
/// response — restarts the normal (at least 20 minute) countdown via <c>MarkReminded</c>. A
/// failed one only causes a short private retry backoff, so a flaky server cannot turn the
/// 30-second timer into spam.
/// </summary>
public static class MobilePushDeliveryPolicy
{
    /// <summary>How long to wait after a failed publish before trying again (1–2 minutes).</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(90);

    /// <summary>Result of one publish attempt: mark the reminder only on success, otherwise retry later.</summary>
    public readonly record struct DeliveryOutcome(bool MarkReminded, DateTime NextAttempt);

    public static bool ShouldAttempt(DateTime now, DateTime nextAttemptAt, bool inFlight)
        => !inFlight && now >= nextAttemptAt;

    public static DeliveryOutcome AfterPublish(bool published, DateTime attemptedAt)
        => published
            ? new DeliveryOutcome(MarkReminded: true, NextAttempt: DateTime.MinValue)
            : new DeliveryOutcome(MarkReminded: false, NextAttempt: attemptedAt + FailureBackoff);
}
