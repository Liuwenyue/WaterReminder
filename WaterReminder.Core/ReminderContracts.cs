namespace WaterReminder;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<PhoneDeliveryMode>))]
public enum PhoneDeliveryMode { Auto, WeChat, PushWard, Both }

/// <summary>Today's status for the Windows client. Contains no PushWard credential.</summary>
public sealed class StatusResponse
{
    public DateTimeOffset? PauseUntil { get; set; }
    public string Date { get; set; } = "";
    public string TimeZone { get; set; } = Services.ShanghaiClock.Id;
    public string Now { get; set; } = "";
    public int ConsumedMl { get; set; }
    public int GoalMl { get; set; }
    public int SuggestedMl { get; set; }
    public string? NextReminder { get; set; }
    public string Phase { get; set; } = "";
    public bool ShowDesktop { get; set; }
    /// <summary>True when this clock time is a phone reminder window (work or after work, before quiet).</summary>
    public bool PushWindow { get; set; }
    public bool RemindersPaused { get; set; }
    public string StatusText { get; set; } = "";
    /// <summary>One-line status for the 352-wide compact pill. Expanded view uses <see cref="StatusText"/>.</summary>
    public string CompactStatusText { get; set; } = "";
    public int WorkCupMl { get; set; }
    public int HomeCupMl { get; set; }
    public string WorkStart { get; set; } = "";
    public string WorkEnd { get; set; } = "";
    public string LunchStart { get; set; } = "";
    public string LunchEnd { get; set; } = "";
    public string QuietStart { get; set; } = "";
    public int MinReminderGapMinutes { get; set; }
    public DesktopAlertResponse? DesktopAlert { get; set; }
}

public sealed class DesktopAlertResponse
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int SuggestedMl { get; set; }
}

/// <summary>Settings the Windows client is allowed to cache. Secrets are omitted.</summary>
public sealed class ClientSettingsResponse
{
    public string? Revision { get; set; }
    public bool PushWardConfigured { get; set; }
    public int DailyGoalMl { get; set; }
    public int WorkCupMl { get; set; }
    public int HomeCupMl { get; set; }
    public string WorkStart { get; set; } = "";
    public string WorkEnd { get; set; } = "";
    public bool LunchEnabled { get; set; }
    public string LunchStart { get; set; } = "";
    public string LunchEnd { get; set; } = "";
    public int NoDrinkBeforeEndMinutes { get; set; }
    public string QuietStart { get; set; } = "";
    public int MinReminderGapMinutes { get; set; }
    public bool MobilePushEnabled { get; set; }
    public PhoneDeliveryMode PhoneMode { get; set; }
    public bool[] ActiveDays { get; set; } = [];
    public bool RemindersPaused { get; set; }
    public string? PauseUntil { get; set; }
}

public sealed class DrinkRequest
{
    public int? AmountMl { get; set; }
    public string? Source { get; set; }
}

public sealed class DrinkResponse
{
    public int RecordedMl { get; set; }
    public int ConsumedMl { get; set; }
    public int GoalMl { get; set; }
    public string Title { get; set; } = "已记录";
    public string Message { get; set; } = "";
}

public sealed class AckRequest
{
    public string? Id { get; set; }
}

public sealed class TodayRecordsResponse
{
    public DateTimeOffset? LastReminderAt { get; set; }
    public string Date { get; set; } = "";
    public int ConsumedMl { get; set; }
    public int GoalMl { get; set; }
    public bool GoalKnown { get; set; } = true;
    public bool RemindersPaused { get; set; }
    public List<RecordEventResponse> Events { get; set; } = [];
    /// <summary>False for past days: the client shows them read-only.</summary>
    public bool Editable { get; set; } = true;
}

public sealed class HistoryDayResponse
{
    public string Date { get; set; } = "";
    public int ConsumedMl { get; set; }
    public int GoalMl { get; set; }
    public bool GoalKnown { get; set; } = true;
    public int EventCount { get; set; }
}

