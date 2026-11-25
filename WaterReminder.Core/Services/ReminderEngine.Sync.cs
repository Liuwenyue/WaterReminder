using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WaterReminder.Services;

public sealed partial class ReminderEngine
{
    public SyncSnapshotResponse SyncSnapshot(Guid memberId, string memberName, int days)
    {
        lock (_gate)
        {
            var now = RefreshLocked();
            var today = DateOnly.FromDateTime(now);
            var count = Math.Clamp(days <= 0 ? 30 : days, 1, 366);
            var stored = _storage.ListDailyRecords().ToDictionary(day => day.Date);
            var snapshot = new SyncSnapshotResponse { MemberId = memberId, MemberName = memberName, Status = BuildStatusLocked(now) };
            for (var offset = 0; offset < count; offset++)
            {
                var date = today.AddDays(-offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var record = offset == 0 ? _progress.Today : stored.GetValueOrDefault(date) ?? new DailyRecord { Date = date };
                snapshot.Days.Add(MapRecords(record, offset == 0));
            }
            return snapshot;
        }
    }

    /// <summary>Member-scoped durable operations; ledger and receipt are saved in the same atomic file replacement.</summary>
    public bool TrySyncOperations(SyncOperationsRequest? request, out SyncOperationsResponse? response,
        out string? error, out bool conflict)
    {
        response = null;
        error = null;
        conflict = false;
        if (request?.Operations is null || request.Operations.Count is < 1 or > 100)
        {
            error = "每批应包含 1–100 条操作。";
            return false;
        }
        lock (_gate)
        {
            EnsureActiveLocked();
            var now = RefreshLocked();
            var today = DateOnly.FromDateTime(now);
            foreach (var operation in request.Operations)
                if (!ValidateOperation(operation, today, out error)) return false;

            var stored = _storage.ListDailyRecords().ToDictionary(record => record.Date);
            var working = new Dictionary<string, DailyRecord>();
            var touched = new HashSet<string>();
            var seen = new Dictionary<Guid, string>();
            var result = new SyncOperationsResponse();

            DailyRecord ForDate(string date)
            {
                if (!working.TryGetValue(date, out var record))
                {
                    record = stored.TryGetValue(date, out var existing) ? existing : new DailyRecord { Date = date };
                    working[date] = record;
                }
                return record;
            }

            foreach (var operation in request.Operations)
            {
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(operation))));
                if (seen.TryGetValue(operation.Id, out var repeated))
                {
                    if (repeated != fingerprint)
                    {
                        error = "同一操作编号的内容已改变，请保留原操作并刷新记录。";
                        conflict = true;
                        return false;
                    }
                    result.Results.Add(new(operation.Id, operation.Date, true, false));
                    touched.Add(operation.Date);
                    continue;
                }
                SyncOperationReceipt? previous = null;
                foreach (var record in stored.Values)
                    if (record.SyncReceipts is not null && record.SyncReceipts.TryGetValue(operation.Id, out var receipt))
                        previous = receipt;
                if (previous is not null)
                {
                    if (previous.Fingerprint != fingerprint)
                    {
                        error = "同一操作编号的内容已改变，请保留原操作并刷新记录。";
                        conflict = true;
                        return false;
                    }
                    result.Results.Add(new(operation.Id, operation.Date, true, previous.Changed));
                    seen[operation.Id] = fingerprint;
                    touched.Add(operation.Date);
                    continue;
                }
                var target = ForDate(operation.Date);
                var date = DateOnly.ParseExact(operation.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!ApplyOperation(target, operation, date, today, now, out var changed, out error))
                {
                    conflict = true;
                    return false;
                }
                (target.SyncReceipts ??= [])[operation.Id] = new(fingerprint, changed);
                seen[operation.Id] = fingerprint;
                touched.Add(operation.Date);
                result.Results.Add(new(operation.Id, operation.Date, false, changed));
            }

