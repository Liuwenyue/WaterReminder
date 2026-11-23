'use strict';

const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');
const journal = require('../../utils/journal.js');
const serverApi = require('../../utils/server.js');
const wechat = require('../../utils/wechat.js');

function notableSync(info) {
  if (!info || !(info.blocked || info.error || info.pending)) return '';
  return info.text || '';
}

function planHint(settings, model, plan, now) {
  const remaining = Math.max(0, settings.dailyGoalMl - calc.consumedMl(model));
  if (!remaining) return '今天的饮水目标已完成，仍可继续记录。';
  if (calc.isPaused(settings, now) || !calc.isActiveDay(settings, now)) return '今天还差 ' + store.formatNumber(remaining) + ' mL，可随时手动记录。';
  if (plan.suggestedMl > 0 && calc.shouldNotify(settings, now)) return '这次建议喝 ' + plan.suggestedMl + ' mL。';
  if (calc.phaseAt(settings, now) === 'AfterWork') return '今晚用家庭杯，还差 ' + store.formatNumber(remaining) + ' mL。';
  const workLeft = calc.workPortionMl(settings, remaining);
  if (workLeft > 0 && workLeft < remaining) return '上班还需 ' + store.formatNumber(workLeft) + ' mL，下班后留一杯。';
  return '距离目标还差 ' + store.formatNumber(remaining) + ' mL。';
}

