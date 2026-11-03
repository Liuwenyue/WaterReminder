<p align="center"><a href="README.md">简体中文</a> | <b>English</b></p>

# Water Reminder (Windows)

A WPF/.NET 8 desktop app that reminds you to drink water. It has a floating progress window, a system tray icon, workday reminders, pausing, local JSON storage, and launch at startup.

## Features

- **Dynamic reminders**: Based on today's actual progress (amount left ÷ cup size), the remaining cups are spread evenly over the time left in the workday. If you're ahead, the next reminder moves later; if you're behind, reminders come more often (at least 20 minutes apart).
- **Lunch break and end-of-day cutoff**: No reminders during lunch, and none in the last N minutes before you leave work (30 by default), so the last drink lands well before the end of the day.
- **Compact mode**: Switch between the full window and a compact floating bar. Each mode remembers where you dragged it, and a window that ends up off-screen is pulled back into view.
- **Quick correction**: Click the progress number to type in today's total directly.
- Light/dark theme follows the system setting.

## Running

The published app is `publish\WaterReminder.exe` (a framework-dependent single file that needs the Windows Desktop Runtime 8.x; if 8.x is missing, a newer major version is used). Start it from the "喝水提醒" desktop shortcut or with `启动喝水提醒.cmd`. If the exe doesn't exist, the script publishes it with the bundled `dotnet\dotnet.exe` first.

## Building from source

You need Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The repository doesn't include the SDK or build output. `启动喝水提醒.cmd` uses `dotnet\dotnet.exe` from the repository when it's there, and otherwise falls back to `dotnet` on the system PATH.

```powershell
git clone https://github.com/Liuwenyue/WaterReminder.git
cd WaterReminder
dotnet test .\WaterReminder.sln -c Release
dotnet publish .\WaterReminder\WaterReminder.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o .\publish
.\publish\WaterReminder.exe
```

App data is stored in `%LocalAppData%\WaterReminder`. On first launch a setup wizard opens; after that, open Settings from the top-right of the floating window or from the system tray.

## Notifications

This first version uses Windows balloon notifications from the WinForms `NotifyIcon` as a fallback with no external dependencies, together with the floating window and a system sound. If Windows toast notifications with actions are needed later, the notification layer in `TrayIconService` can be swapped out without changing the reminder logic or data format.

## License

MIT, see [LICENSE](LICENSE).
