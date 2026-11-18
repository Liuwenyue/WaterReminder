# 汐杯记 · 微信小程序

和 Windows 版同一套喝水规则：每日目标、工作杯、家庭杯、午休、下班前不喝水、最小间隔和免打扰。算法在 `utils/calculator.js`，对照 `WaterReminder.Core` 的动态计划移植。

## 打开

1. 安装 [微信开发者工具](https://developers.weixin.qq.com/miniprogram/dev/devtools/download.html)。
2. 导入这个文件夹。不要导入仓库根目录。
3. 导入时核对开发者工具从 `project.config.json` 读取的 AppID。
4. 编译后用预览扫码，在手机上查看。

在「今日」点「记一杯」会按当前水杯的容量记录。点「自定义」可从底部面板选择常用容量或输入毫升数。「记录」页可以点柱子切换日期，也可以用左右箭头回看过去 30 天。在「编辑」里可校正总量、删除一笔或清空当天。「设置」里可以修改目标、水杯和提醒时间，点「保存设置」写入本机。

页面随系统切换浅色和深色外观。记水、柱图和面板的过渡会响应系统的减弱动态效果设置。

## 和桌面版的差别

小程序不能在完全退出后自己响铃，也不能访问家里那台只监听本机的服务器。到点时如果小程序还开着，会弹出「该喝水了」；关掉之后再打开，如果已经到点，也会补弹一次。后台推送仍然用原来的 Windows 悬浮窗和 PushWard。

## 测试

在仓库根目录运行：

```bash
node ./miniprogram-tests/calculator.test.js
node --test ./miniprogram-tests/interface.test.js
```

<details>
<summary>首页水纹素材</summary>

`assets/tide.jpg` 由内置图像生成工具制作，随小程序包加载。生成提示词如下。

> Use case: photorealistic-natural. Asset: a wide, full-bleed background photograph for the small primary card of a Chinese iOS-style personal water journal. A single sculptural curl of clear drinking water swells diagonally up from lower center toward the upper RIGHT of the frame. Shot close with a macro lens, one luminous, almost glasslike folded surface, ripples with precise concentric caustics, a few suspended microbubbles, cove studio lighting, sapphire and vivid cobalt blue with icy pale-cyan edges. On the right the main crest is broad, dimensional and visually legible at small sizes. LEFT 52 percent is almost still, smooth rich deeper ink/cobalt blue with no bright areas. Lower edge falls into a deep blue. Sophisticated editorial studio photography, restrained, tactile, high contrast and identifiable water, not a vector or a generic abstract gradient. Wide landscape 2:1. Full bleed, no edges, no text, no digits, no logo, no graphic UI, no product, no ornaments.

</details>
