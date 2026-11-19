
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

test('bootstrap drinks survive restart and lost receipts with stable IDs; older drinks stay local', async () => {
  storage.clear();
  store.addEvent(150, 'cup');
  let releaseLogin;
  wx.login = options => options.success({ code: 'login-code-ok' });
  wx.request = options => { releaseLogin = options; };
  try {
    const initial = server.signIn();
    store.addEvent(250, 'cup');
    const events = Object.values(store.load().records).flatMap(day => day.events);
    const id = events.find(event => event.amountMl === 250).id;
    releaseLogin.fail();
    assert.equal((await initial).ok, false);

    const path = require.resolve('../miniprogram/utils/server.js');
    delete require.cache[path];
    const restarted = require(path);
    const date = store.shanghaiDate(new Date());
    const status = { goalMl: 2000 };
    let sent = 0;
    wx.request = options => {
      if (options.url.endsWith('/api/wechat/login')) {
        return options.success({ statusCode: 200, data: { memberId: 'new-member', memberName: '新成员',
          accessCode: 'wr_' + 'ed'.repeat(32) } });
      }
      if (options.method === 'GET') return options.success({ statusCode: 200, data: { status, days: [] } });
      const [op] = options.data.operations;
      assert.equal(options.data.operations.length, 1);
      assert.equal(op.id, id);
      assert.equal(op.amountMl, 250);
      sent++;
      if (sent === 1) return options.fail();
      options.success({ statusCode: 200, data: {
        status, days: [{ date, events: [{ id, at: op.at, amountMl: 250 }] }],
        results: [{ id, duplicate: true }]
      } });
    };
    assert.equal((await restarted.signIn()).ok, true);
    assert.equal(store.legacyCount(), 1);
    assert.equal((await restarted.syncLedger()).ok, false);
    assert.equal(store.load().queue[0].id, id);
    assert.equal((await restarted.syncLedger({ operationsOnly: true })).ok, true);
    assert.equal((await restarted.syncLedger({ operationsOnly: true })).ok, true);
    assert.equal(sent, 2);
    assert.equal(store.load().queue.length, 0);
    assert.equal(calc.consumedMl(store.snapshot(new Date()).model), 250);
  } finally { delete wx.login; }
});

test('bootstrap blocks ambiguous total edits while delete and clear only remove local records', async () => {
  storage.clear();
  const now = new Date();
  const tomorrow = new Date(now);
  tomorrow.setDate(now.getDate() + 1);
  store.addEvent(100, 'cup', now);
  let complete;
  wx.login = options => options.success({ code: 'login-code-ok' });
  wx.request = options => { complete = options; };
  try {
    const signup = server.signIn();
    store.addEvent(300, 'cup', now);
    store.addEvent(50, 'cup', now);
    store.addEvent(200, 'cup', tomorrow);
    const date = calc.dateKey(now);
    const before = store.load();
    assert.match(store.setTotal(now, 200).error, /等账本建好后再校正/);
    assert.deepEqual(store.load(), before);
    const pending = before.records[date].events.find(event => event.amountMl === 300);
    const removed = before.records[date].events.find(event => event.amountMl === 50);
    store.deleteEvent(now, removed.id);
    store.clearDay(tomorrow);

    complete.success({ statusCode: 200, data: {
      memberId: 'personal', memberName: '个人账本', accessCode: 'wr_' + 'ad'.repeat(32)
    } });
    assert.equal((await signup).ok, true);
    assert.deepEqual(store.load().queue.map(op => [op.kind, op.id, op.amountMl]), [['drink', pending.id, 300]]);
    assert.equal(store.legacyCount(), 1);
  } finally { delete wx.login; }
});

test('legacy share query still boots normally, and a reconnect refresh follows an in-flight receipt', async () => {
  storage.clear();
  let app;
  let reconnect;
  global.App = definition => { app = definition; };
  wx.onNetworkStatusChange = callback => { reconnect = callback; };
  wx.login = options => options.success({ code: 'login-code-ok' });
  let snapshots = 0;
  let release;
  const status = { goalMl: 2000 };
  wx.request = options => {
    if (options.url.endsWith('/api/wechat/login')) {
      assert.equal(options.data.shareToken, null);
      return options.success({ statusCode: 200, data: { memberId: 'personal', memberName: '个人账本',
        accessCode: 'wr_' + 'fe'.repeat(32) } });
    }
    if (options.url.endsWith('/api/settings')) return options.success({ statusCode: 200, data: {} });
    if (options.method === 'GET') {
      snapshots++;
      return options.success({ statusCode: 200, data: { status, days: [] } });
    }
    const [operation] = options.data.operations;
    release = () => options.success({ statusCode: 200, data: {
      status, days: [{ date: operation.date, events: [] }], results: [{ id: operation.id }]
    } });
  };
  try {
    require('../miniprogram/app.js');
    const options = { query: { share: 'ws_' + 'aa'.repeat(24) } };
    assert.equal((await app.onLaunch(options)).ok, true);
    assert.equal(app.globalData.pendingShare, undefined);
    assert.equal(snapshots, 1);
    store.addEvent(250, 'cup');
    const operation = app.syncChanges();
    assert.equal(snapshots, 1);
    reconnect({ isConnected: true });
    assert.equal(snapshots, 1);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(snapshots, 1);
    release();
    assert.equal((await operation).ok, true);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(snapshots, 2);
    assert.equal((await app.onShow(options)).ok, true);
    assert.equal(snapshots, 3);
  } finally {
    delete wx.login;
    delete wx.onNetworkStatusChange;
    delete global.App;
  }
});
