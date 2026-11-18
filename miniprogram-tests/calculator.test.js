'use strict';

const assert = require('assert');
const calc = require('../miniprogram/utils/calculator.js');

let passed = 0;
function test(name, fn) {
  try {
    fn();
    passed += 1;
  } catch (error) {
    console.error('FAIL ' + name);
    console.error(error.stack || error);
    process.exitCode = 1;
  }
}

function day(y, m, d, h, min, s) {
  return new Date(y, m - 1, d, h || 0, min || 0, s || 0, 0);
}
function T(h, m) { return day(2026, 11, 3, h, m || 0); }
function clock(date) { return calc.formatClock(date); }
function times(s, date) {
  return calc.schedule(s, date).map(function (slot) { return clock(slot.at); });
}

function settings(overrides) {
  const s = calc.defaultSettings();
  Object.keys(overrides || {}).forEach(function (key) { s[key] = overrides[key]; });
  if (overrides && overrides.cupCapacityMl != null && overrides.workCupCapacityMl == null) {
    s.workCupCapacityMl = overrides.cupCapacityMl;
  }
  return s;
}

function mine() {
  return settings({
    dailyGoalMl: 2220,
    cupCapacityMl: 370,
    workCupCapacityMl: 370,
    homeCupCapacityMl: 800,
    workStart: '08:30',
    workEnd: '18:00',
    lunchStart: '12:00',
    lunchEnd: '13:30',
    activeDays: [true, true, true, true, true, false, false]
  });
}

function desktopDefault() {
  return settings({
    dailyGoalMl: 2000,
    cupCapacityMl: 300,
    workCupCapacityMl: 300,
    workStart: '09:00',
    workEnd: '18:00'
  });
}

function rec(events) {
  return { events: events || [] };
}
function ev(h, m, ml) {
  return { at: T(h, m), amountMl: ml, source: 'cup' };
}

function one(sh, sm, ls, lsm, le, lem, eh, em, noDrink) {
  return settings({
    dailyGoalMl: 300,
    cupCapacityMl: 300,
    workCupCapacityMl: 300,
    workStart: String(sh).padStart(2, '0') + ':' + String(sm).padStart(2, '0'),
    lunchStart: String(ls).padStart(2, '0') + ':' + String(lsm).padStart(2, '0'),
    lunchEnd: String(le).padStart(2, '0') + ':' + String(lem).padStart(2, '0'),
    workEnd: String(eh).padStart(2, '0') + ':' + String(em).padStart(2, '0'),
    noDrinkBeforeEndMinutes: noDrink
  });
}

test('schedule matches the pinned workday', function () {
  assert.deepStrictEqual(times(mine(), T(0, 0)), ['09:40', '10:50', '11:55', '14:35', '15:45', '16:55']);
  assert.deepStrictEqual(calc.schedule(mine(), T(0, 0)).map(function (slot) { return slot.dueMl; }), [370, 740, 1110, 1480, 1850, 2220]);
});

test('ceiling reminder count', function () {
  const s = settings({ dailyGoalMl: 2000, cupCapacityMl: 300, workStart: '09:00', workEnd: '18:00' });
  assert.strictEqual(calc.schedule(s, day(2026, 11, 2, 12)).length, 7);
});

test('single reminder reaches the smaller goal', function () {
  const s = settings({ dailyGoalMl: 200, cupCapacityMl: 300, workStart: '09:00', workEnd: '18:00' });
  const slots = calc.schedule(s, day(2026, 11, 2, 10));
  assert.strictEqual(slots.length, 1);
  assert.strictEqual(slots[0].dueMl, 200);
});

test('static schedule stays before work end and out of lunch', function () {
  const goals = [[2220, 370], [2000, 300], [1500, 500], [2500, 500], [3000, 200], [200, 300], [10000, 50]];
  goals.forEach(function (pair) {
    const s = mine();
    s.dailyGoalMl = pair[0];
    s.cupCapacityMl = pair[1];
    const slots = calc.schedule(s, T(0, 0));
    assert.ok(slots.length > 0);
    assert.strictEqual(slots[slots.length - 1].dueMl, pair[0]);
    slots.forEach(function (slot, index) {
      const mins = slot.at.getHours() * 60 + slot.at.getMinutes();
      assert.strictEqual(mins % 5, 0);
      assert.ok(mins > 8 * 60 + 30);
      assert.ok(mins < 17 * 60 + 30, clock(slot.at));
      assert.ok(!(mins >= 12 * 60 && mins < 13 * 60 + 30), clock(slot.at));
      if (index > 0) {
        assert.ok(slot.at > slots[index - 1].at);
        assert.ok(slot.dueMl >= slots[index - 1].dueMl);
      }
    });
  });
});

