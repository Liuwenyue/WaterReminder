'use strict';

const server = require('./utils/server.js');
const store = require('./utils/storage.js');

App({
  globalData: { prompting: false, amountEditorOpen: false, openTodayRecords: false },
  onLaunch() {
    if (wx.onNetworkStatusChange) wx.onNetworkStatusChange(status => {
      if (status.isConnected) this.signIn({ restoreOwner: true });
    });
    return this.onShow();
  },
  onShow() {
    return this.signIn({ restoreOwner: true });
  },
  async signIn(options) {
    const result = await server.signIn(options);
    if (!result.ok) {
      if (server.loadConnection().configured && !result.stale) {
        store.syncFailure(result.error, store.load().blocked);
        this.refreshPages();
      }
      return result;
    }
    const synced = await this.syncWithRenewal();
    if (synced.ok) await server.refreshCachedSettings();
    this.refreshPages();
    return synced;
  },
  async syncWithRenewal(options) {
    const connection = server.loadConnection();
    const synced = await server.syncLedger(options);
    if (synced.status !== 401 && synced.status !== 403) return synced;
    const current = server.loadConnection();
    if (connection.serverUrl !== current.serverUrl || connection.apiToken !== current.apiToken)
      return { ok: false, skipped: true };
    const account = store.accountKey();
    const renewed = await server.signIn({ force: true });
    if (!renewed.ok) return renewed;
    return store.accountKey() === account ? server.syncLedger(options) : { ok: false, skipped: true };
  },
  async syncChanges() {
    const result = await this.syncWithRenewal({ operationsOnly: true });
    this.refreshPages();
    return result;
  },
  refreshPages() {
    if (typeof getCurrentPages !== 'function') return;
    getCurrentPages().forEach(function (page) {
      if (typeof page.reload === 'function') page.reload();
    });
  }
});
