'use strict';

const assert = require('node:assert/strict');
const { test, beforeEach } = require('node:test');
const calc = require('../miniprogram/utils/calculator.js');
const store = require('../miniprogram/utils/storage.js');
const journal = require('../miniprogram/utils/journal.js');

const storage = new Map();
let app;
let definition;
let modal;
let tabVisible;
global.App = options => { app = options; };
global.getApp = () => app;
global.Page = global.Component = options => { definition = options; };
let requestHandler = options => options.fail({ errMsg: 'request:fail' });
global.wx = {
  getStorageSync: key => structuredClone(storage.get(key)),
  setStorageSync: (key, value) => storage.set(key, structuredClone(value)),
  removeStorageSync: key => storage.delete(key),
  hideTabBar: () => { tabVisible = false; },
  showTabBar: () => { tabVisible = true; },
  showModal: options => { modal = options; },
  switchTab() {},
  showToast() {},
  request: options => requestHandler(options)
};
require('../miniprogram/app.js');

function loadDefinition(path) {
  require('../miniprogram/' + path);
  return definition;
}
const todayPage = loadDefinition('pages/today/today.js');
const recordsPage = loadDefinition('pages/records/records.js');
const settingsPage = loadDefinition('pages/settings/settings.js');
const sheet = loadDefinition('components/amount-sheet/index.js');

function mount(options, extraData) {
  return Object.assign({}, options, options.methods, {
    data: Object.assign(structuredClone(options.data), extraData),
    setData(patch, complete) {
      Object.assign(this.data, patch);
      if (complete) complete();
    }
  });
}
function control(key, value) {
  return { currentTarget: { dataset: { key } }, detail: { value } };
}
beforeEach(() => {
  storage.clear();
  requestHandler = options => options.fail({ errMsg: 'request:fail' });
  app.globalData.prompting = app.globalData.amountEditorOpen = false;
  app.globalData.openTodayRecords = false;
  tabVisible = true;
  modal = null;
  delete wx.login;
  delete wx.scanCode;
  delete wx.pageScrollTo;
});

test('one tap updates the amount, progress and recent entry; a rapid second tap is ignored', async () => {
  const today = mount(todayPage);
  today.onShow();
  today.drinkCup();
  today.drinkCup();
  assert.equal(today.data.recent.length, 1);
  assert.equal(today.data.consumedText, store.formatNumber(today.data.cupAmount));
  assert.equal(today.data.fraction, today.data.cupAmount / store.load().settings.dailyGoalMl);
  today.togglePause();
  assert.equal(today.data.paused, true);
  assert.equal(today.data.hasNext, false);
  await new Promise(resolve => setTimeout(resolve, 550));
  today.togglePause();
  assert.equal(today.data.paused, false);
  today.onHide();
  assert.equal(today.data.feedback, '');
});

test('first opening asks for a name before creating a personal ledger', async () => {
  wx.login = options => options.success({ code: 'login-code-ok' });
  requestHandler = options => {
    if (String(options.url).endsWith('/api/wechat/login')) {
      assert.equal(options.data.shareToken, null);
      if (!options.data.name) {
        options.success({ statusCode: 409, data: { error: '请输入你的名字。', registrationRequired: true } });
        return;
      }
      assert.equal(options.data.name, '小林');
      options.success({ statusCode: 200, data: {
        accessCode: 'wr_' + 'ab'.repeat(32),
        memberId: '11111111-1111-1111-1111-111111111111',
        memberName: '小林',
        sharedWithOwner: true
      }});
      return;
    }
    options.success({ statusCode: 200, data: {} });
  };
  const today = mount(todayPage);
  today.onShow();
  await new Promise(resolve => setImmediate(resolve));
  const api = require('../miniprogram/utils/server.js');
  assert.equal(api.loadConnection().configured, false);
  assert.equal(today.data.registrationRequired, true);
  today.updateName({ detail: { value: ' 小林 ' } });
  await today.register();
  assert.equal(api.loadConnection().configured, true);
  assert.equal(api.loadConnection().memberName, '小林');
  assert.equal(api.loadConnection().serverUrl, 'https://wednesday-holly-warcraft-beef.trycloudflare.com');
  const settings = mount(settingsPage);
  assert.equal(settings.data.apiToken, undefined);
  delete wx.login;
});


