'use strict';

const SPAN = 366;
const DOW = ['一', '二', '三', '四', '五', '六', '日'];

function stamp(date) {
  return Date.UTC(date.getFullYear(), date.getMonth(), date.getDate());
}

function diff(today, day) {
  return Math.round((stamp(today) - stamp(day)) / 86400000);
}

function startOfWeek(date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate() - ((date.getDay() + 6) % 7));
}

function weekDays(today, selected) {
  const start = startOfWeek(selected);
  return Array.from({ length: 7 }, function (_, index) {
    const day = new Date(start.getFullYear(), start.getMonth(), start.getDate() + index);
    return { day: day, offset: diff(today, day) };
  });
}

function monthCells(today, selected) {
  const first = new Date(selected.getFullYear(), selected.getMonth(), 1);
  const lead = (first.getDay() + 6) % 7;
  const count = new Date(selected.getFullYear(), selected.getMonth() + 1, 0).getDate();
  const cells = [];
  for (let index = 0; index < lead; index++) cells.push({ blank: true, key: 'blank-' + selected.getMonth() + '-' + index });
  for (let day = 1; day <= count; day++) {
    const date = new Date(selected.getFullYear(), selected.getMonth(), day);
    cells.push({
      blank: false,
      key: date.getFullYear() + '-' + (date.getMonth() + 1) + '-' + day,
      day: date,
      offset: diff(today, date),
      label: String(day)
    });
  }
  return cells;
}

function shiftMonth(today, selected, direction, span) {
  const target = new Date(selected.getFullYear(), selected.getMonth() + direction, 1);
  const last = new Date(target.getFullYear(), target.getMonth() + 1, 0).getDate();
  const date = new Date(target.getFullYear(), target.getMonth(), Math.min(selected.getDate(), last));
  return Math.max(0, Math.min(span, diff(today, date)));
}

module.exports = { SPAN, DOW, diff, startOfWeek, weekDays, monthCells, shiftMonth };
