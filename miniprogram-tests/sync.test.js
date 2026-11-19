
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

test('a single sync drains multiple batches including edits made during the first POST', async () => {
  storage.clear();
  server.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'batch', memberId: 'one' });
  const date = store.shanghaiDate(new Date());
  for (let index = 0; index < 101; index++) store.addEvent(1, 'cup');
  const batchSizes = [];
  const status = { goalMl: 2000, remindersPaused: false };
  let release;
  const events = [];
  wx.request = options => {
    if (options.method === 'GET') return options.success({ statusCode: 200, data: { status, days: [] } });
    const ops = options.data.operations;
    batchSizes.push(ops.length);
    events.push(...ops.map(op => ({ id: op.id, at: op.at, amountMl: op.amountMl, source: op.source })));
    const finish = () => options.success({ statusCode: 200, data: {
      status, results: ops.map(op => ({ id: op.id })), days: [{ date, events: events.slice() }]
    } });
    if (batchSizes.length === 1) release = finish;
    else finish();
  };
  const task = server.syncLedger();
  await new Promise(resolve => setImmediate(resolve));
  store.addEvent(1, 'custom');
  assert.equal(server.syncLedger(), task);
  release();
  assert.equal((await task).ok, true);
  assert.deepEqual(batchSizes, [100, 2]);
  assert.equal(store.load().queue.length, 0);
  assert.equal(store.load().records[date].events.length, 102);
  wx.request = options => options.success({ statusCode: 200, data: { status, days: [{ date, events: [] }] } });
  await server.syncLedger();
  assert.equal(store.load().records[date].events.length, 0);
});

test('discarding a blocked import requires the same queue and retry preserves original identities', () => {
  storage.clear();
  const original = store.uuid();
  const when = new Date();
  store.addEvent(100, 'cup', when);
  const localKey = [...storage.keys()][0];
  const local = storage.get(localKey);
  local.records[calc.dateKey(when)].events[0].id = original;
  store.addEvent(120, 'custom', when);
  server.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'import', memberId: 'one' });
  store.applyRemote({ status: { goalMl: 2000 } }, []);
  assert.equal(store.importLegacy().ok, true);
  const first = store.load().queue;
  assert.equal(first[0].eventId, original);
  assert.equal(store.importLegacy().ok, false);
  store.syncFailure('conflict', true);
  assert.equal(store.dropQueue([]), false);
  assert.equal(store.dropQueue(first.map(op => op.id)), true);
  assert.equal(store.importLegacy().ok, true);
  assert.deepEqual(store.load().queue, first);
});

test('a connected drink stays queued until sync and is posted once with the same id', async () => {
  storage.clear();
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