test('settings has one save action and keeps pairing and legacy invitations separate', () => {
  const template = require('node:fs').readFileSync(
    require('node:path').join(__dirname, '../miniprogram/pages/settings/settings.wxml'), 'utf8');
  assert.equal([...template.matchAll(/bindtap="save"/g)].length, 1);
  assert.match(template, /bindtap="scanInvite"/);
  assert.match(template, /bindtap="confirmInvitation"/);
  assert.match(template, /关联我的电脑账本/);
  assert.match(template, /bindtap="createOwnerPair"/);
  assert.match(template, /wx:if="{{inviteOpen}}" class="surface invite-card"/);
  assert.match(template, /disabled="{{inviteBusy}}" bindtap="cancelInvitation"/);
  assert.match(template, /当前设置收不到手机提醒/);
  assert.equal(mount(settingsPage).data.inviteOpen, false);
  assert.doesNotMatch(template, /apiToken|open-type="share"|bindtap="connect"/);
});

test('custom entry validates, persists once and releases the reminder and tab bar on dismissal', () => {
  const today = mount(todayPage);
  today.onShow();
  today.customAmount();
  const editor = mount(sheet, { actionId: today.data.actionId, initialValue: '', minimum: 1, maximum: 5000 });
  today.selectComponent = () => editor;
  editor.triggerEvent = (name, detail) => {
    if (name === 'confirm') today.addCustom({ detail });
  };
  sheet.lifetimes.attached.call(editor);
  assert.equal(tabVisible, false);
  assert.equal(app.globalData.amountEditorOpen, true);
  editor.onInput({ detail: { value: '5001' } });
  editor.confirm();
  assert.ok(editor.data.error);
  store.setTotal(new Date(), 19900);
  editor.pick({ currentTarget: { dataset: { amount: 250 } } });
  editor.confirm();
  assert.match(editor.data.error, /20,000/);
  assert.equal(editor.data.closing, false);
  editor.onInput({ detail: { value: '100' } });
  editor.confirm();
  editor.confirm();
  assert.equal(editor.data.closing, true);
  assert.equal(today.data.consumedText, '20,000');
  assert.equal(today.data.recent[0].glyph, 'measure');
  sheet.lifetimes.detached.call(editor);
  today.closeAmount({ detail: { actionId: today.data.actionId } });
  today.onHide();
  assert.equal(tabVisible, true);
  assert.equal(app.globalData.amountEditorOpen, false);
});

test('chart selection and total corrections target the selected day, including the oldest visible day', () => {
  const now = new Date();
  const older = journal.dayAtOffset(now, 30);
  store.addEvent(370, 'cup', now);
  store.addEvent(250, 'custom', older);
  const records = mount(recordsPage);
  records.onShow();
  records.pickDay({ currentTarget: { dataset: { offset: 5 } } });
  assert.equal(records.data.bars.find(bar => bar.selected).offset, 5);
  records.selectDay(30);
  records.prev();
  assert.equal(records.data.canPrev, false);
  assert.deepEqual(records.data.bars.map(bar => bar.offset), [30, 29, 28, 27, 26, 25, 24]);
  assert.equal(records.data.total, 250);
  records.adjust();
  records.selectComponent = () => ({ dismiss() {} });
  records.saveTotal({ detail: { actionId: records.data.actionId, amount: 0 } });
  assert.equal(records.data.total, 0);
  assert.equal(records.data.empty, false);
  assert.ok(records.data.events.some(entry => entry.negative));
  records.clearDay();
  modal.success({ confirm: false });
  modal.complete();
  assert.equal(records.data.empty, false);
  records.clearDay();
  modal.success({ confirm: true });
  modal.complete();
  assert.equal(records.data.empty, true);
  mount(todayPage).goRecords();
  records.onShow();
  assert.equal(records.data.total, 370);
  assert.equal(records.data.canNext, false);
  assert.equal(app.globalData.openTodayRecords, false);
});

