'use strict';

const assert = require('node:assert/strict');
const { test, beforeEach } = require('node:test');

const storage = new Map();
global.wx = {
  getStorageSync: key => (storage.has(key) ? structuredClone(storage.get(key)) : undefined),
  setStorageSync: (key, value) => storage.set(key, structuredClone(value)),
  removeStorageSync: key => storage.delete(key),
  request(options) { options.fail({ errMsg: 'request:fail' }); }
};
const server = require('../miniprogram/utils/server.js');
const store = require('../miniprogram/utils/storage.js');
const config = require('../miniprogram/utils/config.js');

const sample = () => ({
  dailyGoalMl: 2500,
  workCupMl: 370,
  homeCupMl: 800,
  workStart: '08:30',
  workEnd: '17:30',
  lunchEnabled: true,
  lunchStart: '11:50',
  lunchEnd: '13:30',
  noDrinkBeforeEndMinutes: 30,
  quietStart: '23:00',
  minReminderGapMinutes: 20,
  mobilePushEnabled: true,
  activeDays: [true, true, true, true, true, false, false],
  remindersPaused: true,
  pauseUntil: '2026-11-19T18:00:00'
});

beforeEach(() => storage.clear());

test('connection keeps the token local and rejects addresses that would expose it', () => {
  assert.match(server.validateConnection('cup.example.com', 'token').error, /http/);
  assert.match(server.validateConnection('https://user:secret@cup.example.com', 'token').error, /令牌/);
  assert.match(server.validateConnection('https://cup.example.com/api/settings?token=secret', 'token').error, /参数/);
  const saved = server.saveConnection({ serverUrl: 'http://127.0.0.1:8787/api/settings/', apiToken: ' desktop-api-token ' });
  assert.equal(saved.error, '');
  assert.equal(saved.serverUrl, 'http://127.0.0.1:8787');
  assert.equal(server.loadConnection().apiToken, 'desktop-api-token');
  assert.match(server.connectionLabel(saved.serverUrl), /https/);
  server.clearConnection();
  assert.equal(server.loadConnection().configured, false);
});

test('phone connection validation does not depend on a browser URL global', () => {
  const previous = global.URL;
  try {
    global.URL = undefined;
    const valid = server.validateConnection('https://wednesday-holly-warcraft-beef.trycloudflare.com', 'test-token');
    assert.equal(valid.error, '');
    assert.equal(valid.serverUrl, 'https://wednesday-holly-warcraft-beef.trycloudflare.com');
    assert.match(server.connectionLabel(valid.serverUrl), /已连接/);
    assert.ok(server.validateConnection('https://example.com\\@evil.com', 'token').error);
    assert.ok(server.validateConnection('https://example.com:99999', 'token').error);
  } finally {
    global.URL = previous;
  }
});

test('validation matches the server rules and echoes pause without a secret', () => {
  const form = {
    dailyGoalMl: '1800',
    workCupCapacityMl: '370',
    homeCupCapacityMl: '800',
    workStart: '08:30',
    workEnd: '17:30',
    lunchEnabled: true,
    lunchStart: '11:50',
    lunchEnd: '13:30',
    noDrinkBeforeEndMinutes: '30',
    minReminderGapMinutes: '20',
    quietStart: '23:00',
    mobilePushEnabled: true,
    activeDays: [true, true, true, true, true, false, false],
    serverPaused: true,
    serverPauseUntil: '2026-11-19T18:00:00'
  };
  const ok = server.validateSettings(form);
  assert.equal(ok.error, '');
  assert.equal(ok.update.workCupMl, 370);
  assert.equal(ok.update.remindersPaused, true);
  assert.equal(JSON.stringify(ok.update).includes('token'), false);
  assert.match(server.validateSettings(Object.assign({}, form, { dailyGoalMl: '50' })).error, /100–10000/);
  assert.match(server.validateSettings(Object.assign({}, form, { quietStart: '17:30' })).error, /晚于下班/);
  assert.match(server.validateSettings(Object.assign({}, form, { activeDays: [false, false, false, false, false, false, false] })).error, /至少选择一天/);
});