test('lunch boundaries', function () {
  assert.deepStrictEqual(times(one(9, 0, 10, 0, 11, 0, 11, 30, 0), T(0, 0)), ['11:00']);
  assert.deepStrictEqual(times(one(10, 0, 10, 40, 12, 0, 14, 30, 30), T(0, 0)), ['13:05']);
  assert.deepStrictEqual(times(one(10, 0, 10, 40, 12, 0, 13, 30, 30), T(0, 0)), ['12:25']);
  assert.deepStrictEqual(times(one(10, 0, 10, 55, 11, 20, 11, 30, 0), T(0, 0)), ['10:45']);
  assert.deepStrictEqual(times(one(10, 0, 10, 45, 11, 10, 11, 30, 0), T(0, 0)), ['10:40']);
});

test('lunch disabled can land inside the old lunch window', function () {
  const s = mine();
  s.lunchEnabled = false;
  assert.ok(times(s, T(0, 0)).some(function (t) { return t >= '12:00' && t < '13:30'; }));
});

test('invalid work hours produce an empty plan', function () {
  const s = settings({ workStart: '18:00', workEnd: '09:00' });
  const now = day(2026, 11, 2, 8);
  assert.strictEqual(calc.schedule(s, now).length, 0);
  assert.strictEqual(calc.calculateDynamic(s, rec(), now).nextReminder, null);
});

test('active days are Monday based', function () {
  const s = settings({ activeDays: [true, false, false, false, false, false, false] });
  assert.strictEqual(calc.isActiveDay(s, day(2026, 11, 2)), true);
  assert.strictEqual(calc.isActiveDay(s, day(2026, 11, 3)), false);
  assert.strictEqual(calc.isActiveDay(settings(), day(2026, 11, 7)), false);
  assert.strictEqual(calc.isActiveDay(settings(), day(2026, 11, 8)), false);
  assert.strictEqual(calc.isActiveDay(settings(), day(2026, 11, 6)), true);
});

test('dynamic plan golden times', function () {
  const onPace = calc.calculateDynamic(mine(), rec([ev(9, 50, 370)]), T(10, 0));
  assert.strictEqual(clock(onPace.nextReminder), '11:36');
  assert.strictEqual(onPace.suggestedMl, 0);

  const pace = calc.calculateDynamic(mine(), rec([ev(10, 0, 370)]), T(10, 5));
  const ahead = calc.calculateDynamic(mine(), rec([ev(10, 0, 1110)]), T(10, 5));
  assert.strictEqual(clock(pace.nextReminder), '11:43');
  assert.strictEqual(clock(ahead.nextReminder), '15:30');

  const behind = calc.calculateDynamic(mine(), rec(), T(11, 0));
  assert.strictEqual(behind.suggestedMl, 370);
  assert.ok(behind.nextReminder <= T(11, 0));
  const after = calc.calculateDynamic(mine(), rec(), T(11, 1), T(11, 0));
  assert.strictEqual(clock(after.nextReminder), '13:37');
  assert.strictEqual(after.suggestedMl, 0);

  const gap = calc.calculateDynamic(mine(), rec(), T(16, 31), T(16, 30));
  assert.strictEqual(clock(gap.nextReminder), '16:50');

  const skip = calc.calculateDynamic(mine(), rec([ev(11, 50, 1110)]), T(11, 51));
  assert.strictEqual(clock(skip.nextReminder), '16:07');

  const lunch = calc.calculateDynamic(mine(), rec([ev(9, 0, 370)]), T(12, 30));
  assert.strictEqual(clock(lunch.nextReminder), '13:30');
  assert.strictEqual(lunch.suggestedMl, 0);
  assert.strictEqual(calc.statusLine(mine(), rec([ev(9, 0, 370)]), lunch, T(12, 30), false), '午休中 · 下次提醒 13:30');

  assert.strictEqual(clock(calc.calculateDynamic(mine(), rec(), T(8, 0)).nextReminder), '10:10');

  const near = rec([ev(16, 0, 1050)]);
  assert.strictEqual(calc.calculateDynamic(mine(), near, T(17, 10)).suggestedMl, 370);
  assert.strictEqual(clock(calc.calculateDynamic(mine(), near, T(17, 1), T(17, 0)).nextReminder), '17:20');
  assert.strictEqual(calc.calculateDynamic(mine(), near, T(17, 11), T(17, 10)).nextReminder, null);
  assert.strictEqual(calc.calculateDynamic(mine(), near, T(17, 35)).nextReminder, null);
  assert.strictEqual(calc.statusLine(mine(), near, calc.calculateDynamic(mine(), near, T(17, 35)), T(17, 35), false), '下班前不再提醒');
});

