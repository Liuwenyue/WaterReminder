'use strict';

const server = require('./server.js');
const store = require('./storage.js');

const PENDING_KEY = 'water-reminder-wechat-pending';
const TEMPLATE_KEY = 'water-reminder-wechat-template';

function remember(id) {
  const key = TEMPLATE_KEY + store.accountKey();
  if (id) wx.setStorageSync(key, id);
  else if (typeof wx.removeStorageSync === 'function') wx.removeStorageSync(key);
}

function describe(status) {
  if (!status || !status.enabled) return '微信提醒暂不可用，请检查服务器连接和微信模板';
  if (!status.bound) return '微信尚未绑定，请重新打开设置重试';
  return status.credits ? '微信提醒剩余 ' + status.credits + ' 次' : '微信提醒次数已用完';
}

function capture() {
  const settings = store.load().settings;
  const account = store.accountKey();
  if (!server.loadConnection().configured || !settings.mobilePushEnabled || settings.phoneMode === 'PushWard') return Promise.resolve({ status: 'skip' });
  const id = wx.getStorageSync(TEMPLATE_KEY + account);
  if (!id || typeof wx.requestSubscribeMessage !== 'function') return Promise.resolve({ status: 'skip' });
  return new Promise(function (resolve) {
    wx.requestSubscribeMessage({
      tmplIds: [id],
      success: function (res) {
        const value = res && res[id];
        const status = value === 'accept' || value === 'reject' || value === 'ban' || value === 'filter' ? value : 'fail';
        resolve({ status: status, account, id: operationId() });
      },
      fail: function () { resolve({ status: 'fail' }); }
    });
  });
}

function operationId() {
  return 'wx' + Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
}

function pending(account) {
  const value = wx.getStorageSync(PENDING_KEY + account);
  return Array.isArray(value) ? value.filter(function (item) {
    return item && typeof item.id === 'string' && item.status === 'accept';
  }) : [];
}

function savePending(account, items) {
  wx.setStorageSync(PENDING_KEY + account, items);
}

const sending = new Map();
function flush(account) {
  if (sending.has(account)) return sending.get(account);
  if (!pending(account).length || account !== store.accountKey()) return Promise.resolve();
  const task = drain();
  sending.set(account, task);
  return task;

  async function drain() {
    let result;
    try {
      while (account === store.accountKey()) {
        const current = pending(account)[0];
        if (!current) break;
        result = await server.reportSubscription(current);
        if (!result.ok) break;
        savePending(account, pending(account).filter(item => item.id !== current.id));
      }
      return result;
    } finally {
      sending.delete(account);
    }
  }
}

function report(result) {
  if (!result || result.status !== 'accept') return Promise.resolve();
  const connection = server.loadConnection();
  const current = store.accountKey();
  const account = connection.configured && result.account === store.legacyAccountKey(connection)
    ? current : result.account || current;
  if (!account) return Promise.resolve();
  const items = pending(account);
  const id = result.id || operationId();
  if (!items.some(item => item.id === id)) items.push({ id, status: 'accept' });
  savePending(account, items);
  return flush(account);
}

function bind() {
  const connection = server.loadConnection();
  if (!connection.configured || typeof wx.login !== 'function') return Promise.resolve();
  return new Promise(function (resolve) {
    wx.login({
      success: function (res) {
        const current = server.loadConnection();
        if (!res || !res.code || current.serverUrl !== connection.serverUrl || current.apiToken !== connection.apiToken) return resolve();
        server.bindWeChat(res.code).then(resolve);
      },
      fail: function () { resolve(); }
    });
  });
}

function prepare() {
  const account = store.accountKey();
  const settings = store.load().settings;
  const disabled = { enabled: false, templateId: '', bound: false, credits: 0 };
  if (!server.loadConnection().configured || !settings.mobilePushEnabled || settings.phoneMode === 'PushWard') return Promise.resolve(disabled);
  const legacyKey = store.legacyAccountKey(server.loadConnection());
  if (account !== legacyKey && pending(legacyKey).length) {
    const items = pending(account);
    const known = new Set(items.map(item => item.id));
    savePending(account, items.concat(pending(legacyKey).filter(item => !known.has(item.id))));
    savePending(legacyKey, []);
  }
  const oldPending = wx.getStorageSync(PENDING_KEY);
  const oldBinding = wx.getStorageSync('water-reminder-wechat-bound');
  if (Array.isArray(oldPending) && oldBinding && oldBinding.token === server.loadConnection().apiToken) {
    savePending(account, pending(account).concat(oldPending.filter(item => item && item.status === 'accept')));
    wx.removeStorageSync(PENDING_KEY);
    wx.removeStorageSync('water-reminder-wechat-bound');
  }
  return server.wechatStatus().then(function (result) {
    if (store.accountKey() !== account) return disabled;
    const data = result.ok && result.data && typeof result.data === 'object' ? result.data : {};
    if (result.ok) remember(typeof data.templateId === 'string' ? data.templateId : '');
    const status = {
      enabled: !!data.enabled,
      templateId: data.templateId || '',
      bound: !!data.bound,
      credits: Number.isInteger(data.credits) ? data.credits : 0
    };
    const next = status.enabled && !status.bound ? bind() : Promise.resolve();
    return next.then(function (binding) {
      if (binding && binding.ok) status.bound = !!binding.data.bound;
      return flush(account);
    }).then(function (report) {
      if (report && report.ok && Number.isInteger(report.data && report.data.credits)) status.credits = report.data.credits;
      return status;
    });
  });
}

module.exports = {
  remember: remember,
  describe: describe,
  capture: capture,
  report: report,
  prepare: prepare,
  operationId: operationId
};