test('account changes invalidate open record editors and delayed confirmations', () => {
  const api = require('../miniprogram/utils/server.js');
  const connect = (memberId, memberName, apiToken) => api.saveConnection({
    serverUrl: 'https://cup.example', apiToken, memberId, memberName, loginMethod: 'wechat'
  });
  const day = store.shanghaiDate(new Date());
  const seed = (amountMl, id) => store.applyRemote({
    days: [{ date: day, events: [{ id, amountMl, at: day + 'T09:00:00' }] }], status: { goalMl: 2000 }
  }, []);
  connect('old', '原账本', 'old-session');
  seed(350, 'old-event');
  const oldKey = store.accountKey();
  const records = mount(recordsPage);
  const today = mount(todayPage);
  records.onShow();
  today.onShow();
  records.adjust();
  const correction = { detail: { amount: 500, actionId: records.data.actionId } };
  today.customAmount();
  const custom = { detail: { amount: 200, actionId: today.data.actionId } };
  records.remove({ currentTarget: { dataset: { id: 'old-event' } } });
  const deletePrompt = modal;
  records.clearDay();
  const clearPrompt = modal;
  assert.match(clearPrompt.content, new RegExp('原账本 · ' + day));
  connect('owner', '管理员', 'owner-session');
  seed(270, 'owner-event');
  records.reload();
  today.reload();
  assert.equal(records.data.ledgerName, '管理员');
  assert.equal(records.data.amountOpen, false);
  assert.equal(today.data.amountOpen, false);
  records.saveTotal(correction);
  today.addCustom(custom);
  for (const prompt of [deletePrompt, clearPrompt]) {
    prompt.success({ confirm: true });
    prompt.complete();
  }
  assert.equal(store.load().queue.length, 0);
  assert.equal(wx.getStorageSync(oldKey).queue.length, 0);
  assert.equal(records.data.total, 270);

  records.adjust();
  records.selectComponent = () => ({ dismiss() {} });
  connect('owner', '管理员', 'renewed-session');
  records.reload();
  assert.equal(records.data.amountOpen, true);
  records.saveTotal({ detail: { amount: 300, actionId: records.data.actionId } });
  assert.equal(store.load().queue[0].kind, 'setTotal');
  assert.equal(store.load().queue[0].totalMl, 300);
  today.onHide();
});

test('pair codes copy only digits, expire on return, and stale requests cannot update a new ledger', async () => {
  const api = require('../miniprogram/utils/server.js');
  const settings = mount(settingsPage, { pairOpen: true });
  const originalNow = Date.now;
  const originalPair = api.createOwnerPair;
  let now = originalNow(), copied, respond;
  Date.now = () => now;
  wx.setClipboardData = options => { copied = options.data; };
  api.createOwnerPair = async () => ({ ok: true, data: { code: '12345678', expiresAt: new Date(now + 5000).toISOString() } });
  try {
    await settings.createOwnerPair();
    assert.equal(settings.data.pairTime, '0:05');
    settings.copyOwnerPair();
    assert.equal(copied, '12345678');
    settings.onHide();
    now += 5001;
    settings._hidden = false;
    settings.reload(false);
    assert.equal(settings.data.pairCode, '');
    assert.equal(settings.data.pairExpired, true);
    copied = undefined;
    settings.copyOwnerPair();
    assert.equal(copied, undefined);
    api.createOwnerPair = () => new Promise(resolve => { respond = resolve; });
    const pending = settings.createOwnerPair();
    api.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'another', memberId: 'another' });
    settings.reload(false);
    respond({ ok: true, data: { code: '87654321', expiresAt: new Date(now + 5000).toISOString() } });
    await pending;
    assert.equal(settings.data.pairCode, '');
    assert.equal(settings.data.pairBusy, '');
    const leaving = settings.createOwnerPair();
    settings.onHide();
    await settings.createOwnerPair();
    assert.equal(settings.data.pairBusy, 'code');
    respond({ ok: true, data: { code: '87654321', expiresAt: new Date(now + 5000).toISOString() } });
    await leaving;
    assert.equal(settings.data.pairCode, '');
    assert.equal(settings.data.pairBusy, '');
  } finally {
    settings.onHide();
    Date.now = originalNow;
    api.createOwnerPair = originalPair;
    delete wx.setClipboardData;
  }
});

