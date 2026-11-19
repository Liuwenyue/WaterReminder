'use strict';

const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');
const server = require('../../utils/server.js');
const wechat = require('../../utils/wechat.js');

const DAY_LABELS = ['一', '二', '三', '四', '五', '六', '日'];

function previewFor(data) {
  const days = Array.isArray(data.activeDays) ? data.activeDays : [];
  if (days.length === 7 && days.every(function (active) { return !active; })) {
    return '未选择提醒日，仍可随时手动记水。';
  }
  const checked = server.validateSettings(data);
  if (checked.error) return '';
  const settings = calc.normalizeSettings(server.toLocal(checked.update));
  const firstDay = settings.activeDays.indexOf(true);
  if (firstDay < 0) return '未选择提醒日，仍可随时手动记水。';
  const morning = new Date();
  morning.setHours(0, 0, 0, 0);
  const weekday = (morning.getDay() + 6) % 7;
  morning.setDate(morning.getDate() + (firstDay - weekday + 7) % 7);
  const plan = calc.calculateDynamic(settings, { events: [] }, morning, null);
  if (!plan.nextReminder) return '按当前设置，上班时段没有提醒。';
  return '上班时段预计提醒 ' + plan.totalReminders + ' 次，首次约在 ' + calc.formatClock(plan.nextReminder) + '。';
}

