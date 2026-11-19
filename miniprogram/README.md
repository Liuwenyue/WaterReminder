# 汐杯记 · 微信小程序

和 Windows 版同一套喝水规则：每日目标、工作杯、家庭杯、午休、下班前不喝水、最小间隔和免打扰。算法在 `utils/calculator.js`，对照 `WaterReminder.Core` 的动态计划移植。

## 打开

1. 安装 [微信开发者工具](https://developers.weixin.qq.com/miniprogram/dev/devtools/download.html)。
2. 导入这个文件夹。不要导入仓库根目录。
3. 导入时核对开发者工具从 `project.config.json` 读取的 AppID。
4. 编译后用预览扫码，在手机上查看。

在「今日」点「记一杯」会按当前水杯的容量记录。「自定义」可输入单笔饮水量。「记录」页可以点柱子切换日期，也可以用左右箭头回看过去 30 天。在「编辑」里可校正总量、删除一笔或清空当天。连接后，首页显示成员名称和同步状态。旧的本机记录可在设置里手动导入当前账本。

页面随系统切换浅色和深色外观。记水、柱图和面板的过渡会响应系统的减弱动态效果设置。

## 和桌面版的差别

手机提醒由服务器统一发送。在「设置 → 手机提醒」选择自动、仅微信、仅 PushWard 或两者同时发送。自动模式优先使用微信，未绑定或次数用完时使用该成员已配置的 PushWard。保存后可点「授权一次提醒」增加微信提醒次数。关闭总开关会停用手机提醒。

首次打开，在「设置」填写桌面端所用服务的 HTTPS 地址及 `client.json` 中的 `apiToken`，点「连接服务器」。手机和桌面端将读写同一账本。收到微信邀请的家人可向邀请人确认服务器地址，在设置页点「微信登录并接受邀请」加入自己的成员账本。使用旧临时测试地址的用户升级后需重新确认连接，已有本机数据按账本保留。

服务器默认只监听 `127.0.0.1:8787`，手机访问不到。需要一个手机能打开的 HTTPS 地址，并在微信公众平台把该域名配进 request 合法域名。开发者工具可在「详情 → 本地设置」里临时关闭合法域名校验；正式版不能用 http 或本机地址。

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