test('owner pairing shows the current ledger and retries record sync separately', async () => {
  const api = require('../miniprogram/utils/server.js');
  const originalOpen = api.openOwnerLedger;
  const originalSignIn = app.signIn;
  const owner = '00000000-0000-0000-0000-000000000000';
  function connect(memberId, loginMethod) {
    api.saveConnection({ serverUrl: 'https://cup.example', apiToken: memberId, memberId, loginMethod });
  }
  const settings = mount(settingsPage, { pairOpen: true });
  settings.loadFromServer = async () => true;
  api.openOwnerLedger = async () => { connect(owner, 'wechat'); return { ok: true }; };
  app.signIn = async () => ({ ok: false, error: '模拟网络故障' });
  try {
    await settings.openOwnerLedger();
    assert.equal(settings.data.ownerLinked, true);
    assert.equal(settings.data.ownerSyncText, '记录尚未同步');
    assert.equal(settings.data.ownerNeedsSync, true);
    app.signIn = async () => {
      store.applyRemote({ days: [], status: { goalMl: 2000 } }, []);
      return { ok: true };
    };
    await settings.retryOwnerSync();
    assert.equal(settings.data.ownerSyncText, '记录已同步');
    assert.equal(settings.data.ownerNeedsSync, false);
    const reopened = mount(settingsPage);
    reopened.reload(false);
    assert.equal(reopened.data.ownerLinked, true);
    connect('ordinary', 'wechat');
    settings.reload(false);
    assert.equal(settings.data.ownerLedger, false);
    assert.equal(settings.data.ownerSyncText, '');
  } finally {
    api.openOwnerLedger = originalOpen;
    app.signIn = originalSignIn;
  }
});

function serverSettings(extra) {
  return Object.assign({
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
    pauseUntil: '2026-11-18T23:59:00'
  }, extra);
}

