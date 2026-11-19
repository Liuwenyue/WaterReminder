'use strict';

const server = require('./server.js');

const BOUND_KEY = 'water-reminder-wechat-bound';
const PENDING_KEY = 'water-reminder-wechat-pending';
const TEMPLATE_KEY = 'water-reminder-wechat-template';
let templateId = '';
try {
  const saved = wx.getStorageSync(TEMPLATE_KEY);
  if (typeof saved === 'string') templateId = saved;
} catch (error) {
  templateId = '';
}

function remember(id) {
  templateId = typeof id === 'string' ? id : '';
  if (templateId) wx.setStorageSync(TEMPLATE_KEY, templateId);
  else if (typeof wx.removeStorageSync === 'function') wx.removeStorageSync(TEMPLATE_KEY);
}

function describe(status) {
  if (!status || !status.enabled) return '服务器还没配好微信提醒。配好之前，手机仍用原来的推送。';
  if (!status.bound) return '还没绑定微信。点下面允许一次，会弹出授权。';
  if (!status.credits) return '已绑定，可发送次数用完了。再点一次喝水就会静默加一条。';
  return '已绑定，还能发送 ' + status.credits + ' 次。点喝水或改水量会静默加次数。';
}

function capture() {
  const id = templateId;
  if (!id || typeof wx.requestSubscribeMessage !== 'function') return Promise.resolve({ status: 'skip' });
  return new Promise(function (resolve) {
    wx.requestSubscribeMessage({
      tmplIds: [id],
      success: function (res) {
        const value = res && res[id];
        const status = value === 'accept' || value === 'reject' || value === 'ban' || value === 'filter' ? value : 'fail';
        resolve({ status: status });
      },
      fail: function () { resolve({ status: 'fail' }); }
    });
  });
}

function operationId() {
  return 'wx' + Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
}

function pending() {
  const value = wx.getStorageSync(PENDING_KEY);
  return Array.isArray(value) ? value.filter(function (item) {
    return item && typeof item.id === 'string' && item.status === 'accept';
  }) : [];
}

function savePending(items) {
  wx.setStorageSync(PENDING_KEY, items.slice(0, 30));
}

function flush() {
  const items = pending();
  if (!items.length || !server.loadConnection().configured) return Promise.resolve();
  const current = items[0];
  return server.reportSubscription(current).then(function (result) {
    if (!result.ok) return result;
    savePending(pending().filter(function (item) { return item.id !== current.id; }));
    return pending().length ? flush() : result;
  });
}

function report(result) {
  if (!result || result.status !== 'accept') return Promise.resolve();
  const items = pending();
  items.push({ id: operationId(), status: 'accept' });
  savePending(items);
  return flush();
}

function bind() {
  const connection = server.loadConnection();
  if (!connection.configured || typeof wx.login !== 'function') return Promise.resolve();
  const marker = wx.getStorageSync(BOUND_KEY) || {};
  if (marker.token === connection.apiToken && marker.bound) return Promise.resolve();
  return new Promise(function (resolve) {
    wx.login({
      success: function (res) {
        if (!res || !res.code) return resolve();
        server.bindWeChat(res.code).then(function (result) {
          if (result.ok && result.data && result.data.bound) {
            wx.setStorageSync(BOUND_KEY, { token: connection.apiToken, bound: true });
          }
          resolve(result);
        });
      },
      fail: function () { resolve(); }
    });
  });
}

function prepare() {
  if (!server.loadConnection().configured) return Promise.resolve({ enabled: false, templateId: '', bound: false, credits: 0 });
  return server.wechatStatus().then(function (result) {
    const data = result.ok && result.data && typeof result.data === 'object' ? result.data : {};
    if (result.ok) remember(typeof data.templateId === 'string' ? data.templateId : '');
    const status = {
      enabled: !!data.enabled,
      templateId: templateId,
      bound: !!data.bound,
      credits: Number.isInteger(data.credits) ? data.credits : 0
    };
    if (status.bound) {
      const connection = server.loadConnection();
      wx.setStorageSync(BOUND_KEY, { token: connection.apiToken, bound: true });
    }
    const next = status.enabled && !status.bound ? bind() : Promise.resolve();
    return next.then(flush).then(function () { return status; });
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
