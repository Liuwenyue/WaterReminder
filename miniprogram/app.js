'use strict';

const store = require('./utils/storage.js');
const calc = require('./utils/calculator.js');
const server = require('./utils/server.js');

App({
  globalData: { prompting: false, amountEditorOpen: false, openTodayRecords: false },
  onShow() {
    this.startWatch();
    const ticket = (this._settingsSync || 0) + 1;
    this._settingsSync = ticket;
    server.refreshCachedSettings().then((changed) => {
      if (changed && ticket === this._settingsSync) this.refreshPages();
    }).catch(() => {});
  },
  onHide() {
    this.stopWatch();
  },
  startWatch() {
    this.stopWatch();
    this.checkReminder();
    this._timer = setInterval(() => this.checkReminder(), 20000);
  },
  stopWatch() {
    if (this._timer) clearInterval(this._timer);
    this._timer = null;
  },
  refreshPages() {
    getCurrentPages().forEach(function (page) {
      if (typeof page.reload === 'function') page.reload();
    });
  },
  checkReminder() {
    if (this.globalData.prompting || this.globalData.amountEditorOpen) return;
    const now = new Date();
    const snap = store.snapshot(now);
    const plan = calc.calculateDynamic(snap.state.settings, snap.model, now, snap.record.lastReminderAt);
    if (!calc.shouldNotify(snap.state.settings, now) || plan.suggestedMl <= 0) return;
    const amount = plan.suggestedMl;
    const consumed = calc.consumedMl(snap.model);
    const goal = snap.state.settings.dailyGoalMl;
    this.globalData.prompting = true;
    if (wx.vibrateShort) wx.vibrateShort({ type: 'medium' });
    wx.showModal({
      title: '该喝水了',
      content: '已喝 ' + consumed + ' / ' + goal + ' ml，建议 ' + amount + ' ml',
      confirmText: '记下',
      cancelText: '稍后',
      success: (res) => {
        store.markReminded(new Date(), res.confirm ? amount : 0);
        this.refreshPages();
      },
      complete: () => {
        this.globalData.prompting = false;
      }
    });
  }
});