public sealed class HistoryResponse
{
    public List<HistoryDayResponse> Days { get; set; } = [];
}

public sealed record FamilyMember(Guid Id, string Name, bool Archived = false);
public sealed record FamilyOverview(bool IsOwner, IReadOnlyList<FamilyMember> Members);
public sealed record AddFamilyMember(string Name, string? PushWardToken);
public sealed record FamilyInvitation(FamilyMember Member, string AccessCode);
/// <summary>Owner-only update. Blank phone token preserves the member's existing credential.</summary>
public sealed record FamilySettingsUpdate(SettingsUpdate Settings, string? PushWardToken = null);

public sealed class RecordEventResponse
{
    public Guid Id { get; set; }
    public string At { get; set; } = "";
    public int AmountMl { get; set; }
    public string Source { get; set; } = "";
}

public sealed class SetTotalRequest
{
    public int TotalMl { get; set; }
}

public sealed class SetTotalResponse
{
    public int DeltaMl { get; set; }
    public int ConsumedMl { get; set; }
    public int GoalMl { get; set; }
    public bool Changed { get; set; }
}

public sealed class EditRecordRequest
{
    public int AmountMl { get; set; }
}

/// <summary>Fields the desktop settings window may change. No credentials.</summary>
public sealed class SettingsUpdate
{
    public string? ExpectedRevision { get; set; }
    public bool PreservePause { get; set; }
    public bool PauseOnly { get; set; }
    public int DailyGoalMl { get; set; }
    public int WorkCupMl { get; set; }
    public int HomeCupMl { get; set; }
    public string WorkStart { get; set; } = "";
    public string WorkEnd { get; set; } = "";
    public bool LunchEnabled { get; set; }
    public string LunchStart { get; set; } = "";
    public string LunchEnd { get; set; } = "";
    public int NoDrinkBeforeEndMinutes { get; set; }
    public string QuietStart { get; set; } = "";
    public int MinReminderGapMinutes { get; set; }
    public bool MobilePushEnabled { get; set; }
    public PhoneDeliveryMode? PhoneMode { get; set; }
    public bool[] ActiveDays { get; set; } = [];
    public bool RemindersPaused { get; set; }
    public string? PauseUntil { get; set; }
}

/// <summary>Ordered, independently durable operations. Retry unchanged IDs after an interrupted batch.</summary>
public sealed class SyncOperationsRequest
{
    public List<SyncOperation> Operations { get; set; } = [];
}
public sealed class SyncOperation
{
    public Guid Id { get; set; }
    public string Date { get; set; } = "";
    public string Kind { get; set; } = "";
    public int? AmountMl { get; set; }
    public int? TotalMl { get; set; }
    public Guid? EventId { get; set; }
    public string? At { get; set; }
    public int? GoalMl { get; set; }
    public string? Source { get; set; }
}
public sealed record SyncOperationResult(Guid Id, string Date, bool Duplicate, bool Changed);
public sealed class SyncOperationsResponse
{
    public List<SyncOperationResult> Results { get; set; } = [];
    public List<TodayRecordsResponse> Days { get; set; } = [];
    public StatusResponse Status { get; set; } = new();
}

public sealed class SyncSnapshotResponse
{
    public StatusResponse Status { get; set; } = new();
    public Guid MemberId { get; set; }
    public string MemberName { get; set; } = "";
    public List<TodayRecordsResponse> Days { get; set; } = [];
}

public sealed class WeChatStatusResponse
{
    public bool Enabled { get; set; }
    public string TemplateId { get; set; } = "";
    public bool Bound { get; set; }
    public int Credits { get; set; }
}

public sealed class WeChatCodeRequest
{
    public string? Code { get; set; }
}

public sealed class WeChatSubscriptionRequest
{
    public string? Id { get; set; }
    public string? Status { get; set; }
}
