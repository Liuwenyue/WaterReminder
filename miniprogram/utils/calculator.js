'use strict';

function pad(n) {
  return String(n).padStart(2, '0');
}

function dateKey(date) {
  return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate());
}

function sameDay(a, b) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

function atMidnight(date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate(), 0, 0, 0, 0);
}

function fromMinutes(day, minutes) {
  return new Date(atMidnight(day).getTime() + minutes * 60000);
}

function timeOfDay(date) {
  return date.getHours() * 60 + date.getMinutes() + date.getSeconds() / 60 + date.getMilliseconds() / 60000;
}

function parseTime(text) {
  const parts = String(text || '').split(':');
  const h = Number(parts[0]);
  const m = Number(parts[1] || 0);
  if (!Number.isFinite(h) || !Number.isFinite(m)) return 0;
  return h * 60 + m;
}

function formatClock(date) {
  return pad(date.getHours()) + ':' + pad(date.getMinutes());
}

function roundAway(value) {
  return (value < 0 ? -1 : 1) * Math.round(Math.abs(value));
}

function roundTo5(mins) {
  return roundAway(mins / 5) * 5;
}

function floorTo5(mins) {
  return Math.floor(mins / 5) * 5;
}

function ceilToMinute(date) {
  const extra = date.getSeconds() * 1000 + date.getMilliseconds();
  if (extra === 0) return new Date(date.getTime());
  return new Date(date.getTime() - extra + 60000);
}

function defaultSettings() {
  return {
    dailyGoalMl: 2000,
    cupCapacityMl: 370,
    workCupCapacityMl: 300,
    homeCupCapacityMl: 300,
    workStart: '09:00',
    workEnd: '18:00',
    lunchEnabled: true,
    lunchStart: '11:50',
    lunchEnd: '13:30',
    noDrinkBeforeEndMinutes: 30,
    minReminderGapMinutes: 20,
    quietStart: '23:00',
    activeDays: [true, true, true, true, true, false, false],
    remindersPaused: false,
    pauseUntil: null
  };
}