test('refresh caches server settings and ignores a stale response after a save', async () => {
  let releaseGet;
  const remote = sample();
  wx.request = options => {
    if ((options.method || 'GET') === 'GET') {
      releaseGet = () => options.success({ statusCode: 200, data: remote });
      return;
    }
    options.success({ statusCode: 200, data: Object.assign({}, remote, options.data, { dailyGoalMl: 1800 }) });
  };
  server.saveConnection({ serverUrl: 'https://cup.example.com', apiToken: 'desktop-api-token' });
  const refreshing = server.refreshCachedSettings();
  const saved = await server.saveSettings(Object.assign(server.validateSettings({
    dailyGoalMl: '1800',
    workCupCapacityMl: '370',
    homeCupCapacityMl: '800',
    workStart: '08:30',
    workEnd: '17:30',
    lunchEnabled: false,
    lunchStart: '11:50',
    lunchEnd: '13:30',
    noDrinkBeforeEndMinutes: '30',
    minReminderGapMinutes: '20',
    quietStart: '23:00',
    mobilePushEnabled: false,
    activeDays: [false, false, false, false, false, false, true],
    serverPaused: true,
    serverPauseUntil: remote.pauseUntil
  }).update));
  assert.equal(saved.ok, true);
  releaseGet();
  assert.equal(await refreshing, false);
  assert.equal(store.load().settings.dailyGoalMl, 1800);
  assert.equal(store.load().settings.lunchEnabled, false);
  assert.equal(store.load().settings.remindersPaused, true);
  assert.equal(JSON.stringify(wx.getStorageSync('water-reminder-v1') || {}).includes('desktop-api-token'), false);
  assert.equal(JSON.stringify(store.load()).includes('desktop-api-token'), false);
});

test('network and auth failures stay on the form and do not invent settings', async () => {
  server.saveConnection({ serverUrl: 'https://cup.example.com', apiToken: 'desktop-api-token' });
  wx.request = options => options.fail({ errMsg: 'request:fail' });
  const offline = await server.getSettings();
  assert.equal(offline.ok, false);
  assert.match(offline.error, /连不上服务器/);
  wx.request = options => options.success({ statusCode: 401, data: {} });
  const denied = await server.getSettings();
  assert.match(denied.error, /令牌不正确/);
  assert.equal(store.load().settings.dailyGoalMl, store.load().settings.dailyGoalMl);
});

test('sign-in falls back to a device ledger when WeChat login is unavailable', async () => {
  wx.login = options => options.success({ code: 'login-code-ok' });
  wx.request = options => {
    assert.equal(Object.hasOwn(options.data, 'expectedMemberId'), false);
    if (String(options.url).endsWith('/api/wechat/login')) {
      options.success({ statusCode: 503, data: {} });
      return;
    }
    assert.equal(options.url, 'https://wednesday-holly-warcraft-beef.trycloudflare.com/api/device/login');
    assert.match(options.data.deviceId, /^[0-9a-f]{64}$/);
    options.success({ statusCode: 200, data: {
      accessCode: 'wr_' + 'cd'.repeat(32),
      memberId: '22222222-2222-2222-2222-222222222222',
      memberName: '成员device',
      sharedWithOwner: false
    }});
  };
  const result = await server.signIn();
  assert.equal(result.ok, true);
  assert.equal(server.loadConnection().memberName, '成员device');
  assert.equal(server.loadConnection().sharedWithOwner, false);
  delete wx.login;
});

test('settings distinguish unknown PushWard capability and report all invalid form fields', () => {
  assert.equal(server.fromResponse(sample()).pushWardConfigured, undefined);
  for (const value of [true, false]) {
    assert.equal(server.fromResponse({ ...sample(), pushWardConfigured: value }).pushWardConfigured, value);
  }
  const result = server.validateSettings({
    ...server.toLocal(server.fromResponse(sample())),
    dailyGoalMl: 50, workCupCapacityMl: 0, homeCupCapacityMl: 5000,
    workStart: 'abc', lunchEnd: 'abc', quietStart: 'abc'
  });
  assert.deepEqual(Object.keys(result.fieldErrors), [
    'dailyGoalMl', 'workCupCapacityMl', 'homeCupCapacityMl', 'workStart', 'lunchEnd', 'quietStart'
  ]);
  assert.ok(result.error);
  assert.equal(result.update, undefined);
});

