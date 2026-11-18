'use strict';

const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');
const server = require('../../utils/server.js');

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
    error: ''
  },
  onShow() {
    const connection = server.loadConnection();
    const patch = {
      serverUrl: this.data.serverUrl || connection.serverUrl,
      connected: connection.configured
    };
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
      return true;
    }).catch(() => {
      this.setData({ loading: false, error: '读取设置失败', connectionText: '读取设置失败' });
      return false;
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
