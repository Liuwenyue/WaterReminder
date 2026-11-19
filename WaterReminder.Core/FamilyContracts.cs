namespace WaterReminder;
public sealed record CreateMemberInvite(string? Name, Guid? MemberId, string? ServerUrl, string? PushWardToken = null);
public sealed record InvitationTokenRequest(string InviteToken);
public sealed record MemberInvite(FamilyMember Member, string InviteToken, string ServerUrl, DateTimeOffset ExpiresAt) { public int Version => 1; }
public sealed record InvitationPreview(FamilyMember Member, DateTimeOffset ExpiresAt, string ServerUrl);
public sealed record RedeemedInvitation(FamilyMember Member, string AccessCode, string ServerUrl);
public sealed record MemberPushFeedback(string State, string Message, DateTime? AttemptedAt = null, DateTime? ChannelAcceptedAt = null, DateTime? ConfirmedAt = null);
public sealed record FamilyProgress(FamilyMember Member, int ConsumedMl, int GoalMl, string? LastRecordAt, int WeekAchievedDays, int WeekRecordedDays, MemberPushFeedback Push);
public sealed record FamilyProgressOverview(string WeekStart, IReadOnlyList<FamilyProgress> Members);
