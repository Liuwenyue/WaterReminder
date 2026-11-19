'use strict';

const app = getApp();
const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');
const journal = require('../../utils/journal.js');

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
    if (this.data.amountOpen || this.data.editing) this.setData({ amountOpen: false, editing: false });
  },
  reload() {
    const now = new Date();
    const connected = !!store.accountKey();
    const today = connected ? store.shanghaiClock(now) : now;
    const offset = this.data.offset;
    const date = journal.dayAtOffset(today, offset);
    this._selectedDate = date;
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
      events, empty: events.length === 0
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
    this._editDate = this._selectedDate;
    this.setData({ amountOpen: true, initialTotal: String(this.data.total) });
  },
  saveTotal(event) {
    const result = store.setTotal(this._editDate, event.detail.amount);
    if (!result.ok) {
      this.selectComponent('#amountSheet').showError(result.error);
      return;
    }
    this.selectComponent('#amountSheet').dismiss();
    if (result.pending) app.syncChanges();
    this.reload();
    wx.showToast({ title: result.unchanged ? '总量没有变化' : (result.pending ? '待同步' : '总量已更新'), icon: 'none' });
  },
  closeAmount() { this.setData({ amountOpen: false }); },
  remove(event) {
    const id = event.currentTarget.dataset.id;
    const date = this._selectedDate;
    app.globalData.prompting = true;
    wx.showModal({
      title: '删除这笔饮水？', confirmText: '删除', confirmColor: '#BC3848',
      success: result => {
        if (!result.confirm) return;
        const removed = store.deleteEvent(date, id);
        if (removed && removed.pending) app.syncChanges();
        if (removed && removed.ok === false) wx.showToast({ title: removed.error, icon: 'none' });
        this.reload();
      },
      complete: () => { app.globalData.prompting = false; }
    });
  },
  clearDay() {
    const date = this._selectedDate;
    app.globalData.prompting = true;
    wx.showModal({
      title: '清空当天记录？', content: '将删除 ' + journal.monthDay(date) + '的全部饮水记录。',
      confirmText: '清空', confirmColor: '#BC3848',
      success: result => {
        if (!result.confirm) return;
        const cleared = store.clearDay(date);
        if (cleared && !cleared.ok) return wx.showToast({ title: cleared.error, icon: 'none' });
        if (cleared && cleared.pending) app.syncChanges();
        this.setData({ editing: false });
        this.reload();
      },
      complete: () => { app.globalData.prompting = false; }
    });
  }
});
