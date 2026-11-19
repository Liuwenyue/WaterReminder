'use strict';

const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');
const journal = require('../../utils/journal.js');
const serverApi = require('../../utils/server.js');
const wechat = require('../../utils/wechat.js');

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
    planHint: '', showTip: false, recent: [], feedback: '', sipPulse: [], amountOpen: false, presets: [150, 250, 500]
  },
  onShow() { this.reload(); wechat.prepare(); },
  onHide() { this.resetTransient(); },
  onUnload() { clearTimeout(this._feedbackTimer); },
  resetTransient() {
    clearTimeout(this._feedbackTimer);
    if (this.data.amountOpen || this.data.feedback) this.setData({ amountOpen: false, feedback: '', sipPulse: [] });
  },
  reload() {
    const now = new Date();
    const snap = store.snapshot(now);
    const settings = snap.state.settings;
    const plan = calc.calculateDynamic(settings, snap.model, now, snap.record.lastReminderAt);
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
      recent: journal.entries(snap.model).slice(0, 2), showTip: !store.tipDismissed(),
      identity: serverApi.loadConnection().memberName ? ('正在为 ' + serverApi.loadConnection().memberName + ' 记录') : '',
      syncText: serverApi.loadConnection().configured ? store.syncInfo().text : '',
      sharedNotice: serverApi.loadConnection().sharedWithOwner ? '你的饮水记录可由邀请人查看和设置。' : '',
      loginText: getApp().globalData.authState === 'loading' ? '正在连接…' :
        (getApp().globalData.authState === 'error' ? getApp().globalData.authError : ''),
      loginFailed: getApp().globalData.authState === 'error'
    });
  },
  retryLogin() { getApp().startSession(''); },
  recordAmount(amount, source) {
    if (!serverApi.loadConnection().configured && ['loading', 'error'].includes(getApp().globalData.authState)) {
      return { ok: false, error: '请先连接账号' };
    }
    const result = store.addEvent(amount, source);
    if (!result.ok) return result;
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
    const subscription = wechat.capture();
    this._lastCupAt = now;
    const result = this.recordAmount(this.data.cupAmount, 'cup');
    if (!result.ok) wx.showToast({ title: result.error, icon: 'none' });
    subscription.then(wechat.report);
  },
  customAmount() { this.setData({ amountOpen: true }); },
  addCustom(event) {
    const sheet = this.selectComponent('#amountSheet');
    const result = this.recordAmount(event.detail.amount, 'custom');
    if (result.ok) sheet.dismiss();
    else sheet.showError(result.error);
  },
  closeAmount() { this.setData({ amountOpen: false }); },
  togglePause() {
    if (this.data.paused) store.resume();
    else store.pauseToday(new Date());
    this.reload();
  },
  goRecords() {
    getApp().globalData.openTodayRecords = true;
    wx.switchTab({ url: '/pages/records/records', fail: () => { getApp().globalData.openTodayRecords = false; } });
  },
  dismissTip() {
    store.dismissTip();
    this.setData({ showTip: false });
  }
});
