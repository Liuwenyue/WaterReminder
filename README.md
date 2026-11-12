<p align="center"><b>简体中文</b> | <a href="README.en.md">English</a></p>

# 喝水提醒

提醒日程在 Linux 上无界面运行。Windows 客户端保留原来的悬浮窗、设置和今日记录，并在上班时间弹出桌面提醒；下班后改为向手机发送 PushWard 通知。

- **WaterReminder.Core**：日程、杯量、PushWard 和记录。
- **WaterReminder.Server**：在虚拟机上运行的轻量 HTTP 服务，是设置和当日记录的唯一来源。默认端口 **8787**。时间固定为 **Asia/Shanghai**。
- **WaterReminder.Client**：Windows 托盘，以及原来的悬浮窗、设置和今日记录。本机保存服务器地址、API 令牌，以及是否显示悬浮窗。托盘菜单「设置」打开桌面设置窗口，把目标、杯量、时间和工作日写回服务器。上班时间弹出「该喝水了」，下班后不再弹窗。
- **WaterReminder**：原来的 WPF 程序，仍可在 Windows 上单独使用。与服务器同时运行时会重复提醒。

手机提醒使用 **PushWard**。

## 提醒规则（都可以改）

| 项 | 默认 |
| --- | --- |
| 每日目标 | 3000 ml |
| 上班 | 08:30–17:30 |
| 午休 | 11:50–13:30 |
| 下班前不喝水 | 30 分钟 |
| 免打扰 | 23:00 起，直到下一个工作日 |
| 工作杯 / 家庭杯 | 370 ml / 800 ml |
| 最小间隔 | 20 分钟 |

午休期间，以及下班前的这段时间里，不发送提醒。其余上班时间，服务器给手机发 PushWard，Windows 客户端同时弹出桌面提醒。下班后、免打扰开始前，提醒发到手机，桌面保持安静。免打扰开始后不再发送。

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

## 许可

MIT，见 [LICENSE](LICENSE)。
