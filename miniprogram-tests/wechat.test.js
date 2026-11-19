'use strict';

const assert = require('node:assert/strict');
const { test, beforeEach } = require('node:test');

const storage = new Map();
const calls = [];
global.wx = {
  getStorageSync: key => (storage.has(key) ? structuredClone(storage.get(key)) : undefined),
  setStorageSync: (key, value) => storage.set(key, structuredClone(value)),
  removeStorageSync: key => storage.delete(key),
  request(options) { options.fail({ errMsg: 'request:fail' }); },
  requestSubscribeMessage(options) { calls.push(options.tmplIds); options.success({ [options.tmplIds[0]]: 'accept' }); },
  login(options) { options.success({ code: 'login-code-1' }); }
};
const wechat = require('../miniprogram/utils/wechat.js');
const server = require('../miniprogram/utils/server.js');

beforeEach(() => {
  storage.clear();
  calls.length = 0;
  wechat.remember('');
});

test('subscribe stays quiet until a template is known, then one tap adds one credit report', async () => {
  assert.equal((await wechat.capture()).status, 'skip');
  wechat.remember('template-id-0123456789');
  const result = await wechat.capture();
  assert.equal(result.status, 'accept');
  assert.deepEqual(calls, [['template-id-0123456789']]);
  assert.match(wechat.describe({ enabled: true, bound: true, credits: 0 }), /用完/);
  assert.match(wechat.describe({ enabled: true, bound: false, credits: 0 }), /弹出授权/);
  assert.match(wechat.describe({ enabled: false }), /原来的推送/);
});

test('an accepted tap reports once and a retry keeps the same id', async () => {
  const posted = [];
  wx.request = options => {
    posted.push({ url: options.url, data: options.data });
    options.success({ statusCode: 200, data: { enabled: true, bound: true, credits: posted.length, templateId: 'template-id-0123456789' } });
  };
  server.saveConnection({ serverUrl: 'https://cup.example.com', apiToken: 'desktop-api-token' });
  wechat.remember('template-id-0123456789');
  await wechat.report({ status: 'accept' });
  await wechat.report({ status: 'reject' });
  const subscription = posted.filter(item => item.url.endsWith('/api/wechat/subscription'));
  assert.equal(subscription.length, 1);
  assert.equal(subscription[0].data.status, 'accept');
  assert.match(subscription[0].data.id, /^wx/);
  assert.equal(JSON.stringify(subscription).includes('desktop-api-token'), false);
});
