<p align="center"><a href="README.md">简体中文</a> | <b>English</b></p>

# Water Reminder

Reminder rules run headless on Linux. The Windows client keeps the original floating widget, settings, and today's records, and shows desktop toasts during work hours. After work, reminders go to the phone through PushWard.

- **WaterReminder.Core** — schedule, cup sizes, PushWard, and records.
- **WaterReminder.Server** — small HTTP service on a VM, and the source of truth for settings and today's record. Default port **8787**. Times are always **Asia/Shanghai**.
- **WaterReminder.Client** — Windows tray, plus the original floating widget, settings, and today's records. It stores the server URL, the API token, and whether the widget is shown. The tray menu **设置** opens the desktop settings window and writes the goal, cups, schedule, and workdays back to the server. It toasts 「该喝水了」 during work hours and stays quiet after work ends.
- **WaterReminder** — the original WPF app. It still runs on Windows by itself. Running it together with the server sends duplicate reminders.

Phone reminders use **PushWard**.

## Reminder rules (all configurable)

| Setting | Default |
| --- | --- |
| Daily goal | 3000 ml |
| Work | 08:30–17:30 |
| Lunch | 11:50–13:30 |
| No drinks before work ends | 30 minutes |
| Quiet hours | from 23:00 until the next workday |
| Work cup / home cup | 370 ml / 800 ml |
| Minimum gap | 20 minutes |

Nothing is sent during lunch or in the no-drink window before work ends. Through the rest of the workday the server sends PushWard and the Windows client shows a desktop toast. After work ends and before quiet hours, reminders go to the phone and the desktop stays quiet. After quiet hours, nothing is sent.

Copy stays short: title 「该喝水了」, body 「已喝 {已喝}/{目标} ml，建议 {建议} ml」, confirmation 「已记录 / 已记 {N} ml」. On the phone, one tap on the notification records the drink and does not open a browser.

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet test ./WaterReminder.sln -c Release
dotnet publish ./WaterReminder.Server/WaterReminder.Server.csproj -c Release -o ./publish-server
dotnet run --project ./WaterReminder.Server -c Release -- --dry-run
```

Windows client:

```powershell
dotnet publish .\WaterReminder.Client\WaterReminder.Client.csproj -c Release -o .\publish-client
```

Deployment, the systemd unit, secrets, and the firewall are in [DEPLOY.md](DEPLOY.md). PushWard button behavior is in [PUSHWARD.md](PUSHWARD.md).

The PushWard token and the client API token stay in the server data directory or in environment variables. Do not commit `settings.json` or tokens.

The original WPF app can still be published on Windows:

```powershell
dotnet publish .\WaterReminder\WaterReminder.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o .\publish
```

## License

MIT, see [LICENSE](LICENSE).