test('goal reached and weekend status', function () {
  const done = rec([ev(9, 0, 2220)]);
  const plan = calc.calculateDynamic(mine(), done, T(10, 0));
  assert.strictEqual(plan.nextReminder, null);
  assert.strictEqual(plan.suggestedMl, 0);
  assert.strictEqual(calc.statusLine(mine(), done, plan, T(10, 0), true), '今日目标已完成');
  const sat = day(2026, 11, 7, 10);
  const weekend = calc.calculateDynamic(mine(), rec(), sat);
  assert.strictEqual(calc.statusLine(mine(), rec(), weekend, sat, false), '今天休息，不提醒');
});

test('default workday reserves one home cup', function () {
  const s = calc.defaultSettings();
  const plan = calc.calculateDynamic(s, rec(), day(2026, 11, 3, 8));
  assert.strictEqual(plan.totalReminders, 6);
  assert.strictEqual(Math.round(plan.intervalMin), 63);
  assert.strictEqual(clock(plan.nextReminder), '09:34');
  assert.strictEqual(plan.suggestedMl, 0);
  const during = day(2026, 11, 3, 9);
  assert.strictEqual(calc.statusLine(s, rec(), plan, during, true), '下次 09:34 · 370 ml');
  assert.strictEqual(calc.statusLine(s, rec(), plan, during, false), '下次提醒 09:34 · 建议 370 ml');
});

test('evening plan and quiet-hours fallback', function () {
  const s = calc.defaultSettings();
  const day0 = day(2026, 11, 3);
  const six = rec([0, 1, 2, 3, 4, 5].map(function (i) {
    return { at: new Date(day0.getTime() + (9 * 60 + i * 50) * 60000), amountMl: 370, source: 'cup' };
  }));
  assert.strictEqual(calc.consumedMl(six), 2220);
  const during = calc.calculateDynamic(s, six, day(2026, 11, 3, 16));
  assert.strictEqual(during.totalReminders, 0);
  assert.strictEqual(during.nextReminder, null);
  assert.strictEqual(calc.statusLine(s, six, during, day(2026, 11, 3, 16), false), '下班后再提醒');

  const evening = calc.calculateDynamic(s, rec([{ at: day(2026, 11, 3, 9), amountMl: 2220 }]), day(2026, 11, 3, 17, 30));
  assert.strictEqual(evening.totalReminders, 1);
  assert.strictEqual(clock(evening.nextReminder), '21:10');
  assert.strictEqual(evening.suggestedMl, 0);

  const whole = settings({ quietStart: '17:30' });
  assert.strictEqual(calc.calculateDynamic(whole, rec(), day(2026, 11, 3, 8)).totalReminders, 9);
});

test('drink and correction move the next reminder', function () {
  const drunk = calc.calculateDynamic(mine(), rec([ev(11, 0, 370)]), T(11, 0));
  assert.strictEqual(clock(drunk.nextReminder), '13:56');
  const corrected = calc.calculateDynamic(mine(), rec([ev(11, 0, 1110)]), T(11, 0));
  assert.strictEqual(clock(corrected.nextReminder), '15:50');
  const changed = settings(mine());
  changed.dailyGoalMl = 1900;
  const moved = calc.calculateDynamic(changed, rec([ev(10, 0, 370)]), T(10, 30));
  assert.strictEqual(clock(moved.nextReminder), '13:54');
});

test('phases around lunch and cutoff', function () {
  const s = mine();
  const d = day(2026, 11, 3);
  assert.strictEqual(calc.phaseAt(s, new Date(d.getTime() + 8 * 3600000)), 'BeforeWork');
  assert.strictEqual(calc.phaseAt(s, new Date(d.getTime() + 11.99 * 3600000)), 'Working');
  assert.strictEqual(calc.phaseAt(s, new Date(d.getTime() + 12 * 3600000)), 'Lunch');
  assert.strictEqual(calc.phaseAt(s, new Date(d.getTime() + 13.5 * 3600000)), 'Working');
  assert.strictEqual(calc.phaseAt(s, new Date(d.getTime() + 17.5 * 3600000)), 'NoDrinkWindow');
  assert.strictEqual(calc.phaseAt(s, new Date(d.getTime() + 18 * 3600000)), 'AfterWork');
  const slots = calc.schedule(s, d);
  const at17 = new Date(d.getTime() + 17 * 3600000);
  const current = slots.filter(function (slot) { return slot.at <= at17; }).pop();
  assert.strictEqual(clock(current.at), '16:55');
});

