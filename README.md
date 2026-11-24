<p align="center"><b>简体中文</b> | <a href="README.en.md">English</a></p>

# 喝水提醒

提醒日程在 Linux 上无界面运行。Windows 客户端提供悬浮窗、设置和今日记录，并在上班时间弹出桌面提醒。下班后，服务器按账本设置发送手机提醒。

- **WaterReminder.Core**：日程、杯量、PushWard 和记录。
- **WaterReminder.Server**：在虚拟机上运行的轻量 HTTP 服务，是设置和当日记录的唯一来源。默认端口 **8787**。时间固定为 **Asia/Shanghai**。
- **WaterReminder.Client**：Windows 托盘，以及原来的悬浮窗、设置和今日记录。本机保存服务器地址、API 令牌、可选 SSH 隧道，以及悬浮窗/开机自启等本地偏好。托盘菜单「设置」打开桌面设置窗口，把目标、杯量、时间和工作日写回服务器；也可暂停/恢复提醒。上班时间弹出「该喝水了」，下班后不再弹窗。单实例：再次启动会唤醒已有悬浮窗。
- **WaterReminder**：原来的 WPF 程序，仍可在 Windows 上单独使用。与服务器同时运行时会重复提醒。

手机提醒走 PushWard。服务器默认只监听 `127.0.0.1:8787`。Windows 客户端可通过 `client.json` 里的 `tunnel` 自建 SSH 本地转发（示例使用占位符）。

## 最近更新

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

## 手机提醒

手机提醒走 PushWard：家人在 PushWard App 里创建集成，把 Token 交给管理员录入，提醒就会推送到手机上，点一次通知即可记一杯。家人加入走桌面访问码：管理员在「记录 → 添加成员」生成访问码，家人用 Windows 客户端连接同一服务。

## 许可

MIT，见 [LICENSE](LICENSE)。