test('503, 404 and offline cold starts retain the bound ledger and queue', async () => {
  server.saveConnection({ serverUrl: config.serverUrl, apiToken: 'wr_' + 'ab'.repeat(32), memberId: 'original' });
  store.addEvent(250, 'cup');
  const connection = server.loadConnection();
  const state = store.load();
  for (const status of [503, 404, undefined]) {
    wx.request = options => {
      assert.equal(options.url, config.serverUrl + '/api/settings');
      if (status) options.success({ statusCode: status, data: {} });
      else options.fail();
    };
    assert.equal((await server.signIn()).ok, false);
    assert.deepEqual(server.loadConnection(), connection);
    assert.deepEqual(store.load(), state);
  }
});

test('forced renewal rejects another member and retains the queue for the matching member', async () => {
  const original = 'wr_' + '11'.repeat(32);
  const renewed = 'wr_' + '22'.repeat(32);
  server.saveConnection({ serverUrl: config.serverUrl, apiToken: original, memberId: 'original', loginMethod: 'wechat' });
  store.addEvent(250, 'cup');
  const queue = store.load().queue;
  const key = store.accountKey();
  let status = 200;
  let memberId = 'different';
  let requests = 0;
  wx.login = options => options.success({ code: 'login-code-ok' });
  wx.request = options => {
    requests++;
    if (options.method === 'GET') return options.success({ statusCode: status, data: sample() });
    assert.equal(options.url, config.serverUrl + '/api/wechat/login');
    assert.equal(options.data.expectedMemberId, 'original');
    options.success({ statusCode: 200, data: { accessCode: renewed, memberId, memberName: '成员' } });
  };
  try {
    assert.equal((await server.signIn()).ok, true);
    status = 401;
    assert.equal((await server.signIn()).ok, true);
    assert.equal(requests, 1);
    assert.equal((await server.signIn({ force: true })).ok, false);
    assert.equal(server.loadConnection().apiToken, original);
    memberId = 'original';
    status = 403;
    assert.equal((await server.signIn({ force: true })).ok, true);
    assert.equal(server.loadConnection().apiToken, renewed);
    assert.equal(store.accountKey(), key);
    assert.deepEqual(store.load().queue, queue);
  } finally { delete wx.login; }
});

for (const loginMethod of ['device', '']) {
  test(`renewal specifies the original member for ${loginMethod ? 'known device' : 'legacy'} credentials`, async () => {
    const original = 'wr_' + '33'.repeat(32);
    const renewed = 'wr_' + '44'.repeat(32);
    const deviceId = '55'.repeat(32);
    wx.setStorageSync('water-device-id', deviceId);
    server.saveConnection({ serverUrl: config.serverUrl, apiToken: original, memberId: 'original', loginMethod });
    const methods = [];
    wx.login = options => options.success({ code: 'login-code-ok' });
    wx.request = options => {
      if (options.method === 'GET') return options.success({ statusCode: 401, data: {} });
      assert.equal(options.data.expectedMemberId, 'original');
      if (options.url.endsWith('/api/wechat/login')) {
        methods.push('wechat');
        return options.success({ statusCode: 403, data: {} });
      }
      assert.ok(options.url.endsWith('/api/device/login'));
      assert.equal(options.data.deviceId, deviceId);
      methods.push('device');
      options.success({ statusCode: 200, data: { accessCode: renewed, memberId: 'original', memberName: '成员' } });
    };
    try {
      assert.equal((await server.signIn({ force: true })).ok, true);
      assert.deepEqual(methods, loginMethod ? ['device'] : ['wechat', 'device']);
      assert.equal(server.loadConnection().apiToken, renewed);
      assert.equal(server.loadConnection().loginMethod, 'device');
    } finally { delete wx.login; }
  });
}