test('suggestion never exceeds the small remainder', function () {
  const now = day(2026, 11, 2, 17, 20);
  const s = settings({
    dailyGoalMl: 500,
    cupCapacityMl: 300,
    workCupCapacityMl: 300,
    homeCupCapacityMl: 50,
    workStart: '09:00',
    workEnd: '18:00'
  });
  const record = rec([{ at: new Date(now.getTime() - 50 * 60000), amountMl: 400 }]);
  const plan = calc.calculateDynamic(s, record, now);
  assert.strictEqual(plan.suggestedMl, 50);
});

test('missed reminders ask for one cup, and a finished goal stops', function () {
  const missed = calc.calculateDynamic(desktopDefault(), rec(), day(2026, 11, 2, 14, 30));
  assert.strictEqual(missed.suggestedMl, 300);
  assert.ok(missed.nextReminder <= day(2026, 11, 2, 14, 30));
  const done = calc.calculateDynamic(desktopDefault(), rec([{ at: day(2026, 11, 2, 16), amountMl: 2000 }]), day(2026, 11, 2, 17));
  assert.strictEqual(done.suggestedMl, 0);
  const early = calc.calculateDynamic(desktopDefault(), rec(), day(2026, 11, 2, 8));
  assert.strictEqual(early.dueMl, 0);
  assert.strictEqual(early.suggestedMl, 0);
  assert.ok(early.nextReminder > day(2026, 11, 2, 8));
});

test('next reminder stays out of lunch, before cutoff, and after the gap', function () {
  for (let consumed = 0; consumed < 2220; consumed += 185) {
    for (let min = 8 * 60; min < 17 * 60 + 30; min += 7) {
      const now = new Date(T(0, 0).getTime() + min * 60000);
      const record = rec([{ at: new Date(now.getTime() - 60000), amountMl: Math.max(1, consumed) }]);
      const next = calc.calculateDynamic(mine(), record, now).nextReminder;
      if (!next) continue;
      const label = clock(now) + '/' + consumed + ' -> ' + clock(next);
      assert.ok(!(next >= T(12, 0) && next < T(13, 30)), label);
      assert.ok(next < T(17, 30), label);
      assert.ok(next >= new Date(now.getTime() + 19 * 60000), label);
    }
  }
});

test('totals, adjustments, and parsing', function () {
  assert.strictEqual(calc.consumedMl(rec([{ amountMl: 300 }, { amountMl: -500 }])), 0);
  assert.strictEqual(calc.adjustmentFor(rec([{ amountMl: 370 }]), 1000), 630);
  assert.strictEqual(calc.adjustmentFor(rec([{ amountMl: 370 }, { amountMl: 300 }]), 500), -170);
  assert.strictEqual(calc.adjustmentFor(rec([]), 800), 800);
  assert.strictEqual(calc.tryParseTotal(' 1,200 ').total, 1200);
  assert.strictEqual(calc.tryParseTotal('800ml').total, 800);
  assert.strictEqual(calc.tryParseTotal('20001').ok, false);
  assert.strictEqual(calc.tryParseTotal('12.5').ok, false);
  assert.strictEqual(calc.tryParseTotal('-5').ok, false);
});

test('normalize keeps an older single cup and the product defaults', function () {
  const legacy = calc.normalizeSettings({ dailyGoalMl: 2220, cupCapacityMl: 370 });
  assert.strictEqual(legacy.workCupCapacityMl, 370);
  assert.strictEqual(legacy.lunchStart, '11:50');
  assert.strictEqual(legacy.noDrinkBeforeEndMinutes, 30);
  assert.strictEqual(legacy.quietStart, '23:00');
  const fresh = calc.normalizeSettings({});
  assert.strictEqual(fresh.dailyGoalMl, 3000);
  assert.strictEqual(fresh.homeCupCapacityMl, 800);
  assert.deepStrictEqual(fresh.activeDays, [true, true, true, true, true, false, false]);
});

if (process.exitCode) {
  console.error('calculator tests failed');
} else {
  console.log('ok ' + passed);
}