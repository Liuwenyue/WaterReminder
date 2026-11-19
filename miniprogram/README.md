# 汐杯记 · 微信小程序

和 Windows 版同一套喝水规则：每日目标、工作杯、家庭杯、午休、下班前不喝水、最小间隔和免打扰。算法在 `utils/calculator.js`，对照 `WaterReminder.Core` 的动态计划移植。

## 打开

1. 安装 [微信开发者工具](https://developers.weixin.qq.com/miniprogram/dev/devtools/download.html)。
2. 导入这个文件夹。不要导入仓库根目录。
3. 导入时核对开发者工具从 `project.config.json` 读取的 AppID。
4. 编译后用预览扫码，在手机上查看。这一步只给开发者预览，不是用户注册，也不是关联电脑。

在「今日」点「记一杯」会按当前水杯的容量记录。「自定义」可输入单笔饮水量。「记录」页可以点柱子切换日期，也可以用左右箭头回看过去 30 天。在「编辑」里可校正总量、删除一笔或清空当天。首页显示当前成员和同步状态。

页面随系统切换浅色和深色外观。记水、柱图和面板的过渡会响应系统的减弱动态效果设置。

## 账本

首次打开会自动建立一本个人账，不用注册，也不用填写服务器地址或访问码。微信登录可用时，这本账跟着微信身份，同一个人再次打开仍是这本个人账。微信登录不可用时，用这台手机上保存的设备标识区分；清除数据或换到另一台手机后，不会自动回到这本账。

个人账还没建好时新记的饮水会先留在这台手机上，建好后写入这本个人账。更早就留在手机里的旧历史不在此列，不会自动并进去。设置里的「手动导入本机旧记录」只有你点下去，才会把这些旧记录送入当前账本。

要改记电脑上的成员，打开「设置 → 关联电脑账本」，扫描电脑上的邀请二维码，或粘贴邀请码，确认姓名后才会切换。邀请 15 分钟内有效。兑换后的凭据只保存在这台手机里，不会绑定微信登录身份，也和自动建立的个人账不是同一本。只要这台手机还留着这些数据，就可以继续用这本电脑账。换手机或清除小程序数据后，需要从电脑再领一次邀请。切换账本不会把两边的历史合并。

只有当前选中的是同一成员，并且连的是同一服务端，手机上的记录才会和那台电脑同步。断网时显示待同步，恢复后同一笔只补记一次。

## 和桌面版的差别

手机提醒由服务器按当前账本发送。在「设置 → 手机提醒」里按这本账实际可用的渠道选择。微信订阅和 PushWard 都可用时，可以选自动、仅微信、仅 PushWard，或两者同时发送；自动会先用已绑定且还有次数的微信，否则用已配置的 PushWard。没有配好的渠道不能当作可用。增加微信次数只能点「授权一次提醒」，保存设置不会自动弹出授权。关闭总开关会停用手机提醒。

`utils/config.js` 里的服务器地址是发布配置，不是用户注册。服务器默认只监听 `127.0.0.1:8787`，手机访问不到。需要一个手机能打开的 HTTPS 地址，并在微信公众平台把该域名配进 request 合法域名。开发者工具可在「详情 → 本地设置」里临时关闭合法域名校验；正式版不能用 http 或本机地址。

## 测试

在仓库根目录运行：

```bash
node ./miniprogram-tests/calculator.test.js
node --test ./miniprogram-tests/interface.test.js ./miniprogram-tests/server.test.js ./miniprogram-tests/wechat.test.js
```

<details>
<summary>首页水纹素材</summary>

`assets/tide.jpg` 由内置图像生成工具制作，随小程序包加载。生成提示词如下。

> Use case: photorealistic-natural. Asset: a wide, full-bleed background photograph for the small primary card of a Chinese iOS-style personal water journal. A single sculptural curl of clear drinking water swells diagonally up from lower center toward the upper RIGHT of the frame. Shot close with a macro lens, one luminous, almost glasslike folded surface, ripples with precise concentric caustics, a few suspended microbubbles, cove studio lighting, sapphire and vivid cobalt blue with icy pale-cyan edges. On the right the main crest is broad, dimensional and visually legible at small sizes. LEFT 52 percent is almost still, smooth rich deeper ink/cobalt blue with no bright areas. Lower edge falls into a deep blue. Sophisticated editorial studio photography, restrained, tactile, high contrast and identifiable water, not a vector or a generic abstract gradient. Wide landscape 2:1. Full bleed, no edges, no text, no digits, no logo, no graphic UI, no product, no ornaments.

</details>
