'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const view = require('../miniprogram/utils/history-view.js');

test('week starts on Monday and contains the selected day', () => {
  const selected = new Date(2026, 10, 24);
  const today = new Date(2026, 10, 24);
  const days = view.weekDays(today, selected);
  assert.equal(days.length, 7);
  assert.equal(days[0].day.getDay(), 1);
  assert.equal(days[6].day.getDay(), 0);
  assert.equal(days.find(day => day.offset === 0).day.getDate(), 24);
});

test('month leaves blanks before the first Monday and marks future days', () => {
  const selected = new Date(2026, 10, 24);
  const today = new Date(2026, 10, 24);
  const cells = view.monthCells(today, selected);
  assert.equal(cells[0].blank, true);
  const days = cells.filter(cell => !cell.blank);
  assert.equal(days.length, 30);
  assert.equal(days[0].label, '1');
  assert.equal(days[0].day.getDay(), 0);
  assert.equal(days.find(cell => cell.label === '25').offset, -1);
});

test('month paging keeps the same day number and never enters the future', () => {
  const today = new Date(2026, 10, 24);
  const selected = new Date(2026, 10, 24);
  assert.equal(view.shiftMonth(today, selected, 1, view.SPAN), 0);
  const previous = view.shiftMonth(today, selected, -1, view.SPAN);
  assert.equal(previous, view.diff(today, new Date(2026, 9, 24)));
});
