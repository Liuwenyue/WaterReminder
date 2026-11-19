
'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const storage = new Map();
const requests = [];
global.wx = {
  getStorageSync: key => (storage.has(key) ? structuredClone(storage.get(key)) : undefined),
  setStorageSync: (key, value) => storage.set(key, structuredClone(value)),
  removeStorageSync: key => storage.delete(key),
  request(options) { requests.push(options); options.fail({ errMsg: 'request:fail' }); }
};
const store = require('../miniprogram/utils/storage.js');
const server = require('../miniprogram/utils/server.js');
const calc = require('../miniprogram/utils/calculator.js');

test('a connected drink stays queued until sync and is posted once with the same id', async () => {
  server.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'member-token', memberName: '甲' });
  const added = store.addEvent(300, 'cup');
  assert.equal(added.ok, true);
  assert.equal(added.pending, true);
  assert.equal(requests.length, 0);
  assert.equal(calc.consumedMl(store.snapshot(new Date()).model), 300);
  const id = store.load().queue[0].id;
  wx.request = options => {
    requests.push(options);
    if (String(options.url).includes('/api/sync/snapshot')) {
      options.success({ statusCode: 200, data: { status: { goalMl: 2000, remindersPaused: false }, days: [], memberName: '甲', memberId: 'member-1' } });
      return;
    }
    const operation = options.data.operations[0];
    assert.equal(operation.id, id);
    assert.equal(operation.amountMl, 300);
    options.success({ statusCode: 200, data: { results: [{ id, duplicate: false, changed: true }], days: [{ date: operation.date, consumedMl: 300, goalMl: 2000, goalKnown: true, events: [{ id, at: operation.date + 'T09:30:00', amountMl: 300, source: 'cup' }] }], status: { goalMl: 2000, remindersPaused: false } } });
  };
  const synced = await server.syncLedger();
  assert.equal(synced.ok, true);
  assert.equal(store.load().queue.length, 0);
  assert.equal(calc.consumedMl(store.snapshot(new Date()).model), 300);
  assert.equal(server.loadConnection().memberName, '甲');
});
