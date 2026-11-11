<p align="center"><b>简体中文</b> | <a href="README.en.md">English</a></p>

# 喝水提醒

提醒规则在 Linux 上无界面运行，Windows 只负责上班时间的桌面气泡。不要把原来的 WPF 程序搬到 Linux 上跑。

- **WaterReminder.Core**：日程、杯量、PushWard、记录。没有 WPF。
- **WaterReminder.Server**：在虚拟机上跑的轻量 HTTP 服务，是设置和当日记录的唯一来源。默认端口 **8787**。时间固定为 **Asia/Shanghai**。
- **WaterReminder.Client**：Windows 托盘。只保存服务器地址和 API 令牌，上班时间弹「该喝水了」，下班后不弹窗。
- **WaterReminder**：原来的 WPF 悬浮窗。可以继续在本机单独使用；和服务器同时开会重复提醒。

手机通道只有 **PushWard**（Notification Answer，动作 `drink`，没有 url、没有 foreground）。ntfy 和 Pushover 已删除，不要加回来。

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

上班的提醒窗口里，服务器给手机发 PushWard，Windows 客户端同时弹桌面气泡。`WorkEnd` 之后到 `QuietStart` 之前只推手机，不弹桌面。免打扰之后保持安静。

文案保持短句：标题「该喝水了」，正文「已喝 {已喝}/{目标} ml，建议 {建议} ml」；记下之后是「已记录 / 已记 {N} ml」。手机上点一次通知动作即可记账，不会打开浏览器。同一条 `notification_id`（字符串或数字）只记一次。

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

服务器上的 PushWard 令牌和客户端 API 令牌只放在数据目录或环境变量里。不要把 `settings.json`、令牌、`client.json` 或备份目录提交进仓库。Windows 客户端的 `client.json` 只有 `serverUrl` 和 `apiToken`。

原来的 WPF 程序仍可用下面的命令在 Windows 上发布：

```powershell
dotnet publish .\WaterReminder\WaterReminder.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o .\publish
```

## 许可

MIT，见 [LICENSE](LICENSE)。
