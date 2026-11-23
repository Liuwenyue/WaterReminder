<p align="center"><a href="README.md">简体中文</a> | <b>English</b></p>

# Water Reminder

Reminder rules run headless on Linux. The Windows client provides the floating widget, settings, and today's records, and shows desktop toasts during work hours. After work, the server uses the account's selected phone channels.

- **WaterReminder.Core** — schedule, cup sizes, PushWard, and records.
- **WaterReminder.Server** — small HTTP service on a VM, and the source of truth for settings and today's record. Default port **8787**. Times are always **Asia/Shanghai**.
- **WaterReminder.Client** — Windows tray, plus the original floating widget, settings, and today's records. It stores the server URL, the API token, an optional SSH tunnel, and local prefs such as the floating window and launch-at-logon. The tray menu **设置** writes reminder settings back to the server; the tray can also pause or resume reminders. It toasts 「该喝水了」 during work hours and stays quiet after work ends. A second launch activates the existing instance.
- **WaterReminder** — the original WPF app. It still runs on Windows by itself. Running it together with the server sends duplicate reminders.

Phone reminders support WeChat subscription messages and PushWard. The server defaults to `127.0.0.1:8787`. The Windows client can open an SSH local forward from the optional `tunnel` block in `client.json`. The sample uses placeholder values.

## Recent update

- WeChat reminders go to the released mini program unless a developer state is configured.
- Logging a cup asks for the next authorization. Today shows the remaining count and can renew it when it reaches zero.
- In Auto mode, a failed WeChat send falls back to PushWard for that reminder.
- API limits are per caller. A hidden floating window stops polling, and an unchanged window slows to about 30 seconds.


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

The schedule reserves one home cup (800 ml by default) for the evening. The default 3000 ml goal calls for about six work-cup reminders. Reminders pause during lunch and the configured window before work ends. During the rest of the workday, the Windows client shows desktop toasts and the server sends phone reminders according to the account settings. After work, phone reminders use the home cup until the goal is met or quiet hours begin.

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

## WeChat mini program

`miniprogram/` is the same drinking plan on a phone: today's progress, one cup, a custom amount, the log, and settings stay in WeChat local storage. Import the `miniprogram` folder with WeChat DevTools. See [miniprogram/README.md](miniprogram/README.md).

The server schedules phone reminders. Select Auto, WeChat, PushWard, or both in the shared settings. Auto uses WeChat when the member has a binding and subscription credits. If that send fails, the same reminder uses configured PushWard. The default target is the released mini program. Connect the mini program to the desktop account using its HTTPS server address and API token.

```bash
node ./miniprogram-tests/calculator.test.js
```

## License

MIT, see [LICENSE](LICENSE).
