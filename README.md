<p align="center"><b>简体中文</b> | <a href="README.en.md">English</a></p>

# 喝水提醒

提醒日程在 Linux 上无界面运行。Windows 客户端提供悬浮窗、设置和今日记录，并在上班时间弹出桌面提醒。下班后，服务器按账本设置发送手机提醒。

- **WaterReminder.Core**：日程、杯量、PushWard 和记录。
- **WaterReminder.Server**：在虚拟机上运行的轻量 HTTP 服务，是设置和当日记录的唯一来源。默认端口 **8787**。时间固定为 **Asia/Shanghai**。
- **WaterReminder.Client**：Windows 托盘，以及原来的悬浮窗、设置和今日记录。本机保存服务器地址、API 令牌、可选 SSH 隧道，以及悬浮窗/开机自启等本地偏好。托盘菜单「设置」打开桌面设置窗口，把目标、杯量、时间和工作日写回服务器；也可暂停/恢复提醒。上班时间弹出「该喝水了」，下班后不再弹窗。单实例：再次启动会唤醒已有悬浮窗。
- **WaterReminder**：原来的 WPF 程序，仍可在 Windows 上单独使用。与服务器同时运行时会重复提醒。

手机提醒支持微信订阅消息和 PushWard。服务器默认只监听 `127.0.0.1:8787`。Windows 客户端可通过 `client.json` 里的 `tunnel` 自建 SSH 本地转发（示例使用占位符）。

## 最近更新

- 微信提醒默认发给正式版小程序。
- 记一杯时会请求下一次授权。今日页显示剩余次数，用完可以直接点按续上。
- 自动模式下，这次微信没发出去会改由 PushWard 发送。
- 接口限流按访问者分开。悬浮窗隐藏后不再轮询，数字没变化时大约 30 秒询问一次。


## 提醒规则（都可以改）

| 项 | 默认 |
| --- | --- |
| 每日目标 | 2000 ml |
| 上班 | 09:00–18:00 |
| 午休 | 11:50–13:30 |
| 下班前不喝水 | 30 分钟 |
| 免打扰 | 23:00 起，直到下一个工作日 |
| 工作杯 / 家庭杯 | 300 ml / 300 ml |
| 最小间隔 | 20 分钟 |

上班时间安排当天应在公司喝完的部分，预留一杯家庭杯到下班后。默认 2000 ml 大约是 7 次 300 ml 记录。午休和下班前停止提醒的时段内不发送提醒。其余上班时间，Windows 客户端显示桌面提醒，服务器按设置发送手机提醒。下班后若还没达标，按家庭杯提醒手机。免打扰开始后停止发送。

文案保持短句：标题「该喝水了」，正文「已喝 {已喝}/{目标} ml，建议 {建议} ml」；记下之后是「已记录 / 已记 {N} ml」。手机上点一次通知即可记账，不会打开浏览器。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
dotnet test ./WaterReminder.sln -c Release
dotnet publish ./WaterReminder.Server/WaterReminder.Server.csproj -c Release -o ./publish-server
dotnet run --project ./WaterReminder.Server -c Release -- --dry-run
```

Windows 客户端：

```powershell
dotnet publish .\WaterReminder.Client\WaterReminder.Client.csproj -c Release -o .\publish-client
```

部署、systemd、密钥和防火墙见 [DEPLOY.md](DEPLOY.md)。PushWard 的按钮行为见 [PUSHWARD.md](PUSHWARD.md)。

PushWard 令牌和客户端 API 令牌放在服务器的数据目录或环境变量里。不要把 `settings.json` 和令牌提交进仓库。

原来的 WPF 程序仍可用下面的命令在 Windows 上发布：

```powershell
dotnet publish .\WaterReminder\WaterReminder.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o .\publish
```

## 微信小程序

大家使用同一个小程序码。首次打开，普通用户输入自己的名字，点「开始记录」。以后使用同一个微信可回到原账本。普通用户可查看和修改自己的饮水记录，管理员可从电脑「记录」选择新注册的成员，修改他们的记录和提醒设置。

管理员在手机「设置 → 关联我的电脑账本」获取配对码，在已登录的桌面程序「记录 → 绑定我的微信」输入并确认，然后在手机点「打开管理员账本」。配对码 5 分钟内有效。绑定后，用同一微信再次打开小程序即可进入管理员账本。同一微信的旧账本会在待同步记录上传后逐笔合并，记录编号相同或同一天时间和饮水量相同的记录只计一次。

手机、Windows 和 PushWard 只有选中同一成员，并且连接同一服务端时，才同步这一本账。断网时手机会显示待同步，恢复后同一笔只补记一次。用微信开发者工具导入 `miniprogram` 目录即可预览，说明见 [miniprogram/README.md](miniprogram/README.md)。服务器安装和 Windows 的 `client.json` 见 [DEPLOY.md](DEPLOY.md)，那不是手机用户的注册步骤。

手机和桌面端都在设置里选择提醒方式，并且只使用这本账实际可用的渠道。微信和 PushWard 都可用时，可选「自动」「仅微信」「仅 PushWard」或「微信和 PushWard」。默认「自动」优先使用已绑定且还有订阅次数的微信；这次微信没发出去时，会改由已配置的 PushWard 发送。记一杯、修改饮水量，或点「授权一次提醒」，都会请求下一次授权。保存设置不会代为授权。今日页会显示剩余次数。关闭手机推送总开关会停用这两种手机提醒。

```bash
node ./miniprogram-tests/calculator.test.js
node --test ./miniprogram-tests/interface.test.js ./miniprogram-tests/server.test.js ./miniprogram-tests/wechat.test.js
```

## 许可

MIT，见 [LICENSE](LICENSE)。