test('trusted default upgrade keeps the storage namespace, receipts and subscriptions', async () => {
  const { serverUrl: previous, previousDefaultUrls } = config;
  const next = 'https://official.example';
  server.saveConnection({ serverUrl: previous, apiToken: 'session', memberId: 'one' });
  store.addEvent(100, 'cup');
  const key = store.accountKey();
  const queue = store.load().queue;
  const pendingKey = 'water-reminder-wechat-pending' + key;
  const pending = [{ id: 'subscription-id', status: 'accept' }];
  wx.setStorageSync(pendingKey, pending);
  const day = store.shanghaiDate(new Date());
  store.applyRemote({ days: [{ date: day, events: [{ id: 'existing-id', at: day + 'T01:02:03', amountMl: 30 }] }] }, []);
  try {
    config.previousDefaultUrls = [previous];
    config.serverUrl = '';
    wx.request = options => options.success({ statusCode: 200, data: sample() });
    await server.signIn();
    assert.equal(server.loadConnection().serverUrl, previous);
    config.serverUrl = next;
    wx.request = options => {
      assert.equal(options.url, next + '/api/settings');
      options.success({ statusCode: 503, data: {} });
    };
    assert.equal((await server.signIn()).ok, false);
    assert.equal(server.loadConnection().serverUrl, next);
    assert.equal(store.accountKey(), key);
    assert.deepEqual(store.load().queue, queue);
    assert.equal(store.load().records[day].events[0].id, 'existing-id');
    assert.deepEqual(wx.getStorageSync(pendingKey), pending);

    server.saveConnection({ serverUrl: 'https://custom.example', apiToken: 'custom' });
    wx.request = options => {
      assert.equal(options.url, 'https://custom.example/api/settings');
      options.success({ statusCode: 200, data: sample() });
    };
    assert.equal((await server.signIn()).ok, true);
  } finally {
    config.serverUrl = previous;
    config.previousDefaultUrls = previousDefaultUrls;
  }
});

test('desktop invites preserve the current ledger during preview and redemption, and do not renew through login', async () => {
  const base = config.serverUrl;
  server.saveConnection({ serverUrl: base, apiToken: 'original', memberId: 'original' });
  store.addEvent(100, 'cup');
  const original = store.load().queue;
  let redeem;
  wx.request = options => {
    if (options.url.endsWith('/preview')) return options.success({ statusCode: 200, data: { member: { name: '受邀成员' } } });
    if (options.url.endsWith('/redeem')) { redeem = options; return; }
    assert.equal(options.url, base + '/api/settings');
    options.success({ statusCode: 401, data: {} });
  };
  const first = await server.previewInvite(base, 'first');
  const failed = server.redeemInvite(base, 'first', first.previewId);
  assert.deepEqual(store.load().queue, original);
  redeem.success({ statusCode: 400, data: {} });
  assert.equal((await failed).ok, false);
  assert.deepEqual(store.load().queue, original);

  const preview = await server.previewInvite(base, 'second');
  const late = server.redeemInvite(base, 'second', preview.previewId);
  const invited = { accessCode: 'wr_' + 'aa'.repeat(32), member: { id: 'invited', name: '受邀成员' } };
  server.saveConnection({ serverUrl: base, apiToken: 'changed', memberId: 'changed' });
  redeem.success({ statusCode: 200, data: invited });
  assert.equal((await late).stale, true);
  assert.equal(server.loadConnection().memberId, 'changed');
  const latest = await server.previewInvite(base, 'third');
  const joined = server.redeemInvite(base, 'third', latest.previewId);
  redeem.success({ statusCode: 200, data: invited });
  assert.equal((await joined).ok, true);
  assert.equal(server.loadConnection().loginMethod, 'invite');
  const denied = await server.signIn({ force: true });
  assert.match(denied.error, /重新领取邀请/);
  assert.equal(server.loadConnection().memberId, 'invited');
});

test('a desktop invitation preempts automatic creation and receives no unassigned drinks', async () => {
  const base = config.serverUrl;
  let login;
  wx.login = options => options.success({ code: 'login-code-ok' });
  wx.request = options => {
    if (options.url.endsWith('/api/wechat/login')) { login = options; return; }
    if (options.url.endsWith('/preview')) return options.success({ statusCode: 200, data: {} });
    assert.ok(options.url.endsWith('/redeem'));
    options.success({ statusCode: 200, data: { accessCode: 'invited-token', member: { id: 'invited', name: '受邀成员' } } });
  };
  try {
    const signup = server.signIn();
    store.addEvent(150, 'cup');
    const preview = await server.previewInvite(base, 'invite');
    store.addEvent(200, 'cup');
    login.success({ statusCode: 200, data: { accessCode: 'wr_' + 'ab'.repeat(32), memberId: 'automatic', memberName: '个人账本' } });
    assert.equal((await signup).stale, true);
    assert.equal(server.loadConnection().configured, false);
    assert.equal((await server.redeemInvite(base, 'invite', preview.previewId)).ok, true);
    assert.equal(server.loadConnection().memberId, 'invited');
    assert.equal(store.load().queue.length, 0);
    assert.equal(store.legacyCount(), 2);
  } finally { delete wx.login; }
});