test('settings read and write the server configuration without sending the token', async () => {
  const serverApi = require('../miniprogram/utils/server.js');
  const requests = [];
  let remote = serverSettings();
  requestHandler = options => {
    if (options.url.endsWith('/api/wechat')) return options.fail();
    requests.push({
      url: options.url,
      method: options.method || 'GET',
      data: options.data,
      authorization: options.header && options.header.Authorization
    });
    if ((options.method || 'GET') === 'GET') options.success({ statusCode: 200, data: remote });
    else {
      remote = Object.assign({}, remote, options.data);
      options.success({ statusCode: 200, data: remote });
    }
  };
  assert.equal(serverApi.saveConnection({ serverUrl: 'https://cup.example.com/api/settings', apiToken: 'desktop-api-token' }).serverUrl, 'https://cup.example.com');
  const settings = mount(settingsPage);
  assert.equal(await settings.onShow(), true);
  assert.equal(settings.data.loaded, true);
  assert.equal(settings.data.dailyGoalMl, '2500');
  assert.equal(settings.data.mobilePushEnabled, true);
  assert.equal(settings.data.serverPaused, true);
  assert.equal(requests[0].url, 'https://cup.example.com/api/settings');
  assert.equal(requests[0].authorization, 'Bearer desktop-api-token');
  assert.equal(store.load().settings.dailyGoalMl, 2500);
  assert.equal(store.load().settings.remindersPaused, true);

  settings.updateField(control('dailyGoalMl', '0'));
  const scrolls = [];
  wx.pageScrollTo = options => scrolls.push(options.selector);
  assert.equal(await settings.save(), false);
  assert.match(settings.data.fieldErrors.dailyGoalMl, /100–10000/);
  assert.deepEqual(scrolls, ['#field-dailyGoalMl']);
  assert.equal(requests.filter(item => item.method === 'PUT').length, 0);
  assert.equal(remote.dailyGoalMl, 2500);

  settings.updateField(control('dailyGoalMl', '1800'));
  settings.onShow();
  assert.equal(settings.data.dailyGoalMl, '1800');
  settings.updateField(control('lunchEnabled', false));
  settings.updateField(control('mobilePushEnabled', false));
  settings.changePhoneMode({ detail: { value: 3 } });
  settings.data.activeDays.slice().forEach((active, index) => {
    if (active) settings.onDay({ currentTarget: { dataset: { index } } });
  });
  assert.equal(settings.data.dayCount, 0);
  assert.match(settings.data.preview, /未选择提醒日/);
  assert.equal(await settings.save(), false);
  assert.match(settings.data.fieldErrors.activeDays, /至少选择一天/);
  settings.onDay({ currentTarget: { dataset: { index: 6 } } });
  assert.equal(settings.data.dayCount, 1);
  assert.match(settings.data.preview, /首次约在/);
  assert.equal(await settings.save(), true);
  assert.equal(settings.data.dirty, false);
  assert.equal(settings.data.apiToken, undefined);
  const put = requests.at(-1);
  assert.equal(put.method, 'PUT');
  assert.equal(put.authorization, 'Bearer desktop-api-token');
  assert.deepEqual(Object.keys(put.data).sort(), [
    'activeDays', 'dailyGoalMl', 'homeCupMl', 'lunchEnabled', 'lunchEnd', 'lunchStart',
    'minReminderGapMinutes', 'mobilePushEnabled', 'noDrinkBeforeEndMinutes', 'pauseUntil',
    'phoneMode', 'preservePause', 'quietStart', 'remindersPaused', 'workCupMl', 'workEnd', 'workStart'
  ]);
  assert.equal(JSON.stringify(put.data).includes('desktop-api-token'), false);
  assert.equal(put.data.dailyGoalMl, 1800);
  assert.equal(put.data.workCupMl, 370);
  assert.equal(put.data.lunchEnabled, false);
  assert.equal(put.data.mobilePushEnabled, false);
  assert.equal(put.data.phoneMode, 'Both');
  assert.equal(put.data.preservePause, true);
  assert.equal(put.data.remindersPaused, true);
  assert.equal(put.data.pauseUntil, '2026-11-18T23:59:00');
  assert.deepEqual(put.data.activeDays, [false, false, false, false, false, false, true]);
  assert.equal(store.load().settings.dailyGoalMl, 1800);
  assert.equal(store.load().settings.remindersPaused, true);
  assert.equal(JSON.stringify(wx.getStorageSync('water-reminder-v1') || {}).includes('desktop-api-token'), false);
  assert.equal(JSON.stringify(store.load()).includes('desktop-api-token'), false);

  assert.equal(serverApi.loadConnection().configured, true);
});

test('a rejected server save keeps the draft and the previous server values', async () => {
  const serverApi = require('../miniprogram/utils/server.js');
  const remote = serverSettings();
  requestHandler = options => {
    if ((options.method || 'GET') === 'GET') options.success({ statusCode: 200, data: remote });
    else options.success({ statusCode: 400, data: { error: '每日目标需为 100–10000 ml 的整数。' } });
  };
  serverApi.saveConnection({ serverUrl: 'https://cup.example.com', apiToken: 'desktop-api-token' });
  const settings = mount(settingsPage);
  await settings.onShow();
  settings.updateField(control('dailyGoalMl', '1800'));
  assert.equal(await settings.save(), false);
  assert.equal(settings.data.dirty, true);
  assert.equal(settings.data.dailyGoalMl, '1800');
  assert.match(settings.data.error, /100–10000/);
  assert.equal(store.load().settings.dailyGoalMl, 2500);
  assert.equal(remote.dailyGoalMl, 2500);
});

