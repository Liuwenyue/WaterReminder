'use strict';

const app = getApp();
const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');
const journal = require('../../utils/journal.js');
const server = require('../../utils/server.js');
const wechat = require('../../utils/wechat.js');

const LAST_OFFSET = 30;

function rangeDate(date) {
  return (date.getMonth() + 1) + '.' + String(date.getDate()).padStart(2, '0');
}

function goalOf(state, day, today) {
  const record = store.recordOn(state, day);
  if (record.goalKnown === true) return { known: true, goal: Number(record.goalMl) || 0 };
  if (calc.sameDay(day, today)) return { known: true, goal: state.settings.dailyGoalMl };
  if ((record.events || []).length || record.goalKnown === false) return { known: false, goal: 0 };
  return { known: true, goal: state.settings.dailyGoalMl };
}
function weekChart(state, today, selected, endOffset) {
  const bars = [];
  const oldest = journal.dayAtOffset(today, endOffset + 6);
  const newest = journal.dayAtOffset(today, endOffset);
  const selectedGoal = goalOf(state, journal.dayAtOffset(today, selected), today);
  let ceiling = Math.max(1, selectedGoal.known ? selectedGoal.goal : 1);
  for (let offset = endOffset + 6; offset >= endOffset; offset--) {
    const day = journal.dayAtOffset(today, offset);
    const total = calc.consumedMl(store.model(store.recordOn(state, day)));
    ceiling = Math.max(ceiling, total);
    bars.push({
      key: calc.dateKey(day), offset, total, selected: offset === selected,
      label: offset === 0 ? '今' : String(day.getDate()),
      ariaLabel: journal.monthDay(day) + '，' + total + ' 毫升' + (offset === selected ? '，已选中' : '')
    });
  }
  bars.forEach(bar => { bar.fraction = bar.total / ceiling; });
  return { bars, goalKnown: selectedGoal.known, goalText: selectedGoal.known ? store.formatNumber(selectedGoal.goal) : '未记录', goalTop: selectedGoal.known ? (1 - selectedGoal.goal / ceiling) * 100 : 0, rangeLabel: rangeDate(oldest) + ' – ' + rangeDate(newest) };
}

