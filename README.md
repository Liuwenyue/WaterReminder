<p align="center"><b>简体中文</b> | <a href="README.en.md">English</a></p>

# 喝水提醒（Windows）

这是一个 WPF/.NET 8 桌面喝水提醒程序，支持悬浮进度窗、系统托盘、工作日提醒、暂停提醒、本地 JSON 持久化和开机自启。

## 功能

- **动态提醒**：根据今日实际进度（剩余量 ÷ 杯容量）把剩余杯数均匀分布到剩余有效时间里；喝得超前就往后推，落后就缩短间隔（最小 20 分钟）。
- **午休与下班前截止**：午休期间不提醒；下班前 N 分钟（默认 30）不再提醒，最后一次提醒尽量远离下班。
- **精简模式**：可在完整窗口和精简悬浮条之间切换，两种模式各自记住拖动后的位置，超出屏幕时自动拉回。
- **快速修正**：点击进度数字可直接输入今天已喝的总量。
- 浅色/深色主题跟随系统。

## 运行

已发布的程序位于 `publish\WaterReminder.exe`（框架依赖单文件，需要 Windows Desktop Runtime 8.x；缺失时会自动使用更高主版本）。双击桌面“喝水提醒”快捷方式或 `启动喝水提醒.cmd` 即可启动；若 exe 不存在，脚本会用仓库内的 `dotnet\dotnet.exe` 自动发布。

## 从源码构建

需要 Windows 和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。仓库不包含 SDK 和发布产物；`启动喝水提醒.cmd` 会优先使用仓库内的 `dotnet\dotnet.exe`，没有时使用系统 PATH 中的 `dotnet`。

```powershell
git clone https://github.com/Liuwenyue/WaterReminder.git
cd WaterReminder
dotnet test .\WaterReminder.sln -c Release
dotnet publish .\WaterReminder\WaterReminder.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o .\publish
.\publish\WaterReminder.exe
```

程序数据保存在 `%LocalAppData%\WaterReminder`。首次启动会显示设置向导；后续可以从悬浮窗右上角或系统托盘进入设置。

## 当前通知实现

工作时段使用 WinForms `NotifyIcon` 的 Windows 气泡通知，并配合悬浮窗和系统提示音。下班后（默认 18:00 到 23:00）不再弹窗、不再响铃，改为通过 **PushWard** 推送到 iPhone。PushWard 是唯一的手机通道，**不需要 ntfy，也不需要 Pushover**：

1. iPhone 从 App Store 安装 **PushWard**，在 App 内创建集成密钥（`hlk_` 开头）。
2. 打开本程序 → 设置 →「手机推送（PushWard）」，粘贴 API Token 并保存（轮询随即启动）；之后可再打开设置点「发送测试」。

提醒标题为「该喝水了」，正文为「建议补充 N ml」，并带一个自定义按钮 **「喝水了」**。这个按钮没有 `url`、没有 `foreground`，点按只会被 PushWard 记录为 Notification Answer，**不会打开 PushWard App，也不会打开 Safari**。电脑轮询 `GET /notifications/answers` 取回 Answer 后记录一杯家庭杯（默认 800 ml，来源「手机 Pushward」），再发一条「已记录 / 已记 N ml」确认（无按钮）。

**iOS 提示**：通知上的「喝水了」按钮通常需要**长按通知**或在通知中心**下拉/展开**后才能看到。PushWard 免费额度约每月 500 条推送，付费约 $24/年（以官方价格为准），本程序日常使用远低于免费额度。详见 [PUSHWARD.md](PUSHWARD.md)。

电脑需要保持开机并运行本程序：提醒由电脑发出，Answer 也由电脑轮询取回。PushWard Token 只保存在 `%LocalAppData%\WaterReminder\settings.json`，不会写入仓库。23:00 到次日上班前以及周末完全静音。

## 许可

MIT，见 [LICENSE](LICENSE)。
