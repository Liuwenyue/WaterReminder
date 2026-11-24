'use strict';

const app = getApp();
const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');
const journal = require('../../utils/journal.js');
const history = require('../../utils/history-view.js');
const server = require('../../utils/server.js');
const wechat = require('../../utils/wechat.js');

const LAST_OFFSET = history.SPAN;

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
function readDay(state, day, today) {
  const record = store.recordOn(state, day);
  const goal = goalOf(state, day, today);
  return { total: calc.consumedMl(store.model(record)), goal: goal.known ? goal.goal : 0, known: goal.known };
}

function picture(state, today, selectedOffset, mode) {
  const selected = journal.dayAtOffset(today, selectedOffset);
  const chosen = readDay(state, selected, today);
  const days = mode === "month" ? history.monthCells(today, selected).filter(cell => !cell.blank).map(cell => cell.day) : history.weekDays(today, selected).map(item => item.day);
  let ceiling = Math.max(1, chosen.known ? chosen.goal : 0);
  days.forEach(day => { ceiling = Math.max(ceiling, readDay(state, day, today).total); });
  const bars = history.weekDays(today, selected).map(item => {
    const info = readDay(state, item.day, today);
    return {
      key: calc.dateKey(item.day), offset: item.offset, total: info.total, selected: item.offset === selectedOffset,
      future: item.offset < 0, tooOld: item.offset > history.SPAN,
      label: item.offset === 0 ? "今" : String(item.day.getDate()),
      fraction: info.total / ceiling,
      ariaLabel: journal.monthDay(item.day) + "，" + info.total + " 毫升" + (item.offset === selectedOffset ? "，已选中" : "")
    };
  });
  const monthCells = history.monthCells(today, selected).map(cell => {
    if (cell.blank) return Object.assign({}, cell, { future: false, tooOld: false, selected: false, amount: "" });
    const info = readDay(state, cell.day, today);
    return Object.assign({}, cell, {
      future: cell.offset < 0, tooOld: cell.offset > history.SPAN, selected: cell.offset === selectedOffset,
      amount: info.total ? store.formatNumber(info.total) : ""
    });
  });
  const periodDays = mode === "month" ? monthCells.filter(cell => !cell.blank && !cell.future).map(cell => cell.day) : history.weekDays(today, selected).filter(item => item.offset >= 0).map(item => item.day);
  const consumed = periodDays.reduce((sum, day) => sum + readDay(state, day, today).total, 0);
  const achieved = periodDays.filter(day => { const info = readDay(state, day, today); return info.known && info.goal > 0 && info.total >= info.goal; }).length;
  const oldest = days[0];
  const newest = days[days.length - 1];
  return {
    bars, monthCells, dowLabels: history.DOW,
    goalKnown: chosen.known, goalText: chosen.known ? store.formatNumber(chosen.goal) : "未记录",
    goalTop: chosen.known ? (1 - chosen.goal / ceiling) * 100 : 0,
    rangeLabel: (oldest.getMonth() + 1) + "." + String(oldest.getDate()).padStart(2, "0") + " – " + (newest.getMonth() + 1) + "." + String(newest.getDate()).padStart(2, "0"),
    periodLabel: "合计 " + store.formatNumber(consumed) + " mL · 达标 " + achieved + " 天"
  };
}

Page({
  data: {
    offset: 0, viewMode: 'week', canShiftNext: false, canPrev: true, canNext: false, monthLabel: '', periodLabel: '', dateLabel: '',
    relativeLabel: '', total: 0, totalText: '0', goalText: '0', goalTop: 0, rangeLabel: '',
    bars: [], monthCells: [], dowLabels: history.DOW, events: [], empty: true, editing: false, amountOpen: false,
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
    this._totalSubmitting = false;
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
    this._today = today;
    const mode = this.data.viewMode === 'month' ? 'month' : 'week';
    const canShiftNext = mode === 'month'
      ? date.getFullYear() < today.getFullYear() || (date.getFullYear() === today.getFullYear() && date.getMonth() < today.getMonth())
      : offset >= 7;
    this.setData(Object.assign(picture(snap.state, today, offset, mode), {
      viewMode: mode, canShiftNext, canPrev: offset < LAST_OFFSET, canNext: offset > 0,
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
  setMode(event) {
    const mode = event.currentTarget.dataset.mode === 'month' ? 'month' : 'week';
    if (mode === this.data.viewMode) return;
    this.setData({ viewMode: mode }, () => this.reload());
  },
  shiftPeriod(event) {
    if (!this._today) return;
    const dir = Number(event.currentTarget.dataset.dir);
    const selected = journal.dayAtOffset(this._today, this.data.offset);
    const offset = this.data.viewMode === 'month'
      ? history.shiftMonth(this._today, selected, dir, LAST_OFFSET)
      : Math.max(0, Math.min(LAST_OFFSET, this.data.offset - dir * 7));
    this.selectDay(offset);
  },
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
    if (this._totalSubmitting) return;
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
    this._totalSubmitting = true;
    this._edit = null;
    sheet?.dismiss();
    wechat.capture().then(wechat.report);
    if (result.pending) app.syncChanges();
    this.reload();
    wx.showToast({ title: result.unchanged ? '总量没有变化' : (result.pending ? '待同步' : '总量已更新'), icon: 'none' });
  },
  closeAmount(event) {
    if (event.detail.actionId !== this.data.actionId) return;
    this._totalSubmitting = false;
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