Page({
  data: {
    dayLabels: DAY_LABELS,
    serverUrl: '',
    apiToken: '',
    connected: false,
    loaded: false,
    loading: false,
    saving: false,
    connectionText: '未连接服务器',
    dailyGoalMl: '',
    workCupCapacityMl: '',
    homeCupCapacityMl: '',
    workStart: '08:30',
    workEnd: '17:30',
    lunchEnabled: true,
    lunchStart: '11:50',
    lunchEnd: '13:30',
    noDrinkBeforeEndMinutes: '',
    minReminderGapMinutes: '',
    quietStart: '23:00',
    activeDays: [],
    dayCount: 0,
    mobilePushEnabled: false,
    serverPaused: false,
    serverPauseUntil: null,
    dirty: false,
    preview: '',
    error: '',
    inviteToken: '',
    memberName: '',
    syncText: '',
    syncBlocked: false,
    legacyCount: 0,
    wechatText: '连接服务器后可以开启微信提醒。',
    shareToken: '', showAdvanced: false
  },
  onShow() {
    const connection = server.loadConnection();
    const patch = {
      serverUrl: this.data.serverUrl || connection.serverUrl,
      connected: connection.configured
    };
    patch.memberName = connection.memberName || '';
    patch.syncText = connection.configured ? store.syncInfo().text : '';
    patch.syncBlocked = connection.configured && store.syncInfo().blocked;
    patch.legacyCount = connection.configured ? store.legacyCount() : 0;
    if (!connection.configured) {
      patch.connectionText = '未连接服务器';
      patch.loading = false;
      this.setData(patch);
      return Promise.resolve(false);
    }
    this.setData(patch);
    if (this.data.dirty) {
      this.setData({ connectionText: '已连接，未保存的修改还在' });
      return Promise.resolve(true);
    }
    return this.loadFromServer(false);
  },
  prepareShare() {
    if (!server.loadConnection().configured) return;
    server.createShare().then((result) => {
      const token = result.ok && result.data && result.data.token;
      this.setData({ shareToken: typeof token === 'string' ? token : '' });
    });
  },
  onShareAppMessage() {
    const token = this.data.shareToken;
    setTimeout(() => this.prepareShare(), 0);
    return token
      ? { title: '邀请你一起记录喝水，邀请人可查看和设置你的记录', path: '/pages/today/today?share=' + encodeURIComponent(token) }
      : { title: '一起记录每天的喝水量', path: '/pages/today/today' };
  },
  toggleAdvanced() { this.setData({ showAdvanced: !this.data.showAdvanced }); },
  applyLoaded(settings, extra) {
    const data = {
      dailyGoalMl: String(settings.dailyGoalMl),
      workCupCapacityMl: String(settings.workCupMl),
      homeCupCapacityMl: String(settings.homeCupMl),
      workStart: settings.workStart,
      workEnd: settings.workEnd,
      lunchEnabled: !!settings.lunchEnabled,
      lunchStart: settings.lunchStart,
      lunchEnd: settings.lunchEnd,
      noDrinkBeforeEndMinutes: String(settings.noDrinkBeforeEndMinutes),
      minReminderGapMinutes: String(settings.minReminderGapMinutes),
      quietStart: settings.quietStart,
      activeDays: settings.activeDays.slice(),
      dayCount: settings.activeDays.filter(Boolean).length,
      mobilePushEnabled: !!settings.mobilePushEnabled,
      serverPaused: !!settings.remindersPaused,
      serverPauseUntil: settings.pauseUntil || null,
      loaded: true,
      connected: true,
      loading: false,
      saving: false,
      dirty: false,
      error: '',
      connectionText: server.connectionLabel(server.loadConnection().serverUrl)
    };
    data.preview = previewFor(data);
    this.setData(Object.assign(data, extra || {}));
  },
  loadFromServer(force) {
    this.setData({ loading: true, error: '' });
    return server.getSettings().then((result) => {
      if (result.stale) {
        this.setData({ loading: false });
        return false;
      }
      if (!result.ok) {
        this.setData({
          loading: false,
          connected: server.loadConnection().configured,
          connectionText: result.error,
          error: result.error
        });
        return false;
      }
      if (this.data.dirty && !force) {
        this.setData({ loading: false, connected: true, connectionText: '已连接，未保存的修改还在' });
        return true;
      }
      store.saveSettings(server.toLocal(result.settings));
      this.applyLoaded(result.settings);
      wechat.prepare().then((status) => this.setData({ wechatText: wechat.describe(status) }));
      return true;
    }).catch(() => {
      this.setData({ loading: false, error: '读取设置失败', connectionText: '读取设置失败' });
      return false;
    });
  },

  enableWeChat() {
    const subscription = wechat.capture();
    return Promise.resolve(subscription).then((result) => {
      if (result.status === 'skip') {
        wx.showToast({ title: '服务器还没配好微信模板', icon: 'none' });
        return null;
      }
      if (result.status === 'accept') wx.showToast({ title: '已加上 1 次提醒', icon: 'none' });
      else if (result.status === 'reject' || result.status === 'ban') wx.showToast({ title: '没有允许，次数没增加', icon: 'none' });
      return wechat.report(result);
    }).then(() => wechat.prepare()).then((status) => {
      this.setData({ wechatText: wechat.describe(status) });
    });
  },
  updateServer(event) {
    this.setData({ [event.currentTarget.dataset.key]: event.detail.value });
  },
  connect() {
    const existing = server.loadConnection();
    const token = String(this.data.apiToken || '').trim() || existing.apiToken;
    const saved = server.saveConnection({ serverUrl: this.data.serverUrl, apiToken: token });
    if (saved.error) {
      this.setData({ error: saved.error, connectionText: saved.error, connected: false });
      return Promise.resolve(false);
    }
    this.setData({ serverUrl: saved.serverUrl, apiToken: '', connected: true });
    return this.loadFromServer(true);
  },
  updateInvite(event) { this.setData({ inviteToken: event.detail.value, error: '' }); },
  scanInvite() {
    if (typeof wx.scanCode !== 'function') {
      this.setData({ error: '请粘贴电脑上的邀请码' });
      return;
    }
    wx.scanCode({ success: (res) => this.redeem(res.result) });
  },
  redeem(raw) {
    const parsed = server.parseInvite(raw || this.data.inviteToken);
    if (!parsed) { this.setData({ error: '请粘贴邀请码，或扫描电脑上的邀请二维码' }); return; }
    const serverUrl = parsed.serverUrl || this.data.serverUrl;
    if (!serverUrl) { this.setData({ error: '这条邀请没有服务器地址，请先填写地址' }); return; }
    this.setData({ loading: true, error: '' });
    server.previewInvite(serverUrl, parsed.inviteToken).then((preview) => {
      if (!preview.ok) { this.setData({ loading: false, error: preview.error }); return; }
      const name = preview.data && preview.data.member && preview.data.member.name;
      wx.showModal({
        title: '加入 ' + name + ' 的饮水账本？',
        content: '确认后，这台手机只给「' + name + '」记账。',
        success: (answer) => {
          if (!answer.confirm) { this.setData({ loading: false }); return; }
          server.redeemInvite(serverUrl, parsed.inviteToken).then((result) => {
            if (!result.ok) { this.setData({ loading: false, error: result.error }); return; }
            const connection = server.loadConnection();
            this.setData({ serverUrl: connection.serverUrl, apiToken: '', connected: true, inviteToken: '', memberName: connection.memberName });
            server.syncLedger().then(() => this.loadFromServer(true));
          });
        }
      });
    });
  },
  importOld() {
    const result = store.importLegacy();
    if (!result.ok) { this.setData({ error: result.error }); return; }
    server.syncLedger().then(() => this.onShow());
  },
  dropPending() {
    store.dropQueue();
    this.setData({ syncBlocked: false, syncText: store.syncInfo().text, error: '' });
  },
  disconnect() {
    server.clearConnection();
    this.setData({
      connected: false,
      loaded: false,
      apiToken: '',
      dirty: false,
      loading: false,
      saving: false,
      serverPaused: false,
      serverPauseUntil: null,
      connectionText: '未连接服务器',
      error: ''
    });
  },
  edit(patch) {
    const draft = Object.assign({}, this.data, patch);
    this.setData(Object.assign({}, patch, {
      dirty: true,
      error: '',
      preview: previewFor(draft),
      dayCount: (draft.activeDays || []).filter(Boolean).length
    }));
  },
  updateField(event) {
    this.edit({ [event.currentTarget.dataset.key]: event.detail.value });
  },
  onDay(event) {
    const index = Number(event.currentTarget.dataset.index);
    const days = this.data.activeDays.slice();
    days[index] = !days[index];
    this.edit({ activeDays: days });
  },
  save() {
    if (this.data.saving) return Promise.resolve(false);
    if (!this.data.loaded) {
      this.setData({ error: '请先连接服务器' });
      return Promise.resolve(false);
    }
    const checked = server.validateSettings(this.data);
    if (checked.error) {
      this.setData({ error: checked.error });
      wx.showToast({ title: '设置有误', icon: 'none' });
      return Promise.resolve(false);
    }
    this.setData({ saving: true, error: '' });
    return server.saveSettings(checked.update).then((result) => {
      if (!result.ok) {
        this.setData({ saving: false, dirty: true, error: result.error });
        wx.showToast({ title: '保存失败', icon: 'none' });
        return false;
      }
      this.applyLoaded(result.settings);
      wx.showToast({ title: '已保存', icon: 'success' });
      return true;
    }).catch(() => {
      this.setData({ saving: false, dirty: true, error: '保存失败' });
      return false;
    });
  }
});
