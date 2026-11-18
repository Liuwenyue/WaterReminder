'use strict';

const calc = require('./calculator.js');
const store = require('./storage.js');

const WEEKDAYS = ['星期日', '星期一', '星期二', '星期三', '星期四', '星期五', '星期六'];
const GLYPHS = { cup: 'cup', reminder: 'bell', adjust: 'adjust', custom: 'measure' };

function monthDay(date) {
  return (date.getMonth() + 1) + '月' + date.getDate() + '日';
}

function dateLabel(date) {
  return monthDay(date) + ' · ' + WEEKDAYS[date.getDay()];
}

function dayAtOffset(today, offset) {
  return new Date(today.getFullYear(), today.getMonth(), today.getDate() - offset);
}

function entries(model) {
  return model.events.slice().reverse().sort((first, second) => second.at - first.at).map(event => ({
    id: event.id,
    time: calc.formatClock(event.at),
    amount: (event.amountMl > 0 ? '+' : '') + store.formatNumber(event.amountMl),
    negative: event.amountMl < 0,
    glyph: GLYPHS[event.source] || 'measure',
    label: store.sourceLabel(event.source)
  }));
}

module.exports = { monthDay, dateLabel, dayAtOffset, entries };
