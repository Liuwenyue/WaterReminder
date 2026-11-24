namespace WaterReminder;
public sealed record MemberPushFeedback(string State, string Message, DateTime? AttemptedAt = null, DateTime? ChannelAcceptedAt = null, DateTime? ConfirmedAt = null);
public sealed record FamilyProgress(FamilyMember Member, int ConsumedMl, int GoalMl, string? LastRecordAt, int WeekAchievedDays, int WeekRecordedDays, MemberPushFeedback Push);
public sealed record FamilyProgressOverview(string WeekStart, IReadOnlyList<FamilyProgress> Members);
