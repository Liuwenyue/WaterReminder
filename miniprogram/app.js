'use strict';

const store = require('./utils/storage.js');
const calc = require('./utils/calculator.js');
const server = require('./utils/server.js');
const endpoint = require('./utils/endpoint.js');

App({
  globalData: { prompting: false, amountEditorOpen: false, openTodayRecords: false, authState: 'idle', authError: '' },
  onShow(options) {
    this.startWatch();
    const share = options && options.query && options.query.share || '';
    if (share && server.loadConnection().configured) {
      if (wx.showToast) wx.showToast({ title: '已登录的账号不会自动共享记录', icon: 'none' });
    } else if (share) {
      this._pendingShare = share;
    }
    this.startSession(share);
  },
  startSession(share) {
    const ticket = (this._settingsSync || 0) + 1;
    this._settingsSync = ticket;
    if (!server.loadConnection().configured) this.globalData.authState = 'loading';
    const ready = server.loadConnection().configured
      ? Promise.resolve({ ok: true })
      : server.loginWeChat(endpoint.serverUrl, share || this._pendingShare || '');
    return ready.then((result) => {
      if (ticket !== this._settingsSync) return false;
      if (!result.ok) {
        this.globalData.authState = 'error';
        this.globalData.authError = result.error || '连接失败，请重试。';
        this.refreshPages();
        return false;
      }
      this.globalData.authState = 'ready';
      this._pendingShare = '';
      this.globalData.authError = '';
      this.refreshPages();
      return server.syncLedger().catch(() => false).then(() => server.refreshCachedSettings());
    }).then(() => {
      if (ticket === this._settingsSync) this.refreshPages();
    }).catch(() => {
      this.globalData.authState = 'error';
      this.globalData.authError = '连接失败，请重试。';
      this.refreshPages();
    });
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
    if (!server.loadConnection().configured) return;
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