test('a legacy default connection requiring confirmation never triggers automatic login', async () => {
  wx.setStorageSync('water-reminder-server', { serverUrl: config.serverUrl, apiToken: 'unconfirmed' });
  wx.request = () => assert.fail('unconfirmed credentials must stay local');
  assert.match((await server.signIn({ force: true })).error, /确认服务器连接/);
  assert.equal(server.loadConnection().apiToken, 'unconfirmed');
  assert.equal(store.accountKey(), '');
});

test('a restarted bootstrap never sends credentials to a retired untrusted default', async () => {
  const retired = 'https://retired.example';
  for (const method of ['wechat', 'device']) {
    storage.clear();
    const attempt = store.beginBootstrap(retired, method);
    store.pauseBootstrap(false);
    store.addEvent(300, 'cup');
    const [day] = Object.values(store.load().records);
    assert.equal(day.events[0].bootstrapId, attempt.id);
    wx.login = () => assert.fail('must not obtain a WeChat code for an untrusted bootstrap');
    wx.request = () => assert.fail('must not contact the retired endpoint');
    try {
      const before = structuredClone(storage);
      const result = await server.signIn();
      assert.equal(result.ok, false);
      assert.match(result.error, /待归账饮水已保留/);
      assert.deepEqual(store.bootstrap(), attempt);
      assert.deepEqual(storage, before);
    } finally { delete wx.login; }
  }
});

test('redeeming a fresh invite after domain migration retains only the same member namespace', async () => {
  const { serverUrl: previous, previousDefaultUrls } = config;
  const next = 'https://official.example';
  const prefix = 'water-reminder-wechat-pending';
  const pending = { id: 'subscription', status: 'accept' };
  try {
    server.saveConnection({ serverUrl: previous, apiToken: 'original', memberId: 'one', loginMethod: 'invite' });
    const account = store.accountKey();
    store.addEvent(300, 'cup');
    const queue = store.load().queue;
    wx.setStorageSync(prefix + account, [pending]);
    config.serverUrl = next;
    config.previousDefaultUrls = [previous];
    wx.request = options => options.success({ statusCode: 200, data: sample() });
    assert.equal((await server.signIn()).ok, true);
    assert.equal(server.loadConnection().serverUrl, next);

    const tokenQueueKey = prefix + store.legacyAccountKey(server.loadConnection());
    const legacyPending = { id: 'older-token-subscription', status: 'accept' };
    wx.setStorageSync(tokenQueueKey, [legacyPending]);
    let memberId = 'one';
    wx.request = options => {
      assert.equal(options.url, next + '/api/invitations/redeem');
      options.success({ statusCode: 200, data: {
        serverUrl: previous, accessCode: 'replacement-' + memberId, member: { id: memberId, name: '成员' }
      } });
    };
    assert.equal((await server.redeemInvite(next, 'same-member')).ok, true);
    assert.equal(server.loadConnection().ledgerUrl, previous);
    assert.equal(server.loadConnection().serverUrl, next);
    assert.equal(store.accountKey(), account);
    assert.deepEqual(store.load().queue, queue);
    assert.deepEqual(wx.getStorageSync(prefix + account), [pending, legacyPending]);

    memberId = 'two';
    assert.equal((await server.redeemInvite(next, 'different-member')).ok, true);
    assert.equal(server.loadConnection().ledgerUrl, next);
    assert.notEqual(store.accountKey(), account);
    assert.deepEqual(store.load().queue, []);
    assert.equal(wx.getStorageSync(prefix + store.accountKey()), undefined);
    assert.deepEqual(wx.getStorageSync(prefix + account), [pending, legacyPending]);
  } finally {
    config.serverUrl = previous;
    config.previousDefaultUrls = previousDefaultUrls;
  }
});
