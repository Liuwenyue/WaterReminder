'use strict';

const server = require('./utils/server.js');

App({
  globalData: { prompting: false, amountEditorOpen: false, openTodayRecords: false, pendingShare: '' },
  onLaunch() {
    if (wx.onNetworkStatusChange) wx.onNetworkStatusChange(status => {
      if (status.isConnected && server.loadConnection().configured) this.syncChanges();
    });
  },
  syncChanges() {
    return server.syncLedger().then(() => this.refreshPages());
  },
  onShow(options) {
    const share = options && options.query && options.query.share;
    if (!server.loadConnection().configured) {
      if (share) this.globalData.pendingShare = share;
      return;
    }
    this.globalData.pendingShare = '';
    if (share) wx.showToast({ title: '当前账本已连接', icon: 'none' });
    return server.syncLedger().then(() => server.refreshCachedSettings()).then(() => this.refreshPages());
  },
  refreshPages() {
    getCurrentPages().forEach(function (page) {
      if (typeof page.reload === 'function') page.reload();
    });
  }
});
