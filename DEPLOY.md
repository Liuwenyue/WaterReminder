# Deploy the headless server

The scheduler runs on a Linux VM (Ubuntu, 1 OCPU / 1 GB RAM is enough). Reminder times are always **Asia/Shanghai**, even if the VM clock is UTC. The Windows tray client and the iPhone (PushWard) both take reminders from this process. Do not run the old WPF scheduler at the same time.

PushWard is the only phone channel. Do not add ntfy or Pushover.

## Secrets

Keep these only on the server, in the environment or in the data directory. Do not put them in git, sample files, the Windows client, or this document.

| Secret | Environment variable | Or file under the data directory |
| --- | --- | --- |
| Client API token | `WATERREMINDER_API_TOKEN` | `api.token` |
| PushWard token | `WATERREMINDER_PUSHWARD_TOKEN` | `pushward.token` |

Environment wins over the file. A token found that way is not copied into `settings.json` when the service saves a pause. `settings.json` in the data directory may still contain `PushWardApiToken` for a hand-edited install; that file stays on the server and is gitignored.

The Windows client config (`%LocalAppData%\WaterReminderClient\client.json`) has only:

```json
{
  "serverUrl": "http://YOUR_VM:8787",
  "apiToken": "<the server API token>"
}
```

Copy `WaterReminder.Client/client.sample.json` and fill those two fields on the PC. It must not contain the PushWard token.

## Listen port

The process listens on **TCP 8787** (`WATERREMINDER_PORT`, bind `0.0.0.0` via `WATERREMINDER_BIND`). Check it with `GET /health` (no token). The other routes require `Authorization: Bearer <API token>`:

- `GET /api/status` — today, next reminder, and a desktop alert when one is due
- `GET /api/settings` — goal, cups, and the clock settings (no secrets)
- `PUT /api/settings` — save those fields. The running process reloads them immediately; do not restart systemd
- `POST /api/drinks` — log one work cup (`{"source":"desktop"}`)
- `GET /api/records` — today's events
- `PUT /api/records/total` — set today's total (`{"totalMl":1200}`); a lower total is an `adjust` event, the same total changes nothing
- `PUT /api/records/{id}` — edit one event's amount
- `DELETE /api/records/{id}` — delete one event
- `DELETE /api/records` — clear today
- `POST /api/desktop-ack` — the tray client has shown the toast (`{"id":"..."}`)

`--dry-run` prints the Shanghai schedule and exits. It does not open the port and does not call PushWard.

## Build

Install the ASP.NET Core 8 runtime (framework-dependent; lighter than a self-contained publish on a 1 GB VM):

```bash
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-8.0
dotnet publish WaterReminder.Server/WaterReminder.Server.csproj -c Release -o /opt/waterreminder
sudo useradd --system --home /var/lib/waterreminder --shell /usr/sbin/nologin waterreminder || true
sudo mkdir -p /var/lib/waterreminder /etc/waterreminder
sudo cp deploy/waterreminder.env.example /etc/waterreminder/waterreminder.env
sudo chown -R waterreminder:waterreminder /var/lib/waterreminder
sudo chmod 700 /var/lib/waterreminder /etc/waterreminder
```

Edit `/etc/waterreminder/waterreminder.env` on the server and set the two tokens. Reminder values live in `/var/lib/waterreminder/settings.json`. Change them from the Windows client (tray menu **设置**), not by editing that file and not by restarting the service. Leave the PushWard token out of `settings.json` when it is already in the environment. The settings window cannot show or change either token.

```bash
sudo cp deploy/waterreminder.service /etc/systemd/system/waterreminder.service
sudo systemctl daemon-reload
sudo systemctl enable --now waterreminder
curl -sS http://127.0.0.1:8787/health
```

The unit sets `TZ=Asia/Shanghai`. The process also converts from UTC with `Asia/Shanghai` itself, so a UTC host still fires at 08:30 Shanghai.

## Firewall

The Windows PC is not on the VM, so **TCP 8787 has to be allowed in both places**:

1. The host firewall (`iptables` or `nftables`).
2. The Oracle Cloud **security list** or **NSG** on the instance VNIC.

