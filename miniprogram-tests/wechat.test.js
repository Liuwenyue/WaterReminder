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
  server.saveConnection({ serverUrl: 'https://cup.example.com', apiToken: 'test-token' });
  assert.equal((await wechat.capture()).status, 'skip');
  wechat.remember('template-id-0123456789');
  const result = await wechat.capture();
  assert.equal(result.status, 'accept');
  assert.deepEqual(calls, [['template-id-0123456789']]);
  assert.match(wechat.describe({ enabled: true, bound: true, credits: 0 }), /用完/);
  assert.match(wechat.describe({ enabled: true, bound: false, credits: 0 }), /尚未绑定/);
  assert.equal(wechat.describe({ enabled: true, bound: true, credits: 2 }), '微信提醒剩余 2 次');
  assert.match(wechat.describe({ enabled: false }), /微信模板/);
});

test('queued authorizations drain in order and stay with the originating account', async () => {
  server.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'one', memberId: 'one' });
  wechat.remember('template-one');
  const first = await wechat.capture();
  const second = await wechat.capture();
  const calls = [];
  wx.request = options => calls.push(options);
  const inFlight = wechat.report(first);
  const concurrent = wechat.report(second);
  assert.equal(calls.length, 1);
  server.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'two', memberId: 'two' });
  calls[0].success({ statusCode: 200, data: { credits: 1 } });
  await Promise.all([inFlight, concurrent]);
  assert.equal(calls.length, 1);
  assert.equal((await wechat.capture()).status, 'skip');
  server.saveConnection({ serverUrl: 'https://cup.example', apiToken: 'one', memberId: 'one' });
  const retry = wechat.report(second);
  assert.equal(calls[1].data.id, second.id);
  calls[1].success({ statusCode: 200, data: { credits: 2 } });
  await retry;
  assert.equal(calls.length, 2);
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
