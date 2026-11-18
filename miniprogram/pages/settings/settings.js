'use strict';

const store = require('../../utils/storage.js');
const calc = require('../../utils/calculator.js');

const DAY_LABELS = ['一', '二', '三', '四', '五', '六', '日'];

function readInt(text) {
  const value = String(text == null ? '' : text).trim();
  if (!/^\d+$/.test(value)) return null;
  return Number(value);
}

function timeValue(text) {
  return /^\d{2}:\d{2}$/.test(text) ? text : null;
}

function minutes(text) {
  const parts = String(text).split(':');
  return Number(parts[0]) * 60 + Number(parts[1]);
}

function validate(data) {
  const goal = readInt(data.dailyGoalMl);
  const workCup = readInt(data.workCupCapacityMl);
  const homeCup = readInt(data.homeCupCapacityMl);
  const noDrink = readInt(data.noDrinkBeforeEndMinutes);
  const gap = readInt(data.minReminderGapMinutes);
  if (goal == null || goal < 1 || goal > 20000) return { error: '每日目标请填 1 到 20000' };
  if (workCup == null || workCup < 1 || workCup > 5000) return { error: '工作杯请填 1 到 5000' };
  if (homeCup == null || homeCup < 1 || homeCup > 5000) return { error: '家庭杯请填 1 到 5000' };
  if (noDrink == null || noDrink > 600) return { error: '下班前不喝水请填 0 到 600 分钟' };
  if (gap == null || gap < 1 || gap > 240) return { error: '最小间隔请填 1 到 240 分钟' };
  if (!timeValue(data.workStart) || !timeValue(data.workEnd) || !timeValue(data.lunchStart) || !timeValue(data.lunchEnd) || !timeValue(data.quietStart)) {
    return { error: '时间格式应为 HH:mm' };
  }
  if (minutes(data.workStart) >= minutes(data.workEnd)) return { error: '上班时间要早于下班时间' };
  if (data.lunchEnabled && minutes(data.lunchStart) >= minutes(data.lunchEnd)) return { error: '午休开始要早于结束' };
  return {
    error: '',
    settings: {
      dailyGoalMl: goal,
      workCupCapacityMl: workCup,
      homeCupCapacityMl: homeCup,
      workStart: data.workStart,
      workEnd: data.workEnd,
      lunchEnabled: !!data.lunchEnabled,
      lunchStart: data.lunchStart,
      lunchEnd: data.lunchEnd,
      noDrinkBeforeEndMinutes: noDrink,
      minReminderGapMinutes: gap,
      quietStart: data.quietStart,
      activeDays: data.activeDays
    }
  };
}

function previewFor(data) {
  const checked = validate(data);
  if (checked.error) return '';
  const settings = calc.normalizeSettings(checked.settings);
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
    dirty: false,
    preview: '',
    error: ''
  },
  onShow() {
    if (!this.data.dirty) this.loadFromDisk();
  },
  loadFromDisk() {
    const settings = store.load().settings;
    const data = {
      dailyGoalMl: String(settings.dailyGoalMl),
      workCupCapacityMl: String(settings.workCupCapacityMl),
      homeCupCapacityMl: String(settings.homeCupCapacityMl),
      workStart: settings.workStart,
      workEnd: settings.workEnd,
      lunchEnabled: settings.lunchEnabled,
      lunchStart: settings.lunchStart,
      lunchEnd: settings.lunchEnd,
      noDrinkBeforeEndMinutes: String(settings.noDrinkBeforeEndMinutes),
      minReminderGapMinutes: String(settings.minReminderGapMinutes),
      quietStart: settings.quietStart,
      activeDays: settings.activeDays.slice(),
      dayCount: settings.activeDays.filter(Boolean).length,
      dirty: false,
      error: ''
    };
    data.preview = previewFor(data);
    this.setData(data);
  },
  edit(patch) {
    const draft = Object.assign({}, this.data, patch);
    this.setData(Object.assign({}, patch, {
      dirty: true, error: '', preview: previewFor(draft), dayCount: draft.activeDays.filter(Boolean).length
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
    const checked = validate(this.data);
    if (checked.error) {
      this.setData({ error: checked.error });
      wx.showToast({ title: checked.error, icon: 'none' });
      return;
    }
    store.saveSettings(checked.settings);
    wx.showToast({ title: '已保存', icon: 'success' });
    this.loadFromDisk();
  }
});
