'use strict';

const calc = require('../../utils/calculator.js');

Component({
  properties: {
    heading: String,
    context: String,
    actionId: Number,
    confirmText: { type: String, value: '记下' },
    initialValue: { type: String, value: '' },
    minimum: { type: Number, value: 1 },
    maximum: { type: Number, value: 5000 },
    presets: { type: Array, value: [] }
  },
  data: { value: '', error: '', closing: false, keyboardHeight: 0, dragging: false, dragY: 0 },
  lifetimes: {
    attached() {
      getApp().globalData.amountEditorOpen = true;
      this.setData({ value: this.data.initialValue });
      wx.hideTabBar({ animation: false });
    },
    detached() {
      clearTimeout(this._closeTimer);
      getApp().globalData.amountEditorOpen = false;
      wx.showTabBar({ animation: false });
    }
  },
  methods: {
    blockScroll() {},
    onInput(event) { this.setData({ value: event.detail.value, error: '' }); },
    pick(event) { this.setData({ value: String(event.currentTarget.dataset.amount), error: '' }); },
    onKeyboard(event) { this.setData({ keyboardHeight: Math.max(0, event.detail.height || 0) }); },
    showError(error) { this.setData({ error }); },
    confirm() {
      if (this.data.closing) return;
      const parsed = calc.tryParseTotal(this.data.value);
      if (!parsed.ok || parsed.total < this.data.minimum || parsed.total > this.data.maximum) {
        this.showError('请输入 ' + this.data.minimum + ' 到 ' + this.data.maximum + ' 之间的整数毫升数。');
        return;
      }
      this.triggerEvent('confirm', { actionId: this.data.actionId, amount: parsed.total });
    },
    startDrag(event) {
      this._startY = event.touches[0].clientY;
      this.setData({ dragging: true });
    },
    moveDrag(event) {
      if (!this.data.dragging) return;
      const dragY = Math.max(0, event.touches[0].clientY - this._startY);
      if (Math.abs(dragY - this.data.dragY) >= 3) this.setData({ dragY });
    },
    endDrag(event) {
      if (!this.data.dragging) return;
      if (event.changedTouches[0].clientY - this._startY > 54) this.dismiss();
      else this.cancelDrag();
    },
    cancelDrag() { this.setData({ dragging: false, dragY: 0 }); },
    dismiss() {
      if (this.data.closing) return;
      this.setData({ closing: true, dragging: false });
      this._closeTimer = setTimeout(() => this.triggerEvent('close', { actionId: this.data.actionId }), 150);
    }
  }
});