            if (working.Count > 0) _storage.SaveDailyRecords(working.Values);
            now = RefreshLocked();
            result.Days = touched.OrderByDescending(date => date).Select(date =>
                MapRecords(ForDate(date),
                    date == today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).ToList();
            result.Status = BuildStatusLocked(now);
            response = result;
            return true;
        }
    }

    private static bool ValidateOperation(SyncOperation? operation, DateOnly today, out string? error)
    {
        error = "操作参数无法识别。";
        if (operation is null || operation.Id == Guid.Empty ||
            !DateOnly.TryParseExact(operation.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date > today || date.Year < 1900 || operation.Source?.Length > 64) return false;
        if (operation.GoalMl is < 100 or > 10000) return false;
        if (operation.At is { } text && (!TryOperationTime(text, out var at) || DateOnly.FromDateTime(at) != date))
        {
            error = "记录时间必须属于操作指定的上海日期。";
            return false;
        }
        var valid = operation.Kind switch
        {
            "drink" => operation.AmountMl is > 0 and <= 5000,
            "import" => operation.AmountMl is not null and not 0 and >= -20000 and <= 20000
                && operation.EventId is { } eventId && eventId != Guid.Empty && operation.At is not null,
            "setTotal" => operation.TotalMl is >= 0 and <= 20000,
            "edit" => operation.EventId is { } target && target != Guid.Empty &&
                operation.AmountMl is not null and not 0 and >= -3000 and <= 3000,
            "delete" => operation.EventId is { } eventId && eventId != Guid.Empty,
            "clear" or "pause" or "resume" => true,
            _ => false,
        };
        if (valid) error = null;
        return valid;
    }

    private static bool TryOperationTime(string text, out DateTime time)
    {
        time = default;
        if (text.Length > 40 || !text.Contains('T')) return false;
        if (text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10)
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)) return false;
            time = offset.ToOffset(TimeSpan.FromHours(8)).DateTime;
            return true;
        }
        return DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    private bool ApplyOperation(DailyRecord record, SyncOperation operation, DateOnly date, DateOnly today,
        DateTime now, out bool changed, out string? error)
    {
        changed = false;
        error = null;
        if (date == today) record.GoalSnapshotMl = _settings.DailyGoalMl;
        // A legacy import can supply its original goal, but cannot rewrite a known day goal.
        if (operation.Kind == "import" && date != today && operation.GoalMl is { } goal)
        {
            if (record.GoalSnapshotMl is { } existing && existing != goal)
            {
                error = "导入目标与该日已保存的目标不一致。";
                return false;
            }
            record.GoalSnapshotMl = goal;
        }
        var at = operation.At is { } text && TryOperationTime(text, out var parsed)
            ? parsed : date == today ? now : date.ToDateTime(new TimeOnly(12, 0));
        switch (operation.Kind)
        {
            case "drink":
            case "import":
                var eventId = operation.Kind == "import" ? operation.EventId!.Value : operation.Id;
                var amount = operation.AmountMl!.Value;
                var source = string.IsNullOrWhiteSpace(operation.Source)
                    ? operation.Kind == "import" ? "legacy" : "desktop" : operation.Source.Trim();
                var existingEvent = record.Events.FirstOrDefault(e => e.Id == eventId);
                if (existingEvent is not null)
                {
                    if (existingEvent.AmountMl == amount && existingEvent.At == at && existingEvent.Source == source) break;
                    error = "记录编号已存在且内容不同，请刷新后处理。";
                    return false;
                }
                var raw = record.Events.Sum(item => item.AmountMl);
                if (raw > WaterCalculator.MaxDailyTotalMl - amount)
                {
                    error = $"最多 {WaterCalculator.MaxDailyTotalMl:N0} ml";
                    return false;
                }
                if (amount != 0)
                {
                    record.Events.Add(new WaterEvent { Id = eventId, AmountMl = amount, At = at, Source = source });
                    changed = true;
                }
                break;
            case "setTotal":
                var delta = operation.TotalMl!.Value - record.Events.Sum(e => e.AmountMl);
                if (delta != 0)
                {
                    record.Events.Add(new WaterEvent { Id = operation.Id, AmountMl = delta, At = at, Source = "adjust" });
                    changed = true;
                }
                break;
            case "delete": changed = record.Events.RemoveAll(e => e.Id == operation.EventId) > 0; break;
            case "edit":
                var edited = record.Events.FirstOrDefault(e => e.Id == operation.EventId);
                if (edited is null)
                {
                    error = "找不到这条记录，请刷新后重试。";
                    return false;
                }
                var updatedAmount = operation.AmountMl!.Value;
                var updatedTotal = record.Events.Sum(e => (long)e.AmountMl) - edited.AmountMl + updatedAmount;
                if (updatedTotal > WaterCalculator.MaxDailyTotalMl)
                {
                    error = $"最多 {WaterCalculator.MaxDailyTotalMl:N0} ml";
                    return false;
                }
                changed = edited.AmountMl != updatedAmount;
                edited.AmountMl = updatedAmount;
                break;
            case "clear": changed = record.Events.Count > 0; record.Events.Clear(); break;
            case "pause":
            case "resume":
                if (date != today) break;
                var paused = operation.Kind == "pause";
                changed = record.RemindersPausedOverride != paused;
                record.RemindersPausedOverride = paused;
                break;
        }
        return true;
    }
}