test('an in-flight PUT keeps new edits and advances the draft revision', async () => {
  const api = require('../miniprogram/utils/server.js');
  api.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'one', memberId: 'member' });
  const page = mount(settingsPage);
  const writes = [];
  requestHandler = options => {
    if (options.url.endsWith('/api/wechat')) return options.fail();
    if (options.method === 'PUT') { writes.push(options); return; }
    options.success({ statusCode: 200, data: serverSettings({ revision: 'r1' }) });
  };
  await page.onShow();
  page.updateField(control('dailyGoalMl', '1800'));
  const first = page.save();
  page.updateField(control('dailyGoalMl', '1900'));
  assert.equal(await page.save(), false);
  assert.equal(writes.length, 1);
  writes[0].success({ statusCode: 200, data: serverSettings({ dailyGoalMl: 1800, workCupMl: 400, revision: 'r2' }) });
  assert.equal(await first, true);
  assert.equal(page.data.dailyGoalMl, '1900');
  assert.equal(page.data.workCupCapacityMl, '400');
  assert.equal(page.data.dirty, true);
  const second = page.save();
  assert.equal(writes[1].data.expectedRevision, 'r2');
  assert.equal(writes[1].data.dailyGoalMl, 1900);
  writes[1].success({ statusCode: 200, data: serverSettings({ dailyGoalMl: 1900, revision: 'r3' }) });
  assert.equal(await second, true);
  assert.equal(page.data.dirty, false);
});

test('settings 401 uses force renewal and keeps an expired invite ledger and queue', async () => {
  const api = require('../miniprogram/utils/server.js');
  api.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'one', memberId: 'member', loginMethod: 'invite' });
  store.applyRemote({ status: { goalMl: 2000 } }, []);
  const page = mount(settingsPage);
  let denied = false;
  let requests = 0;
  requestHandler = options => {
    if (options.url.endsWith('/api/wechat')) return options.fail();
    assert.ok(options.url.endsWith('/api/settings'));
    requests++;
    options.success({ statusCode: denied ? 401 : 200, data: serverSettings() });
  };
  assert.equal(await page.onShow(), true);
  assert.equal(page.data.syncText, '');
  assert.equal((await api.signIn()).ok, true);
  denied = true;
  store.addEvent(100, 'cup');
  const original = store.load().queue;
  const before = requests;
  assert.equal(await page.onShow(), false);
  assert.equal(requests - before, 2);
  assert.match(page.data.connectionText, /重新领取邀请/);
  assert.match(page.data.syncText, /待同步/);
  assert.equal(api.loadConnection().memberId, 'member');
  assert.deepEqual(store.load().queue, original);
});

test('desktop invite scans and pastes through preview, and only confirms the current queue-safe target', async () => {
  const api = require('../miniprogram/utils/server.js');
  const url = 'https://cup.example';
  api.saveConnection({ serverUrl: url, apiToken: 'original', memberId: 'old' });
  const page = mount(settingsPage);
  const pasted = 'wi_' + 'a'.repeat(64);
  let member = 'new';
  let expiresAt;
  let redeem;
  requestHandler = options => {
    if (options.url.endsWith('/preview')) {
      return options.success({ statusCode: 200, data: { member: { id: member, name: '成员 ' + member }, serverUrl: url, expiresAt } });
    }
    if (options.url.endsWith('/redeem')) { redeem = options; return; }
    options.fail();
  };
  page.toggleInvitation();
  assert.equal(page.data.inviteOpen, true);
  page.updateInvite(control('', pasted));
  expiresAt = '2000-01-01T00:00:00Z';
  assert.equal(await page.previewInvitation(), true);
  assert.equal(await page.confirmInvitation(), false);
  assert.match(page.data.inviteError, /过期/);
  assert.equal(redeem, undefined);

  expiresAt = undefined;
  assert.equal(await page.previewInvitation(), true);
  store.addEvent(200, 'cup');
  assert.equal(await page.confirmInvitation(), false);
  assert.match(page.data.inviteError, /待同步/);
  assert.equal(redeem, undefined);
  const queue = store.load().queue;
  member = 'old';
  wx.scanCode = options => options.success({ result: 'waterreminder|' + url + '|' + pasted });
  page.scanInvite();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(page.data.invitePreview.memberName, '成员 old');
  page.toggleInvitation();
  assert.equal(page.data.invitePreview, null);
  assert.equal(await page.confirmInvitation(), false);
  page.toggleInvitation();
  assert.equal(await page.previewInvitation(), true);
  const redeemed = page.confirmInvitation();
  page.cancelInvitation();
  page.toggleInvitation();
  assert.equal(page.data.inviteBusy, true);
  page.onHide();
  redeem.success({ statusCode: 200, data: { member: { id: 'old', name: '成员 old' }, accessCode: 'renewed' } });
  assert.equal(await redeemed, true);
  assert.equal(page.data.inviteOpen, false);
  assert.equal(api.loadConnection().apiToken, 'renewed');
  assert.deepEqual(store.load().queue, queue);
});

