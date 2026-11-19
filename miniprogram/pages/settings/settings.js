'use strict';

const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');
const server = require('../../utils/server.js');
const wechat = require('../../utils/wechat.js');

const DAY_LABELS = ['一', '二', '三', '四', '五', '六', '日'];
const PHONE_MODES = ['Auto', 'WeChat', 'PushWard', 'Both'];

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
    phoneMode: 'Auto',
    phoneModeIndex: 0,
    phoneModeLabels: ['自动（微信可用时优先）', '仅微信', '仅 PushWard', '微信和 PushWard'],
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
    wechatText: '保存提醒方式后即可授权',
    wechatReady: false,
    shareToken: '', showAdvanced: false
  },
  onShow() {
    const connection = server.loadConnection();
    const patch = {
      serverUrl: this.data.serverUrl || (connection.configured ? connection.serverUrl : ''),
      isOwner: connection.configured && connection.memberId === '00000000-0000-0000-0000-000000000000',
      connected: connection.configured
    };
    patch.memberName = connection.memberName || '';
    patch.sharedWithOwner = connection.sharedWithOwner;
    patch.syncText = connection.configured ? store.syncInfo().text : '';
    patch.syncBlocked = connection.configured && store.syncInfo().blocked;
    patch.legacyCount = connection.configured ? store.legacyCount() : 0;
    if (!connection.configured) {
      patch.connectionText = '填写桌面端使用的服务器地址和令牌，即可连接同一账本。';
      patch.pendingShare = !!getApp().globalData.pendingShare;
      patch.showAdvanced = true;
      patch.loaded = false;
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
    const account = store.accountKey();
    if (!account || !this.data.isOwner) return;
    server.createShare().then((result) => {
      if (store.accountKey() !== account) return;
      const token = result.ok && result.data && result.data.token;
      this.setData({ shareToken: typeof token === 'string' ? token : '' });
      if (!token) wx.showToast({ title: result.error || '生成邀请失败', icon: 'none' });
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
  reload() {
    const connection = server.loadConnection();
    if (!connection.configured) return;
    const sync = store.syncInfo();
    this.setData({ syncBlocked: sync.blocked, syncText: sync.text, legacyCount: store.legacyCount(), memberName: connection.memberName,
      isOwner: connection.memberId === '00000000-0000-0000-0000-000000000000' });
  },
  refreshWeChat() {
    if (!this.data.mobilePushEnabled || this.data.phoneMode === 'PushWard') {
      this.setData({ wechatText: '保存提醒方式后即可授权', wechatReady: false });
      return;
    }
    const account = store.accountKey();
    return wechat.prepare().then(status => {
      if (account === store.accountKey()) this.setData({ wechatText: wechat.describe(status), wechatReady: !!(status.enabled && status.bound && status.templateId) });
    });
  },
  applyLoaded(settings, extra) {
    const data = {
      dailyGoalMl: String(settings.dailyGoalMl),
      settingsRevision: settings.revision,
      conflict: false,
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
      phoneMode: settings.phoneMode,
      phoneModeIndex: PHONE_MODES.indexOf(settings.phoneMode),
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
          connectionText: server.connectionLabel(server.loadConnection().serverUrl),
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
      this.refreshWeChat();
      return true;
    }).catch(() => {
      this.setData({ loading: false, error: '读取设置失败' });
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
      if (result.status === 'accept') wx.showToast({ title: '已授权', icon: 'none' });
      else if (result.status === 'reject' || result.status === 'ban') wx.showToast({ title: '没有允许，次数没增加', icon: 'none' });
      return wechat.report(result);
    }).then(() => this.refreshWeChat());
  },
  updateServer(event) {
    this.setData({ [event.currentTarget.dataset.key]: event.detail.value });
  },
  connect() {
    if (this.data.loading || this.data.saving) return;
    const existing = server.loadConnection();
    const token = this.data.apiToken.trim();
    const candidate = server.validateConnection(this.data.serverUrl, token || (existing.configured && existing.apiToken) || '');
    if (!token && existing.configured && candidate.serverUrl !== existing.serverUrl) candidate.error = '连接新服务器时请重新填写 API 令牌';
    if (candidate.error) {
      this.setData({ error: candidate.error });
      return Promise.resolve(false);
    }
    this.setData({ loading: true, error: '' });
    return server.getSettings(candidate).then((result) => {
      if (!result.ok || result.stale) {
        this.setData({ loading: false, connectionText: result.error || '请重新连接' });
        return false;
      }
      server.saveConnection(candidate);
      getApp().globalData.pendingShare = '';
      store.saveSettings(server.toLocal(result.settings));
      this.applyLoaded(result.settings, { apiToken: '', serverUrl: candidate.serverUrl, shareToken: '', pendingShare: false });
      return server.syncLedger().then(() => this.onShow());
    });
  },
  joinSharedAccount() {
    const share = getApp().globalData.pendingShare;
    if (!share || this.data.loading || server.loadConnection().configured) return;
    this.setData({ loading: true });
    return server.loginWeChat(this.data.serverUrl, share).then((result) => {
      this.setData({ loading: false, connectionText: result.error || '' });
      if (!result.ok) return false;
      getApp().globalData.pendingShare = '';
      this.setData({ pendingShare: false });
      return server.syncLedger().then(() => this.onShow());
    });
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
    const parsed = server.parseInvite(typeof raw === 'string' ? raw : this.data.inviteToken);
    if (!parsed) { this.setData({ error: '请粘贴邀请码，或扫描电脑上的邀请二维码' }); return; }
    const serverUrl = parsed.serverUrl || this.data.serverUrl;
    if (!serverUrl) { this.setData({ error: '这条邀请没有服务器地址，请先填写地址' }); return; }
    this.setData({ loading: true, error: '' });
    server.previewInvite(serverUrl, parsed.inviteToken).then((preview) => {
      if (!preview.ok) { this.setData({ loading: false, error: preview.error }); return; }
      const name = preview.data && preview.data.member && preview.data.member.name;
      wx.showModal({
        title: '加入 ' + name + ' 的饮水账本？',
        content: '这台手机将给「' + name + '」记账。邀请人可以查看记录和修改设置。',
        success: (answer) => {
          if (!answer.confirm) { this.setData({ loading: false }); return; }
          server.redeemInvite(serverUrl, parsed.inviteToken).then((result) => {
            if (!result.ok) { this.setData({ loading: false, error: result.error }); return; }
            const connection = server.loadConnection();
            this.setData({ serverUrl: connection.serverUrl, apiToken: '', connected: true, dirty: false, shareToken: '', inviteToken: '', memberName: connection.memberName });
            server.syncLedger().then(() => this.onShow());
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
  reloadConflict() {
    wx.showModal({
      title: '重新读取设置？', content: '这会替换本页未保存的修改。', confirmText: '重新读取',
      success: answer => { if (answer.confirm) this.loadFromServer(true); }
    });
  },
  dropPending() {
    const account = store.accountKey();
    const ids = store.load().queue.map(op => op.id);
    if (!ids.length) return;
    wx.showModal({
      title: '丢弃未同步更改？',
      content: '将移除本机待同步的 ' + ids.length + ' 项更改，服务器账本会保留现有记录。',
      confirmText: '丢弃', confirmColor: '#BC3848',
      success: answer => {
        if (!answer.confirm || account !== store.accountKey()) return;
        if (store.dropQueue(ids)) getApp().syncChanges();
        this.reload();
      }
    });
  },
  disconnect() {
    server.clearConnection();
    this.setData({
      connected: false,
      shareToken: '',
      isOwner: false,
      memberName: '',
      syncText: '',
      legacyCount: 0,
      syncBlocked: false,
      showAdvanced: true,
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
  changePhoneMode(event) {
    const index = Number(event.detail.value);
    this.edit({ phoneModeIndex: index, phoneMode: PHONE_MODES[index] });
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
        this.setData({ saving: false, dirty: true, error: result.error, conflict: result.status === 409 });
        wx.showToast({ title: '保存失败', icon: 'none' });
        return false;
      }
      this.applyLoaded(result.settings);
      this.refreshWeChat();
      wx.showToast({ title: '已保存', icon: 'success' });
      return true;
    }).catch(() => {
      this.setData({ saving: false, dirty: true, error: '保存失败' });
      return false;
    });
  }
});