function clampInt(value, fallback) {
  const n = Number(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.round(n);
}

function normalizeSettings(raw) {
  const d = defaultSettings();
  const src = raw || {};
  const workCup = src.workCupCapacityMl == null ? src.cupCapacityMl : src.workCupCapacityMl;
  const s = {
    phoneMode: src.phoneMode || 'Auto',
    mobilePushEnabled: src.mobilePushEnabled !== false,
    dailyGoalMl: clampInt(src.dailyGoalMl == null ? d.dailyGoalMl : src.dailyGoalMl, d.dailyGoalMl),
    cupCapacityMl: clampInt(src.cupCapacityMl == null ? d.cupCapacityMl : src.cupCapacityMl, d.cupCapacityMl),
    workCupCapacityMl: clampInt(workCup == null ? d.workCupCapacityMl : workCup, d.workCupCapacityMl),
    homeCupCapacityMl: clampInt(src.homeCupCapacityMl == null ? d.homeCupCapacityMl : src.homeCupCapacityMl, d.homeCupCapacityMl),
    workStart: src.workStart || d.workStart,
    workEnd: src.workEnd || d.workEnd,
    lunchEnabled: src.lunchEnabled == null ? d.lunchEnabled : !!src.lunchEnabled,
    lunchStart: src.lunchStart || d.lunchStart,
    lunchEnd: src.lunchEnd || d.lunchEnd,
    noDrinkBeforeEndMinutes: clampInt(src.noDrinkBeforeEndMinutes == null ? d.noDrinkBeforeEndMinutes : src.noDrinkBeforeEndMinutes, d.noDrinkBeforeEndMinutes),
    minReminderGapMinutes: clampInt(src.minReminderGapMinutes == null ? d.minReminderGapMinutes : src.minReminderGapMinutes, d.minReminderGapMinutes),
    quietStart: src.quietStart || d.quietStart,
    activeDays: Array.isArray(src.activeDays) && src.activeDays.length === 7 ? src.activeDays.map(Boolean) : d.activeDays.slice(),
    remindersPaused: !!src.remindersPaused,
    pauseUntil: src.pauseUntil || null
  };
  ['workStart', 'workEnd', 'lunchStart', 'lunchEnd', 'quietStart'].forEach(function (key) {
    if (!/^\d{2}:\d{2}$/.test(s[key])) s[key] = d[key];
  });
  if (s.workCupCapacityMl > 0) s.cupCapacityMl = s.workCupCapacityMl;
  return s;
}

function workStartMin(s) { return parseTime(s.workStart); }
function workEndMin(s) { return parseTime(s.workEnd); }
function quietMin(s) { return parseTime(s.quietStart); }

function cutoff(s) {
  return workEndMin(s) - Math.max(0, s.noDrinkBeforeEndMinutes);
}

function effectiveLunch(s) {
  if (!s.lunchEnabled) return null;
  const startRaw = parseTime(s.lunchStart);
  const endRaw = parseTime(s.lunchEnd);
  if (endRaw <= startRaw) return null;
  const start = Math.max(startRaw, workStartMin(s));
  const end = Math.min(endRaw, cutoff(s));
  return end > start ? { start: start, end: end } : null;
}

function schedule(s, day) {
  const result = [];
  if (s.dailyGoalMl <= 0 || s.cupCapacityMl <= 0) return result;
  const cut = cutoff(s);
  const ws = workStartMin(s);
  if (cut <= ws) return result;
  const lunch = effectiveLunch(s);
  const lunchLen = lunch ? lunch.end - lunch.start : 0;
  const available = cut - ws - lunchLen;
  if (available <= 0) return result;
  const n = Math.ceil(s.dailyGoalMl / s.cupCapacityMl);
  const step = available / (n + 0.5);
  for (let k = 1; k <= n; k++) {
    let t = ws + k * step;
    let beforeLunch = true;
    if (lunch && t >= lunch.start) {
      t += lunchLen;
      beforeLunch = false;
    }
    let rounded = roundTo5(t);
    if (lunch) {
      if (beforeLunch && rounded >= lunch.start) rounded = floorTo5(t);
      if (rounded >= lunch.start && rounded < lunch.end) rounded = lunch.end;
    }
    if (rounded >= cut) rounded = floorTo5(cut - 1);
    if (rounded <= ws) rounded = ws + 5;
    const due = k === n ? s.dailyGoalMl : Math.min(s.dailyGoalMl, k * s.cupCapacityMl);
    const at = fromMinutes(day, rounded);
    if (result.length > 0 && result[result.length - 1].at.getTime() >= at.getTime()) {
      result[result.length - 1].dueMl = due;
    } else {
      result.push({ at: at, dueMl: due });
    }
  }
  return result;
}

function phaseAt(s, now) {
  const t = timeOfDay(now);
  if (t < workStartMin(s)) return 'BeforeWork';
  if (t >= workEndMin(s)) return 'AfterWork';
  if (t >= cutoff(s)) return 'NoDrinkWindow';
  const lunch = effectiveLunch(s);
  if (lunch && t >= lunch.start && t < lunch.end) return 'Lunch';
  return 'Working';
}

function gapOf(s) {
  let minutes = s.minReminderGapMinutes;
  if (!(minutes > 0)) minutes = 20;
  return minutes;
}

function workCupMl(s) {
  if (s.workCupCapacityMl > 0) return s.workCupCapacityMl;
  if (s.cupCapacityMl > 0) return s.cupCapacityMl;
  return 370;
}

function homeCupMl(s) {
  return s.homeCupCapacityMl > 0 ? s.homeCupCapacityMl : 800;
}

function workPortionMl(s, remaining) {
  if (remaining <= 0) return 0;
  if (quietMin(s) <= workEndMin(s)) return remaining;
  return Math.max(0, remaining - homeCupMl(s));
}

function isActiveDay(s, date) {
  const dow = date.getDay();
  const index = dow === 0 ? 6 : dow - 1;
  return !!s.activeDays[index];
}

function currentCupMl(s, now) {
  if (isActiveDay(s, now) && timeOfDay(now) >= workStartMin(s) && timeOfDay(now) < workEndMin(s)) {
    return workCupMl(s);
  }
  return homeCupMl(s);
}

function consumedMl(record) {
  const events = (record && record.events) || [];
  let sum = 0;
  for (let i = 0; i < events.length; i++) sum += Number(events[i].amountMl) || 0;
  return Math.max(0, sum);
}

function latestPositive(record, now) {
  const events = (record && record.events) || [];
  let best = null;
  for (let i = 0; i < events.length; i++) {
    const event = events[i];
    const at = event.at instanceof Date ? event.at : new Date(event.at);
    if (event.amountMl > 0 && sameDay(at, now) && at.getTime() <= now.getTime()) {
      if (!best || at > best) best = at;
    }
  }
  return best;
}

function availableMinutes(s, from) {
  const start = Math.max(timeOfDay(from), workStartMin(s));
  const cut = cutoff(s);
  if (start >= cut) return 0;
  let total = cut - start;
  const lunch = effectiveLunch(s);
  if (lunch) {
    const os = Math.max(start, lunch.start);
    const oe = Math.min(cut, lunch.end);
    if (oe > os) total -= oe - os;
  }
  return Math.max(0, total);
}

function addAvailable(s, from, minutes) {
  let t = timeOfDay(from) < workStartMin(s) ? fromMinutes(from, workStartMin(s)) : new Date(from.getTime());
  const lunch = effectiveLunch(s);
  if (lunch) {
    const ls = fromMinutes(t, lunch.start);
    const le = fromMinutes(t, lunch.end);
    if (t >= ls && t < le) {
      t = new Date(le.getTime());
    } else if (t < ls && new Date(t.getTime() + minutes * 60000) >= ls) {
      const remain = minutes - (ls.getTime() - t.getTime()) / 60000;
      return new Date(le.getTime() + remain * 60000);
    }
  }
  return new Date(t.getTime() + minutes * 60000);
}

function plan(totalReminders, intervalMin, next, dueMl, suggestedMl) {
  return {
    totalReminders: totalReminders,
    intervalMin: intervalMin,
    nextReminder: next,
    dueMl: dueMl,
    suggestedMl: suggestedMl
  };
}

function asDate(value) {
  if (!value) return null;
  return value instanceof Date ? value : new Date(value);
}

function calculateEvening(s, record, now, lastReminderAt, remaining) {
  const home = homeCupMl(s);
  const cups = Math.ceil(remaining / home);
  const quiet = fromMinutes(now, quietMin(s));
  let anchor = fromMinutes(now, workEndMin(s));
  const lastDrink = latestPositive(record, now);
  if (lastDrink && lastDrink > anchor) anchor = lastDrink;
  const reminded = asDate(lastReminderAt);
  if (reminded && sameDay(reminded, now) && reminded > anchor && reminded <= now) anchor = reminded;
  if (anchor >= quiet) return plan(cups, 0, null, s.dailyGoalMl, 0);
  const available = (quiet.getTime() - anchor.getTime()) / 60000;
  const gap = gapOf(s);
  let interval = available / (cups + 0.5);
  if (interval < gap) interval = gap;
  let next = ceilToMinute(new Date(anchor.getTime() + interval * 60000));
  if (next >= quiet) {
    const latest = new Date(quiet.getTime() - 5 * 60000);
    const minNext = new Date(anchor.getTime() + gap * 60000);
    next = latest >= minNext ? latest : null;
    if (!next) return plan(cups, interval, null, s.dailyGoalMl, 0);
  }
  const dueNow = next <= now;
  const suggested = dueNow ? Math.min(home, remaining) : 0;
  return plan(cups, interval, next, consumedMl(record) + suggested, suggested);
}

function calculateDynamic(s, record, now, lastReminderAt) {
  const workCup = workCupMl(s);
  if (s.dailyGoalMl <= 0 || workCup <= 0) return plan(0, 0, null, 0, 0);
  const consumed = consumedMl(record);
  const remaining = Math.max(0, s.dailyGoalMl - consumed);
  if (remaining === 0) return plan(0, 0, null, s.dailyGoalMl, 0);
  if (now >= fromMinutes(now, workEndMin(s)) && timeOfDay(now) < quietMin(s)) {
    return calculateEvening(s, record, now, lastReminderAt, remaining);
  }
  const workRemaining = workPortionMl(s, remaining);
  if (workRemaining <= 0) return plan(0, 0, null, consumed, 0);
  const cups = Math.ceil(workRemaining / workCup);
  const cut = fromMinutes(now, cutoff(s));
  const ws = fromMinutes(now, workStartMin(s));
  if (now >= cut || cut <= ws) return plan(cups, 0, null, s.dailyGoalMl, 0);
  let anchor = ws;
  const lastDrink = latestPositive(record, now);
  if (lastDrink && lastDrink > anchor) anchor = lastDrink;
  const reminded = asDate(lastReminderAt);
  if (reminded && sameDay(reminded, now) && reminded > anchor && reminded <= now) anchor = reminded;
  if (anchor >= cut) return plan(cups, 0, null, s.dailyGoalMl, 0);
  const available = availableMinutes(s, anchor);
  const gap = gapOf(s);
  let interval = available / (cups + 0.5);
  if (interval < gap) interval = gap;
  let next = ceilToMinute(addAvailable(s, anchor, interval));
  if (next >= cut) {
    const latest = new Date(cut.getTime() - 5 * 60000);
    const minNext = addAvailable(s, anchor, gap);
    next = latest >= minNext ? latest : null;
    if (!next) return plan(cups, interval, null, s.dailyGoalMl, 0);
  }
  const lunch = effectiveLunch(s);
  if (lunch) {
    const ls = fromMinutes(now, lunch.start);
    const le = fromMinutes(now, lunch.end);
    if (next >= ls && next < le) next = new Date(le.getTime());
    if (now >= ls && now < le && next < le) next = new Date(le.getTime());
  }
  const dueNow = next <= now;
  const suggested = dueNow ? Math.min(workCup, workRemaining) : 0;
  return plan(cups, interval, next, consumed + suggested, suggested);
}

function isPaused(s, now) {
  if (!s.remindersPaused) return false;
  if (s.pauseUntil && new Date(s.pauseUntil) <= now) return false;
  return true;
}

function statusLine(s, record, reminderPlan, now, compact) {
  const consumed = consumedMl(record);
  if (consumed >= s.dailyGoalMl) return compact ? '今日目标已完成' : '太棒了，今天的喝水目标已完成！';
  if (isPaused(s, now)) {
    if (s.pauseUntil) {
      const until = new Date(s.pauseUntil);
      if (until > now && sameDay(until, now)) {
        const clock = formatClock(until);
        return compact ? '暂停至 ' + clock : '提醒已暂停至 ' + clock;
      }
    }
    return compact ? '今日暂停' : '今日提醒已暂停';
  }
  if (!isActiveDay(s, now)) return '今天休息，不提醒';
  const phase = phaseAt(s, now);
  if (phase === 'NoDrinkWindow') return '下班前不再提醒';
  if (!reminderPlan.nextReminder) {
    const left = Math.max(0, s.dailyGoalMl - consumed);
    if (left > 0 && (phase === 'BeforeWork' || phase === 'Working' || phase === 'Lunch') && workPortionMl(s, left) <= 0) {
      return '下班后再提醒';
    }
    return '今日提醒已结束';
  }
  if (phase === 'Lunch') {
    const clock = formatClock(reminderPlan.nextReminder);
    return compact ? '午休中 · 下次 ' + clock : '午休中 · 下次提醒 ' + clock;
  }
  const remainingNow = Math.max(0, s.dailyGoalMl - consumed);
  let cap = phase === 'AfterWork' ? remainingNow : workPortionMl(s, remainingNow);
  if (cap <= 0) cap = remainingNow;
  const cup = Math.min(currentCupMl(s, now), cap);
  if (reminderPlan.suggestedMl > 0) {
    return compact
      ? '该喝水了 · ' + reminderPlan.suggestedMl + ' ml'
      : '该喝水了，建议现在补充 ' + reminderPlan.suggestedMl + ' ml';
  }
  const clock = formatClock(reminderPlan.nextReminder);
  return compact ? '下次 ' + clock + ' · ' + cup + ' ml' : '下次提醒 ' + clock + ' · 建议 ' + cup + ' ml';
}

function shouldNotify(s, now) {
  if (isPaused(s, now) || !isActiveDay(s, now)) return false;
  const t = timeOfDay(now);
  if (t < workStartMin(s) || t >= quietMin(s)) return false;
  const phase = phaseAt(s, now);
  return phase === 'Working' || phase === 'AfterWork';
}

const MAX_DAILY_TOTAL_ML = 20000;

function adjustmentFor(record, newTotal) {
  const events = (record && record.events) || [];
  let sum = 0;
  for (let i = 0; i < events.length; i++) sum += Number(events[i].amountMl) || 0;
  return newTotal - sum;
}

function tryParseTotal(text) {
  let t = String(text == null ? '' : text).trim().replace(/,/g, '').replace(/，/g, '');
  if (/ml$/i.test(t)) t = t.slice(0, -2).trim();
  if (!t) return { ok: false, error: '请输入总量' };
  if (!/^\d+$/.test(t)) return { ok: false, error: '请输入 0 或正整数' };
  const total = Number(t);
  if (total > MAX_DAILY_TOTAL_ML) return { ok: false, error: '最多 20,000 ml' };
  return { ok: true, total: total, error: null };
}

function phaseLabel(phase) {
  if (phase === 'BeforeWork') return '上班前';
  if (phase === 'Working') return '工作中';
  if (phase === 'Lunch') return '午休';
  if (phase === 'NoDrinkWindow') return '下班前';
  if (phase === 'AfterWork') return '下班后';
  return '';
}

module.exports = {
  MAX_DAILY_TOTAL_ML: MAX_DAILY_TOTAL_ML,
  dateKey: dateKey,
  sameDay: sameDay,
  formatClock: formatClock,
  defaultSettings: defaultSettings,
  normalizeSettings: normalizeSettings,
  cutoff: cutoff,
  effectiveLunch: effectiveLunch,
  schedule: schedule,
  phaseAt: phaseAt,
  phaseLabel: phaseLabel,
  gapOf: gapOf,
  workCupMl: workCupMl,
  homeCupMl: homeCupMl,
  workPortionMl: workPortionMl,
  isActiveDay: isActiveDay,
  currentCupMl: currentCupMl,
  consumedMl: consumedMl,
  calculateDynamic: calculateDynamic,
  statusLine: statusLine,
  shouldNotify: shouldNotify,
  isPaused: isPaused,
  adjustmentFor: adjustmentFor,
  tryParseTotal: tryParseTotal
};
