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

首版使用 WinForms `NotifyIcon` 的 Windows 气泡通知作为无外部依赖的降级实现，并配合悬浮窗和系统提示音。后续若需要 Windows Toast 的通知动作，可在不改变提醒计算和数据结构的前提下替换 `TrayIconService` 的通知适配层。

## 许可

MIT，见 [LICENSE](LICENSE)。