Page({
  data: {
    dateLabel: '', phaseLabel: '', consumedText: '0', goalText: '0', percent: 0, fraction: 0,
    done: false, cupAmount: 0, cupLabel: '', paused: false, nextText: '', hasNext: false,
    planHint: '', recent: [], feedback: '', sipPulse: [], amountOpen: false, presets: [150, 250, 500],
    needsConnection: true, registrationRequired: false, registrationName: '', loginError: '', registering: false,
    wechatHint: '', wechatHintAction: false
  },
  onShow() {
    this._alive = true;
    this.reload();
    this.refreshWeChatHint();
  },
  onHide() {
    this._alive = false;
    this.resetTransient();
  },
  onUnload() {
    this._alive = false;
    this.resetTransient();
  },
  resetTransient() {
    this._edit = null;
    clearTimeout(this._feedbackTimer);
    if (this.data.amountOpen || this.data.feedback) this.setData({ amountOpen: false, feedback: '', sipPulse: [] });
  },
  reload() {
    const instant = new Date();
    const account = store.accountKey();
    const now = account ? store.shanghaiClock(instant) : instant;
    const view = { account, date: calc.dateKey(now) };
    if (this._view && (view.account !== this._view.account || view.date !== this._view.date)) this.resetTransient();
    this._view = view;
    const connection = serverApi.loadConnection();
    if (!connection.configured && !this._signingIn && !this.data.registrationRequired && !this.data.loginError) {
      this._signingIn = true;
      serverApi.signIn().then((result) => {
        this._signingIn = false;
        if (!this._alive) return;
        if (result.ok) this.reload();
        else if (!result.stale) this.setData({ registrationRequired: !!result.registrationRequired,
          loginError: result.registrationRequired ? '' : result.error || '连接失败，请重试。' });
      }).catch(() => {
        this._signingIn = false;
        if (this._alive) this.setData({ loginError: '连接失败，请重试。' });
      });
    }
    const snap = store.snapshot(instant);
    const settings = snap.state.settings;
    let lastReminder = snap.record.lastReminderAt;
    if (connection.configured) {
      snap.model.events.forEach(event => { event.at = store.shanghaiClock(event.at); });
      if (lastReminder) lastReminder = store.shanghaiClock(new Date(lastReminder));
      if (settings.pauseUntil) settings.pauseUntil = store.shanghaiClock(new Date(settings.pauseUntil));
    }
    const plan = calc.calculateDynamic(settings, snap.model, now, lastReminder);
    const consumed = calc.consumedMl(snap.model);
    const goal = settings.dailyGoalMl;
    const fraction = goal > 0 ? Math.min(1, consumed / goal) : 0;
    const done = fraction >= 1;
    const active = calc.isActiveDay(settings, now);
    const phase = calc.phaseAt(settings, now);
    const workCup = active && ['Working', 'Lunch', 'NoDrinkWindow'].includes(phase);
    const paused = calc.isPaused(settings, now);
    const hasNext = !!plan.nextReminder && active && !paused && !done;
    let nextText = calc.statusLine(settings, snap.model, plan, now, true);
    if (hasNext) nextText = plan.suggestedMl > 0 && calc.shouldNotify(settings, now) ? '现在' : calc.formatClock(plan.nextReminder);
    this.setData({
      dateLabel: journal.dateLabel(now),
      phaseLabel: active ? calc.phaseLabel(phase) : '休息日',
      consumedText: store.formatNumber(consumed), goalText: store.formatNumber(goal),
      fraction, percent: done ? 100 : Math.min(99, Math.round(fraction * 100)), done,
      cupAmount: calc.currentCupMl(settings, now), cupLabel: workCup ? '工作杯' : '家庭杯',
      paused, hasNext, nextText, planHint: planHint(settings, snap.model, plan, now),
      recent: journal.entries(snap.model).slice(0, 2),
      identity: connection.memberName,
      syncText: connection.configured ? notableSync(store.syncInfo()) : '',
      needsConnection: !connection.configured
    });
  },
  updateName(event) { this.setData({ registrationName: event.detail.value, loginError: '' }); },
  retryConnection() {
    this.setData({ loginError: '' });
    this.reload();
  },
  async register() {
    if (this.data.registering) return;
    const name = String(this.data.registrationName || '').trim();
    if (!name) {
      this.setData({ loginError: '请输入你的名字。' });
      return;
    }
    this.setData({ registering: true, loginError: '' });
    try {
      const result = await serverApi.signIn({ name });
      if (!result.ok) this.setData({ loginError: result.error || '注册失败，请重试。' });
      else {
        this.setData({ registrationRequired: false });
        this.reload();
        await getApp().signIn();
      }
    } catch (_) {
      this.setData({ loginError: '注册失败，请重试。' });
    } finally { this.setData({ registering: false }); }
  },
  openOwnerPairSettings() {
    const state = getApp().globalData;
    state.openOwnerPair = true;
    wx.switchTab({ url: '/pages/settings/settings', fail: () => { state.openOwnerPair = false; } });
  },
  recordAmount(amount, source) {
    const result = store.addEvent(amount, source);
    if (!result.ok) return result;
    if (result.pending) getApp().syncChanges();
    this.reload();
    if (wx.vibrateShort) wx.vibrateShort({ type: 'light' });
    clearTimeout(this._feedbackTimer);
    this._sipSequence = (this._sipSequence || 0) + 1;
    this.setData({ feedback: '+' + store.formatNumber(amount) + ' mL', sipPulse: [this._sipSequence] });
    this._feedbackTimer = setTimeout(() => this.setData({ feedback: '', sipPulse: [] }), 1400);
    return result;
  },
  drinkCup() {
    const now = Date.now();
    if (!this.data.cupAmount || now - (this._lastCupAt || 0) < 350) return;
    this._lastCupAt = now;
    const result = this.recordAmount(this.data.cupAmount, 'cup');
    if (!result.ok) {
      wx.showToast({ title: result.error, icon: 'none' });
      return;
    }
    this.renewWeChat();
  },
  refreshWeChatHint() {
    const account = store.accountKey();
    const settings = store.load().settings;
    if (!serverApi.loadConnection().configured || !settings.mobilePushEnabled || settings.phoneMode === 'PushWard') {
      if (this._alive) this.setData({ wechatHint: '', wechatHintAction: false });
      return Promise.resolve();
    }
    return wechat.availability().then(status => {
      if (!this._alive || account !== store.accountKey()) return;
      if (!status || status.unknown || !status.enabled || !status.bound) {
        this.setData({ wechatHint: '', wechatHintAction: false });
        return;
      }
      if (status.credits > 0) this.setData({ wechatHint: '微信提醒还剩 ' + status.credits + ' 次', wechatHintAction: false });
      else this.setData({ wechatHint: '还剩 0 次，点一下才能继续提醒', wechatHintAction: true });
    }).catch(() => {
      if (this._alive) this.setData({ wechatHint: '', wechatHintAction: false });
    });
  },
  renewWeChat() {
    if (this._renewing) return Promise.resolve();
    this._renewing = true;
    return wechat.capture().then(wechat.report).then(() => {
      if (this._alive) return this.refreshWeChatHint();
    }).finally(() => { this._renewing = false; });
  },
  customAmount() {
    if (!this._view) return;
    const actionId = (this._editSequence || 0) + 1;
    this._editSequence = actionId;
    this._edit = { ...this._view, actionId };
    this.setData({ amountOpen: true, actionId,
      editContext: (this.data.identity || (this._view.account ? '当前账本' : '本机账本')) + ' · ' + this._view.date });
  },
  addCustom(event) {
    const edit = this._edit;
    const account = store.accountKey();
    const now = new Date();
    const date = calc.dateKey(account ? store.shanghaiClock(now) : now);
    if (!edit || event.detail.actionId !== edit.actionId || account !== edit.account || date !== edit.date) {
      if (edit && event.detail.actionId === edit.actionId) this.resetTransient();
      wx.showToast({ title: '账本或日期已变化，请重新操作', icon: 'none' });
      return;
    }
    const sheet = this.selectComponent('#amountSheet');
    const result = this.recordAmount(event.detail.amount, 'custom');
    if (result.ok) {
      this._edit = null;
      sheet?.dismiss();
      wechat.capture().then(wechat.report);
    } else sheet?.showError(result.error);
  },
  closeAmount(event) {
    if (event.detail.actionId !== this.data.actionId) return;
    this._edit = null;
    this.setData({ amountOpen: false });
  },
  togglePause() {
    const result = this.data.paused ? store.resume() : store.pauseToday(new Date());
    if (result && result.pending) getApp().syncChanges();
    if (result && !result.ok) wx.showToast({ title: result.error, icon: 'none' });
    this.reload();
  },
  goRecords() {
    getApp().globalData.openTodayRecords = true;
    wx.switchTab({ url: '/pages/records/records', fail: () => { getApp().globalData.openTodayRecords = false; } });
  }
});
