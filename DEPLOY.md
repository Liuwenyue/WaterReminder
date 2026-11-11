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
- `POST /api/drinks` — log one work cup (`{"source":"desktop"}`)
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

Edit `/etc/waterreminder/waterreminder.env` on the server and set the two tokens. Optional `settings.json` in `/var/lib/waterreminder` overrides the defaults (goal 3000 ml, work 08:30–17:30, lunch 11:50–13:30, quiet 23:00, work cup 370 ml, home cup 800 ml, minimum gap 20 minutes, no drinks in the last 30 minutes before work ends). Leave the PushWard token out of that file when it is already in the environment.

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

Put `client.json` next to the exe or at `%LocalAppData%\WaterReminderClient\client.json`. During work hours the tray shows a toast (`该喝水了` / `已喝 {已喝}/{目标} ml，建议 {建议} ml`) and **记录一杯** logs the work cup. After work end it does not show desktop toasts. Quiet hours stay silent. Phone recording is the PushWard **喝水了** action (no URL, no browser); the server polls `GET /notifications/answers` and dedupes `notification_id` whether it arrives as a string or a number.

## 部署说明（简体中文）

服务跑在 Linux 上，提醒时间固定为 **Asia/Shanghai**。默认监听 **8787**。PushWard 令牌和客户端 API 令牌只放在服务器的环境变量或数据目录（`/var/lib/waterreminder`），不要写入仓库或 Windows 客户端。客户端只配置服务器地址和 API 令牌。

必须同时在主机防火墙（iptables 或 nft）和 Oracle Cloud 的安全列表（security list）或 NSG 里放行 TCP 8787。只新增允许规则，不要清空现有规则，也不要改 SSH。仓库里没有防火墙脚本。systemd 示例见 `deploy/waterreminder.service`。
