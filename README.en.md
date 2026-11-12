<p align="center"><a href="README.md">简体中文</a> | <b>English</b></p>

# Water Reminder

The reminder rules run headless on Linux. A thin Windows tray app shows desktop toasts during work hours. The existing WPF app is not ported to Linux.

- **WaterReminder.Core** — schedule, cup sizes, PushWard, and records. No WPF.
- **WaterReminder.Server** — small HTTP service and the source of truth for settings and today's record. Default port **8787**. Times are always **Asia/Shanghai**.
- **WaterReminder.Client** — Windows tray plus the original desktop floating widget. It stores the server URL, the API token, and whether the widget is shown. The tray menu **设置** opens the original desktop settings window and writes the goal, cups, and schedule back to the server. It toasts during work hours and stays quiet after work ends.
- **WaterReminder** — the original WPF window. It still runs on Windows by itself. Do not run it together with the server or reminders fire twice.

The only phone channel is **PushWard** (a Notification Answer, action id `drink`, no url, no foreground). ntfy and Pushover were removed and stay removed.

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

In a work-hour reminder window the server sends PushWard and the Windows client shows a toast. After `WorkEnd` and before `QuietStart` the phone is notified and the desktop stays silent. After quiet hours nothing is sent.

Copy stays short: title 「该喝水了」, body 「已喝 {已喝}/{目标} ml，建议 {建议} ml」, confirmation 「已记录 / 已记 {N} ml」. A phone records a drink with one tap on the notification action. The same `notification_id` (string or number) is stored once.

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

Deploy notes, the systemd unit, secrets, and the firewall (host iptables/nft **and** the Oracle Cloud security list or NSG) are in [DEPLOY.md](DEPLOY.md). PushWard button behavior is in [PUSHWARD.md](PUSHWARD.md).

The PushWard token and the client API token stay in the server data directory or in environment variables. Do not commit `settings.json`, tokens, `client.json`, or backup folders. The Windows `client.json` has `serverUrl` and `apiToken` only.

The original WPF app can still be published on Windows:

```powershell
dotnet publish .\WaterReminder\WaterReminder.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o .\publish
```

## License

MIT, see [LICENSE](LICENSE).