Page({
  data: {
    offset: 0, chartEnd: 0, canPrev: true, canNext: false, monthLabel: '', dateLabel: '',
    relativeLabel: '', total: 0, totalText: '0', goalText: '0', goalTop: 0, rangeLabel: '',
    bars: [], events: [], empty: true, editing: false, amountOpen: false,
    initialTotal: '', maximumTotal: calc.MAX_DAILY_TOTAL_ML
  },
  onShow() {
    if (app.globalData.openTodayRecords) {
      app.globalData.openTodayRecords = false;
      this.setData({ offset: 0, chartEnd: 0 }, () => this.reload());
    } else this.reload();
  },
  onHide() {
    this._edit = this._recordPrompt = null;
    if (this.data.amountOpen || this.data.editing) this.setData({ amountOpen: false, editing: false });
  },
  onUnload() { this.onHide(); },
  reload() {
    const now = new Date();
    const key = store.accountKey();
    const connected = !!key;
    const account = server.loadConnection();
    const ledgerName = connected ? account.memberName || '当前账本' : '本机账本';
    const today = connected ? store.shanghaiClock(now) : now;
    const offset = this.data.offset;
    const date = journal.dayAtOffset(today, offset);
    const dateKey = calc.dateKey(date);
    const old = this._view;
    const changed = old && (old.key !== key || old.dateKey !== dateKey);
    if (changed) this._edit = this._recordPrompt = null;
    this._view = { key, date, dateKey, ledgerName };
    const snap = store.snapshot(now);
    const model = store.model(store.recordOn(snap.state, date));
    if (connected) model.events.forEach(event => { event.at = store.shanghaiClock(event.at); });
    const events = journal.entries(model);
    const total = calc.consumedMl(model);
    const chartEnd = Math.max(0, offset - 6, Math.min(offset, this.data.chartEnd));
    this.setData(Object.assign(weekChart(snap.state, today, offset, chartEnd), {
      chartEnd, canPrev: offset < LAST_OFFSET, canNext: offset > 0,
      monthLabel: date.getFullYear() + '年' + (date.getMonth() + 1) + '月',
      dateLabel: journal.dateLabel(date),
      relativeLabel: offset === 0 ? '今天' : (offset === 1 ? '昨天' : journal.monthDay(date)),
      total, totalText: store.formatNumber(total),
      events, empty: events.length === 0, ledgerName,
      ...(changed ? { amountOpen: false, editing: false } : {})
    }));
  },
  selectDay(offset) {
    if (!Number.isInteger(offset) || offset < 0 || offset > LAST_OFFSET || offset === this.data.offset) return;
    this.setData({ offset, editing: false }, () => this.reload());
  },
  pickDay(event) { this.selectDay(Number(event.currentTarget.dataset.offset)); },
  prev() { this.selectDay(this.data.offset + 1); },
  next() { this.selectDay(this.data.offset - 1); },
  showToday() { this.selectDay(0); },
  toggleEdit() { this.setData({ editing: !this.data.editing }); },
  emptyAction() {
    if (this.data.offset) this.adjust();
    else wx.switchTab({ url: '/pages/today/today' });
  },
  adjust() {
    if (!this.validAction(this._view)) return;
    const actionId = (this._editSequence || 0) + 1;
    this._editSequence = actionId;
    this._edit = { ...this._view, actionId };
    this.setData({ amountOpen: true, actionId, editContext: this.actionLabel(this._edit), initialTotal: String(this.data.total) });
  },
  saveTotal(event) {
    const edit = this._edit;
    if (event.detail.actionId !== edit?.actionId || !edit) {
      this.actionExpired();
      return;
    }
    if (!this.validAction(edit)) {
      this.closeAmount(event);
      return;
    }
    const result = store.setTotal(edit.date, event.detail.amount);
    const sheet = this.selectComponent('#amountSheet');
    if (!result.ok) {
      sheet?.showError(result.error);
      return;
    }
    this._edit = null;
    sheet?.dismiss();
    wechat.capture().then(wechat.report);
    if (result.pending) app.syncChanges();
    this.reload();
    wx.showToast({ title: result.unchanged ? '总量没有变化' : (result.pending ? '待同步' : '总量已更新'), icon: 'none' });
  },
  closeAmount(event) {
    if (event.detail.actionId !== this.data.actionId) return;
    this._edit = null;
    this.setData({ amountOpen: false });
  },
  actionLabel(action) { return action.ledgerName + ' · ' + action.dateKey; },
  actionExpired(message = '账本已切换，请重新操作') {
    wx.showToast({ title: message, icon: 'none' });
  },
  validAction(action) {
    if (!action || action.key !== store.accountKey()) {
      this.actionExpired();
      return false;
    }
    const now = new Date();
    const today = action.key ? store.shanghaiClock(now) : now;
    if (action.dateKey !== calc.dateKey(journal.dayAtOffset(today, this.data.offset))) {
      this.actionExpired('记录日期已变化，请重新操作');
      return false;
    }
    return true;
  },
  remove(event) {
    const id = event.currentTarget.dataset.id;
    this.confirmRecordAction('删除这笔饮水？', '删除', '的这笔饮水记录将被删除。', date => {
      const removed = store.deleteEvent(date, id);
      if (removed && removed.pending) app.syncChanges();
      if (removed && removed.ok === false) wx.showToast({ title: removed.error, icon: 'none' });
      this.reload();
    });
  },
  clearDay() {
    this.confirmRecordAction('清空当天记录？', '清空', '的全部饮水记录将被删除。', date => {
      const cleared = store.clearDay(date);
      if (cleared && !cleared.ok) return wx.showToast({ title: cleared.error, icon: 'none' });
      if (cleared && cleared.pending) app.syncChanges();
      this.setData({ editing: false });
      this.reload();
    });
  },
  confirmRecordAction(title, confirmText, consequence, write) {
    if (!this.validAction(this._view)) return;
    const action = { ...this._view };
    this._recordPrompt = action;
    app.globalData.prompting = true;
    wx.showModal({
      title, content: this.actionLabel(action) + consequence, confirmText, confirmColor: '#BC3848',
      success: result => {
        if (!result.confirm) return;
        if (!this.validAction(action)) return;
        if (this._recordPrompt !== action) return this.actionExpired('操作已失效，请重新操作');
        this._recordPrompt = null;
        write(action.date);
      },
      complete: () => {
        if (this._recordPrompt === action) this._recordPrompt = null;
        if (!this._recordPrompt) app.globalData.prompting = false;
      }
    });
  }
});
