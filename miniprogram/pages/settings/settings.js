'use strict';

const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');
const server = require('../../utils/server.js');
const wechat = require('../../utils/wechat.js');
const config = require('../../utils/config.js');

const DAY_LABELS = ['一', '二', '三', '四', '五', '六', '日'];
const PHONE_MODES = ['Auto', 'WeChat', 'PushWard', 'Both'];
const FIELD_ORDER = ['dailyGoalMl', 'workCupCapacityMl', 'homeCupCapacityMl', 'workStart', 'workEnd',
  'lunchStart', 'lunchEnd', 'quietStart', 'minReminderGapMinutes', 'noDrinkBeforeEndMinutes', 'activeDays'];

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
    wechatText: '微信提醒状态未知',
    wechatReady: false,
    pushWardState: 'unknown',
    wechatState: 'unknown',
    signInFailed: false,
    fieldErrors: {},
    inviteOpen: false,
    inviteBusy: false,
    invitePreview: null,
    inviteError: '',
    pairOpen: false, pairBusy: '', pairCode: '', pairTime: '', pairExpired: false, pairError: '',
    ownerLedger: false, ownerLinked: false, ownerSyncText: '', ownerNeedsSync: false
  },
  onShow() {
    this._hidden = false;
    const state = getApp().globalData;
    if (state.openOwnerPair) {
      state.openOwnerPair = false;
      this.setData({ pairOpen: true });
    }
    this.reload(false);
    if (!this.data.connected) return this.retrySignIn();
    if (this.data.dirty || this.data.saving || this.data.inviteBusy) return Promise.resolve(true);
    return this.loadFromServer();
  },
  onHide() {
    this._hidden = true;
    this.cancelPairCodeRequest();
    clearTimeout(this._pairTimer);
    this._read = null;
    this._wechatRequest = null;
    this._confirm = null;
    if (!this._invite || !this._invite.redeeming) this.cancelInvitation();
    this.setData({ loading: false });
  },
  onUnload() { this._unloaded = true; this.onHide(); },
  reload(refresh = true) {
    const connection = server.loadConnection();
    const account = store.accountKey();
    const changed = this._account !== undefined && this._account !== account;
    this._account = account;
    const sync = connection.configured ? store.syncInfo() : null;
    const ownerLedger = server.isOwnerLedger(connection);
    const ownerLinked = ownerLedger && connection.loginMethod === 'wechat';
    const ownerNeedsSync = ownerLinked && (!sync.synced || !!sync.error || sync.pending > 0);
    if (changed || ownerLinked) this.cancelPairCodeRequest();
    if (changed || ownerLinked) this.clearPairCode();
    this.setData({
      connected: connection.configured, memberName: connection.memberName,
      ownerLedger, ownerLinked, ownerNeedsSync,
      ownerSyncText: ownerLinked && (this.data.pairBusy === 'sync' ? '正在同步记录…' :
        ownerNeedsSync ? '记录尚未同步' : '记录已同步') || '',
      syncText: !ownerLinked && sync && (sync.pending || sync.error || sync.blocked) ? sync.text : '',
      syncBlocked: !!(sync && sync.blocked),
      legacyCount: sync ? store.legacyCount() : 0,
      ...(changed ? { loaded: false, dirty: false, fieldErrors: {}, conflict: false, pairError: '',
        wechatReady: false, wechatState: 'unknown', pushWardState: 'unknown' } : {})
    });
    if (!ownerLinked) this.updatePairTime();
    if (changed) {
      this.cancelInvitation();
      if (refresh && connection.configured && !this.data.saving) return this.loadFromServer();
    }
  },
  retrySignIn() {
    if (this.data.loading || this.data.saving || this.data.inviteBusy || this.data.invitePreview) return Promise.resolve(false);
    return this.loadFromServer({ signIn: true });
  },
  async createOwnerPair() {
    if (this.data.pairBusy || this.data.ownerLinked || !this.data.pairOpen) return;
    const attempt = { account: store.accountKey() };
    this._pairCodeRequest = attempt;
    const active = () => this._pairCodeRequest === attempt && !this._hidden && this.data.pairOpen &&
      store.accountKey() === attempt.account;
    this.clearPairCode();
    this.setData({ pairBusy: 'code', pairError: '' });
    try {
      const result = await server.createOwnerPair();
      if (!active()) return;
      const pair = result.ok && result.data;
      const expiresAt = pair ? Date.parse(pair.expiresAt) : NaN;
      if (!pair || typeof pair.code !== 'string' || !/^[0-9]{8}$/.test(pair.code) || !Number.isFinite(expiresAt)) {
        this.setData({ pairError: result.error || '无法获取配对码，请重试。' });
        return;
      }
      this._pairCode = { code: pair.code, expiresAt, account: attempt.account };
      this.updatePairTime();
    } catch (_) {
      if (active()) this.setData({ pairError: '无法获取配对码，请重试。' });
    } finally {
      this.cancelPairCodeRequest();
      if (!this._unloaded) this.setData({ pairBusy: '' });
    }
  },
  cancelPairCodeRequest() {
    this._pairCodeRequest = null;
  },
  clearPairCode(expired = false) {
    clearTimeout(this._pairTimer);
    this._pairCode = null;
    if (this.data.pairCode || this.data.pairExpired !== expired)
      this.setData({ pairCode: '', pairTime: '', pairExpired: expired });
  },
  updatePairTime() {
    clearTimeout(this._pairTimer);
    const pair = this._pairCode;
    if (!pair) return false;
    if (pair.account !== store.accountKey()) {
      this.clearPairCode();
      return false;
    }
    const remaining = pair.expiresAt - Date.now();
    if (remaining <= 0) {
      this.clearPairCode(true);
      return false;
    }
    if (this._hidden || !this.data.pairOpen) return false;
    const seconds = Math.ceil(remaining / 1000);
    const pairTime = Math.floor(seconds / 60) + ':' + String(seconds % 60).padStart(2, '0');
    this.setData({ pairCode: pair.code, pairTime });
    this._pairTimer = setTimeout(() => this.updatePairTime(), (remaining - 1) % 1000 + 1);
    return true;
  },
  copyOwnerPair() {
    if (this.data.pairBusy || !this.updatePairTime()) return;
    const pair = this._pairCode;
    wx.setClipboardData({ data: pair.code, fail: () => {
      if (!this._hidden && this._pairCode === pair && this.data.pairOpen)
        this.setData({ pairError: '未能复制配对码，请重试。' });
    } });
  },
  async openOwnerLedger() {
    if (this.data.pairBusy || this.data.ownerLinked || this.data.saving || this.data.inviteBusy || this.data.invitePreview) return;
    this.setData({ pairBusy: 'open', pairError: '' });
    try {
      const result = await server.openOwnerLedger();
      if (!result.ok) {
        if (!this._hidden && !result.stale) this.setData({ pairError: result.error || '无法打开管理员账本，请重试。' });
        return;
      }
      if (this._unloaded) return;
      this.clearPairCode();
      this.setData({ pairBusy: '' });
      this.reload(false);
      await this.retryOwnerSync();
    } catch (_) {
      if (!this._hidden) this.setData({ pairError: '无法打开管理员账本，请重试。' });
    } finally {
      if (!this._unloaded && this.data.pairBusy === 'open') this.setData({ pairBusy: '' });
    }
  },
  async retryOwnerSync() {
    if (this.data.pairBusy || !this.data.ownerLinked) return;
    const account = store.accountKey();
    this.setData({ pairBusy: 'sync', ownerSyncText: '正在同步记录…' });
    try {
      const synced = await getApp().signIn();
      if (account !== store.accountKey() || this._unloaded) return;
      if (!synced.ok && !store.syncInfo().error)
        store.syncFailure(synced.error || '暂时无法读取记录，请重试。', store.syncInfo().blocked);
      if (!this._hidden && !this.data.dirty) await this.loadFromServer();
    } catch (_) {
      if (account === store.accountKey()) store.syncFailure('暂时无法读取记录，请重试。', store.syncInfo().blocked);
    } finally {
      if (!this._unloaded) {
        this.setData({ pairBusy: '' });
        this.reload(false);
      }
    }
  },
  toggleOwnerPair() {
    const pairOpen = !this.data.pairOpen;
    this.setData({ pairOpen });
    if (!pairOpen) this.cancelPairCodeRequest();
    this.updatePairTime();
  },
  async loadFromServer({ signIn = false, discard = false } = {}) {
    if (this.data.saving || this.data.inviteBusy) return false;
    const read = {};
    this._read = read;
    const draft = this._draft;
    this.setData({ loading: true, signInFailed: false, error: '',
      connectionText: server.loadConnection().configured ? '正在读取服务器设置…' : '正在建立你的账本…' });
    try {
      let result = signIn ? await server.signIn({ force: true }) : await server.getSettings();
      if (this._read !== read) return false;
      if (!signIn && !result.stale && (result.status === 401 || result.status === 403)) {
        result = await server.signIn({ force: true });
      }
      if (this._read !== read) return false;
      if (result.ok && !result.settings) result = await server.getSettings();
      if (this._read !== read || result.stale) return false;
      this.reload(false);
      if (!result.ok) {
        this.setData({ signInFailed: true, connectionText: result.error });
        return false;
      }
      store.saveSettings(server.toLocal(result.settings));
      if (this.data.dirty && (!discard || draft !== this._draft)) {
        this.setData({ connectionText: '已连接，未保存的修改还在' });
        return true;
      }
      this.applyLoaded(result.settings);
      this.refreshWeChat();
      return true;
    } catch (_) {
      if (this._read === read) this.setData({ signInFailed: true, connectionText: '读取设置失败，请重试。' });
      return false;
    } finally {
      if (this._read === read) {
        this._read = null;
        this.setData({ loading: false });
      }
    }
  },
  async refreshWeChat() {
    const account = store.accountKey();
    const request = {};
    this._wechatRequest = request;
    this.setData({ wechatReady: false, wechatState: 'unknown', wechatText: '正在查询微信提醒状态…' });
    const prepare = !this.data.dirty && this.data.mobilePushEnabled && this.data.phoneMode !== 'PushWard';
    const status = await Promise.resolve().then(() => prepare ? wechat.prepare() : wechat.availability())
      .catch(() => ({ unknown: true }));
    if (this._wechatRequest !== request || account !== store.accountKey()) return;
    const unknown = !status || status.unknown === true;
    const ready = !unknown && !!(status.enabled && status.bound && status.templateId);
    this.setData({
      wechatReady: ready,
      wechatState: unknown ? 'unknown' : ready && status.credits > 0 ? 'ready' : 'unavailable',
      wechatText: unknown ? '微信提醒状态暂不可查，请稍后重试。' :
        ready && !status.credits ? '当前可发送 0 次，可授权增加次数。' : wechat.describe(status)
    });
  },
  async enableWeChat() {
    if (!this.data.wechatReady || this.data.dirty || this.data.saving || this.data.inviteBusy) return;
    const account = store.accountKey();
    const result = await wechat.capture();
    if (result.status === 'accept') wx.showToast({ title: '已授权', icon: 'none' });
    else if (result.status === 'reject' || result.status === 'ban') wx.showToast({ title: '没有允许，次数没增加', icon: 'none' });
    if (result.status === 'accept') await wechat.report(result);
    if (account === store.accountKey()) return this.refreshWeChat();
  },
  applyLoaded(settings, edits) {
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
      quietStart: settings.quietStart,
      noDrinkBeforeEndMinutes: String(settings.noDrinkBeforeEndMinutes),
      minReminderGapMinutes: String(settings.minReminderGapMinutes),
      activeDays: settings.activeDays.slice(),
      mobilePushEnabled: !!settings.mobilePushEnabled,
      phoneMode: settings.phoneMode,
      phoneModeIndex: PHONE_MODES.indexOf(settings.phoneMode),
      serverPaused: !!settings.remindersPaused,
      serverPauseUntil: settings.pauseUntil || null,
      pushWardState: settings.pushWardConfigured === true ? 'ready' :
        settings.pushWardConfigured === false ? 'unavailable' : 'unknown',
      loaded: true,
      connected: true,
      saving: false,
      signInFailed: false,
      dirty: !!edits,
      fieldErrors: {},
      error: '',
      connectionText: server.connectionLabel(server.loadConnection().serverUrl)
    };
    Object.assign(data, edits);
    data.dayCount = data.activeDays.filter(Boolean).length;
    data.preview = previewFor(data);
    this.setData(data);
  },
  updateInvite(event) {
    if (this.data.inviteBusy) return;
    this.cancelInvitation();
    this.setData({ inviteToken: event.detail.value, inviteError: '' });
  },
  toggleInvitation() {
    if (this._invite && this._invite.redeeming) return;
    if (this.data.inviteOpen) this.cancelInvitation();
    this.setData({ inviteOpen: !this.data.inviteOpen });
  },
  scanInvite() {
    if (this.data.saving || this.data.inviteBusy) return;
    if (typeof wx.scanCode !== 'function') {
      this.setData({ inviteError: '请粘贴电脑上的邀请码或完整邀请文本。' });
      return;
    }
    this.cancelInvitation();
    const scan = {};
    this._scan = scan;
    wx.scanCode({
      success: res => { if (this._scan === scan) return this.previewInvitation(res.result); }
    });
  },
  cancelInvitation() {
    const invite = this._invite;
    if (invite && invite.redeeming) return;
    this._invite = null;
    this._scan = null;
    if (invite) server.cancelInvite(invite.previewId);
    this.setData({ inviteBusy: false, invitePreview: null, inviteError: '' });
  },
  inviteStillCurrent(invite) {
    const connection = server.loadConnection();
    return this._invite === invite && store.accountKey() === invite.account &&
      connection.apiToken === invite.connection.apiToken && connection.serverUrl === invite.connection.serverUrl;
  },
  inviteKeepsQueue(invite) {
    if (!invite.account || !store.load().queue.length) return true;
    const current = invite.connection;
    const serverUrl = invite.targetUrl;
    const sameServer = serverUrl === current.serverUrl ||
      (current.serverUrl === config.serverUrl && config.previousDefaultUrls.includes(serverUrl));
    return sameServer && !!current.memberId && invite.memberId === current.memberId;
  },
  async previewInvitation(raw) {
    if (this.data.saving || this.data.inviteBusy) return false;
    this.cancelInvitation();
    const parsed = server.parseInvite(typeof raw === 'string' ? raw : this.data.inviteToken);
    if (!parsed) {
      this.setData({ inviteError: '请粘贴电脑上的邀请，或扫描邀请二维码。' });
      return false;
    }
    const connection = server.loadConnection();
    const serverUrl = parsed.serverUrl || connection.serverUrl || config.serverUrl;
    if (!serverUrl) {
      this.setData({ inviteError: '请使用包含服务器地址的完整邀请或二维码。' });
      return false;
    }
    const invite = { account: store.accountKey(), connection, serverUrl, token: parsed.inviteToken };
    this._invite = invite;
    // A pending settings response must not replace the invite view or draft.
    this._read = null;
    this.setData({ loading: false, inviteBusy: true, inviteError: '' });
    try {
      const result = await server.previewInvite(serverUrl, parsed.inviteToken);
      invite.previewId = result.previewId;
      if (!this.inviteStillCurrent(invite)) {
        if (result.previewId !== undefined) server.cancelInvite(result.previewId);
        if (this._invite === invite) this.setData({ inviteError: '当前账本已变化，请重新预览邀请。' });
        return false;
      }
      if (!result.ok) {
        this.setData({ inviteError: result.error });
        return false;
      }
      const member = result.data && result.data.member;
      const target = server.validateConnection(result.data && result.data.serverUrl || serverUrl, 'invite');
      if (!member || typeof member.name !== 'string' || !member.name || target.error) {
        this.setData({ inviteError: '无法识别邀请里的账本，请联系邀请人。' });
        return false;
      }
      invite.memberId = member.id;
      invite.targetUrl = target.serverUrl;
      invite.expiresAt = Date.parse(result.data.expiresAt);
      if (!this.inviteKeepsQueue(invite)) {
        this.setData({ inviteError: '当前账本还有待同步记录，请先完成同步再切换账本。' });
        return false;
      }
      this.setData({ invitePreview: { memberName: member.name, serverUrl: target.serverUrl } });
      return true;
    } catch (_) {
      if (this._invite === invite) this.setData({ inviteError: '无法预览邀请，请稍后重试。' });
      return false;
    } finally {
      if (this._invite === invite) {
        this.setData({ inviteBusy: false });
        if (!this.data.invitePreview) {
          const error = this.data.inviteError;
          this.cancelInvitation();
          this.setData({ inviteError: error });
        }
      }
    }
  },
  async confirmInvitation() {
    const invite = this._invite;
    if (!invite || !this.data.invitePreview || this.data.saving || this.data.inviteBusy) return false;
    if (invite.expiresAt <= Date.now()) {
      this.cancelInvitation();
      this.setData({ inviteError: '这条邀请已过期，请联系邀请人重新生成。' });
      return false;
    }
    if (!this.inviteStillCurrent(invite) || !this.inviteKeepsQueue(invite)) {
      const changed = !this.inviteStillCurrent(invite);
      this.cancelInvitation();
      this.setData({ inviteError: changed ? '当前账本已变化，请重新预览邀请。' :
        '当前账本还有待同步记录，请先完成同步再切换账本。' });
      return false;
    }
    invite.redeeming = true;
    this.setData({ inviteBusy: true });
    let result;
    try {
      result = await server.redeemInvite(invite.serverUrl, invite.token, invite.previewId);
    } catch (_) {
      result = { ok: false, error: '无法兑换邀请，请稍后重试。' };
    }
    if (this._invite !== invite) return false;
    this._invite = null;
    this.cancelInvitation();
    if (!result.ok) {
      this.setData({ inviteError: result.error });
      return false;
    }
    this.setData({ inviteOpen: false, inviteToken: '' });
    this.reload(false);
    await server.syncLedger();
    return this._hidden ? true : this.onShow();
  },
  async importOld() {
    if (this.data.inviteBusy || this.data.invitePreview || this.data.saving) return false;
    const result = store.importLegacy();
    if (!result.ok) { this.setData({ error: result.error }); return false; }
    await server.syncLedger();
    return this._hidden ? true : this.onShow();
  },
  reloadConflict() {
    const account = store.accountKey();
    const draft = this._draft;
    const confirmation = {};
    this._confirm = confirmation;
    wx.showModal({
      title: '重新读取设置？', content: '这会替换本页未保存的修改。', confirmText: '重新读取',
      success: answer => {
        if (answer.confirm && this._confirm === confirmation && account === store.accountKey() && draft === this._draft)
          return this.loadFromServer({ discard: true });
      }
    });
  },
  dropPending() {
    if (this.data.inviteBusy || this.data.invitePreview || this.data.saving) return;
    const account = store.accountKey();
    const ids = store.load().queue.map(op => op.id);
    if (!ids.length) return;
    const confirmation = {};
    this._confirm = confirmation;
    wx.showModal({
      title: '丢弃未同步更改？',
      content: '将移除本机待同步的 ' + ids.length + ' 项更改，服务器账本会保留现有记录。',
      confirmText: '丢弃', confirmColor: '#BC3848',
      success: answer => {
        if (!answer.confirm || this._confirm !== confirmation || account !== store.accountKey() ||
            this.data.inviteBusy || this.data.invitePreview || this.data.saving) return;
        if (store.dropQueue(ids)) getApp().syncChanges();
        this.reload();
      }
    });
  },
  edit(patch) {
    const draft = Object.assign({}, this.data, patch);
    this._draft = {};
    if (this._save) Object.assign(this._save.edits, patch);
    this.setData(Object.assign({}, patch, {
      dirty: true,
      error: '',
      fieldErrors: Object.keys(this.data.fieldErrors).length ? server.validateSettings(draft).fieldErrors : {},
      preview: previewFor(draft),
      dayCount: draft.activeDays.filter(Boolean).length
    }));
    if ('mobilePushEnabled' in patch || 'phoneMode' in patch) this.refreshWeChat();
  },
  updateField(event) { this.edit({ [event.currentTarget.dataset.key]: event.detail.value }); },
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
  async save() {
    if (this.data.saving || this.data.loading || this.data.inviteBusy || this.data.invitePreview ||
        !this.data.loaded || !this.data.dirty) return false;
    const checked = server.validateSettings(this.data);
    if (checked.error) {
      const first = FIELD_ORDER.find(field => checked.fieldErrors[field]);
      this.setData({ fieldErrors: checked.fieldErrors, error: '' }, () => {
        if (first && typeof wx.pageScrollTo === 'function') wx.pageScrollTo({ selector: '#field-' + first, duration: 200 });
      });
      return false;
    }
    const save = { edits: {} };
    this._save = save;
    const account = store.accountKey();
    this.setData({ saving: true, fieldErrors: {}, error: '' });
    try {
      const result = await server.saveSettings(checked.update);
      if (account !== store.accountKey()) return false;
      if (!result.ok) {
        let error = result.error;
        if (result.status === 401 || result.status === 403) {
          const renewed = await server.signIn({ force: true });
          if (account !== store.accountKey()) return false;
          error = renewed.ok ? '连接已恢复，请再次保存。' : renewed.error;
        }
        this.setData({ dirty: true, error, conflict: result.status === 409 });
        return false;
      }
      const edits = Object.keys(save.edits).length ? save.edits : null;
      this.applyLoaded(result.settings, edits);
      this.refreshWeChat();
      wx.showToast({ title: edits ? '已保存，仍有新修改' : '已保存', icon: 'success' });
      return true;
    } catch (_) {
      if (account === store.accountKey()) this.setData({ error: '保存失败，请重试。' });
      return false;
    } finally {
      this._save = null;
      this.setData({ saving: false });
      if (account !== store.accountKey() && !this._hidden) this.onShow();
    }
  }
});