Add an allow rule only. Do not flush the input chain, do not set the default policy to DROP, and do not change the existing SSH allow rule. This repository intentionally has no firewall script.

Host example (one extra accept rule; SSH is left alone):

```bash
# iptables: insert a single accept. Do not run iptables -F and do not change port 22.
sudo iptables -I INPUT -p tcp --dport 8787 -j ACCEPT

# nftables: the table and chain names depend on the image. Add one accept rule;
# do not replace the whole ruleset.
sudo nft add rule inet filter input tcp dport 8787 accept
```

In the Oracle console, add an ingress rule for TCP 8787 on the subnet security list or the instance NSG. Prefer the office address of the Windows PC over `0.0.0.0/0`. Opening the cloud list without the host rule (or the host rule without the cloud list) still leaves the port unreachable.

## Windows client

On a Windows machine with the .NET 8 desktop runtime:

```powershell
dotnet publish .\WaterReminder.Client\WaterReminder.Client.csproj -c Release -o .\publish-client
```

Put `client.json` next to the exe or at `%LocalAppData%\WaterReminderClient\client.json`. On startup the original desktop floating widget is shown (today's amount, progress, and **喝一杯**). Click the amount (tooltip 「点击修正今日已喝量」) to set today's total, including a lower total or 0. **记录** opens the original records window to edit or delete today's events. Both write to the server immediately. The widget reads `GET /api/status` and logs new drinks with `POST /api/drinks`. It does not run its own scheduler. Hide it from the widget; open it again from the tray item **打开悬浮窗**, or with a left click on the tray icon. The settings checkbox **显示桌面悬浮窗** (default on) is stored in `client.json` together with the window position. That file still has only the server URL, the API token, and this display preference.

During work hours the tray shows a toast (`该喝水了` / `已喝 {已喝}/{目标} ml，建议 {建议} ml`) and **记录一杯** logs the work cup. After work end it does not show desktop toasts. Quiet hours stay silent. Phone recording is the PushWard **喝水了** action (no URL, no browser); the server polls `GET /notifications/answers` and dedupes `notification_id` whether it arrives as a string or a number.

## Settings

Open the tray icon and choose **设置**. The window is the original desktop settings screen. It loads `GET /api/settings` and saves with `PUT /api/settings`, using the API token already in `client.json` (sent as a header, not placed in the URL). Editable fields: daily goal, work cup, home cup, work start, work end, lunch on/off and times, minutes without a drink before work ends, minimum gap, quiet start, weekdays, and whether phone push is enabled. Saving writes `/var/lib/waterreminder/settings.json` and the scheduler uses the new values immediately. The window has no token fields. A request that includes a token is rejected and does not change the file.

## 部署说明（简体中文）

服务跑在 Linux 上，提醒时间固定为 **Asia/Shanghai**。默认监听 **8787**。PushWard 令牌和客户端 API 令牌只放在服务器的环境变量或数据目录（`/var/lib/waterreminder`），不要写入仓库或 Windows 客户端。客户端只配置服务器地址和 API 令牌。

自定义数值在 Windows 托盘菜单「设置」里改，其中「显示桌面悬浮窗」默认打开。启动后桌面上会看到原来的悬浮窗（今日进度和喝一杯）。点击已喝数字可以改今天的总量，调低或改成 0 也可以；「记录」打开原来的记录窗口，能改或删每一条。这些都马上写到服务器。数据来自服务器，不在本机计时。关掉后可从托盘「打开悬浮窗」再打开。悬浮窗开关和位置写在本机 `client.json`，不是密钥。窗口沿用原来的桌面设置界面，读取并保存到服务器的 `settings.json`（`PUT /api/settings`），保存后马上生效，不用重启 systemd。可改：每日目标、工作杯、家庭杯、上下班、午休、下班前不喝水、最小间隔、免打扰、工作日，以及是否开启手机推送。窗口里不能看也不能改任何令牌。

必须同时在主机防火墙（iptables 或 nft）和 Oracle Cloud 的安全列表（security list）或 NSG 里放行 TCP 8787。只新增允许规则，不要清空现有规则，也不要改 SSH。仓库里没有防火墙脚本。systemd 示例见 `deploy/waterreminder.service`。