test('reminder capabilities distinguish unknown, unconfigured, zero credits and permission to send', async () => {
  const api = require('../miniprogram/utils/server.js');
  api.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'one', memberId: 'member' });
  const page = mount(settingsPage);
  let failStatus = true;
  let credits = 0;
  let remote = serverSettings({ mobilePushEnabled: false });
  requestHandler = options => {
    if (options.url.endsWith('/api/wechat')) {
      if (failStatus) return options.fail();
      return options.success({ statusCode: 200, data: { enabled: true, bound: true, templateId: 'template', credits } });
    }
    if (options.method === 'PUT') remote = { ...remote, ...options.data };
    options.success({ statusCode: 200, data: remote });
  };
  await page.onShow();
  await page.refreshWeChat();
  assert.equal(page.data.pushWardState, 'unknown');
  assert.equal(page.data.wechatState, 'unknown');
  failStatus = false;
  page.updateField(control('mobilePushEnabled', true));
  page.changePhoneMode({ detail: { value: 2 } });
  await page.refreshWeChat();
  assert.equal(page.data.wechatReady, true);
  assert.equal(page.data.wechatState, 'unavailable');
  assert.match(page.data.wechatText, /0 次，可授权/);
  remote.pushWardConfigured = false;
  assert.equal(await page.save(), true);
  assert.equal(page.data.pushWardState, 'unavailable');
  page.changePhoneMode({ detail: { value: 1 } });
  assert.equal(await page.save(), true);
  credits = 2;
  remote.pushWardConfigured = true;
  await page.onShow();
  await page.refreshWeChat();
  assert.equal(page.data.wechatState, 'ready');
  assert.equal(page.data.pushWardState, 'ready');
});

test('discard confirmation shows the count and cannot clear another account', () => {
  const api = require('../miniprogram/utils/server.js');
  const connection = { serverUrl: 'https://cup.example', apiToken: 'first', memberId: 'one' };
  api.saveConnection(connection);
  store.addEvent(100, 'cup');
  store.pauseToday(new Date());
  store.syncFailure('冲突', true);
  const page = mount(settingsPage);
  page.dropPending();
  assert.match(modal.content, /2 项/);
  modal.success({ confirm: false });
  assert.equal(store.syncInfo().pending, 2);
  page.dropPending();
  api.saveConnection({ ...connection, memberId: 'two', apiToken: 'second' });
  store.addEvent(200, 'cup');
  modal.success({ confirm: true });
  assert.equal(store.syncInfo().pending, 1);
  api.saveConnection(connection);
  assert.equal(store.syncInfo().pending, 2);
});

test('a cup tap sends a ledger operation while the today page remains open', async () => {
  const api = require('../miniprogram/utils/server.js');
  api.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'member', memberId: 'one' });
  const today = mount(todayPage);
  const status = { goalMl: 2000, remindersPaused: false };
  global.getCurrentPages = () => [today];
  const posts = [];
  requestHandler = options => {
    if (options.method === 'GET') return options.success({ statusCode: 200, data: { status, days: [] } });
    const [op] = options.data.operations;
    posts.push(op);
    options.success({ statusCode: 200, data: { status, results: [{ id: op.id }],
      days: [{ date: op.date, events: [{ id: op.id, at: op.at, amountMl: op.amountMl, source: op.source }] }] } });
  };
  today.reload();
  today.drinkCup();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(posts.length, 1);
  assert.equal(store.syncInfo().pending, 0);
  assert.equal(today.data.recent.length, 1);
  today.onHide();
  delete global.getCurrentPages;
});
