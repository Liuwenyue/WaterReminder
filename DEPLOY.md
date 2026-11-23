# Deploy the headless server

The scheduler runs on a Linux VM (Ubuntu, 1 OCPU / 1 GB RAM is enough). Reminder times are always **Asia/Shanghai**, even if the VM clock is UTC. The Windows tray client and the iPhone (PushWard) both take reminders from this process. Do not run the old WPF scheduler at the same time.

Phone delivery supports WeChat subscription messages and PushWard. Each account stores its channel preference on the server.

## Secrets

Keep these only on the server, in the environment or in the data directory. Do not put them in git, sample files, the Windows client, or this document.

| Secret | Environment variable | Or file under the data directory |
| --- | --- | --- |
| Client API token | `WATERREMINDER_API_TOKEN` | `api.token` |
| PushWard token | `WATERREMINDER_PUSHWARD_TOKEN` | `pushward.token` |
| WeChat AppSecret | `WATERREMINDER_WECHAT_APP_SECRET` | `wechat.json` 的 `appSecret` |
| WeChat template ID | `WATERREMINDER_WECHAT_TEMPLATE_ID` | `wechat.json` 的 `templateId` |


微信服务通知需要 AppSecret、模板 ID、成员绑定和可用的订阅次数。提醒渠道按成员配置，自动模式在微信未就绪时使用已配置的 PushWard。`wechat.json` 可以设置 `miniprogramState`（不写时发给正式版 `formal`，开发者工具里才用 `developer`）和 `fields`。默认字段 `thing1`、`time2`、`thing3` 分别对应「该喝水了」、提醒时间和「建议喝{suggested}毫升」。字段名必须和公众平台模板一致。

Environment wins over the file. A token found that way is not copied into `settings.json` when the service saves a pause. `settings.json` in the data directory may still contain `PushWardApiToken` for a hand-edited install; that file stays on the server and is gitignored.

The Windows client config (`%LocalAppData%\WaterReminderClient\client.json`) holds the API token and optional SSH tunnel settings. Copy `WaterReminder.Client/client.sample.json`. It must not contain the PushWard token or a real host/key path in git:

```json
{
  "serverUrl": "http://127.0.0.1:8787",
  "apiToken": "<the server API token>",
  "floatingWindow": true,
  "autoStart": false,
  "tunnel": {
    "enabled": true,
    "sshPath": "ssh",
    "host": "YOUR_VM_HOST",
    "user": "YOUR_SSH_USER",
    "identityFile": "C:\\\\Users\\\\YOU\\\\.ssh\\\\id_ed25519",
    "remotePort": 8787,
    "localPort": 8787,
    "readyTimeoutSeconds": 15
  }
}
```

With `tunnel.enabled`, the client starts `ssh -N -L` when the local port is not already open. After starting ssh it polls the local port about every 250ms for up to `readyTimeoutSeconds` (default 15). If the ssh process exits during that wait, it fails immediately and retries with backoff; the same backoff applies when `/health` fails later. Only the SSH process this client started is stopped. Status shows 「连接服务器中…」 while connecting (no popup spam).

## Listen port

The process listens on **TCP 8787** and defaults to **`127.0.0.1`** (`WATERREMINDER_BIND`). Prefer keeping it on loopback and reaching it from Windows through the client SSH tunnel. Override the bind only if you intentionally expose the port. Check it with `GET /health` (no token). The other routes require `Authorization: Bearer <API token>`:

- `GET /api/status` — today, next reminder, and a desktop alert when one is due
- `GET /api/settings` — goal, cups, and the clock settings (no secrets)
- `PUT /api/settings` — save those fields (including pause). The running process reloads them immediately; do not restart systemd
- `POST /api/drinks` — log one work cup (`{"source":"desktop"}`)
- `GET /api/records` — today's events
- `GET /api/records?date=yyyy-MM-dd` — one day (past days are read-only for the client)
- `GET /api/history?days=30` — recent day totals
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

With the default **127.0.0.1** bind and the Windows SSH tunnel, **TCP 8787 does not need to be open on the public internet**. Keep SSH (22) open as usual. If you intentionally bind `0.0.0.0`, then TCP 8787 must be allowed in both places:

1. The host firewall (`iptables` or `nftables`).
2. The Oracle Cloud **security list** or **NSG** on the instance VNIC.

Add an allow rule only. Do not flush the input chain, do not set the default policy to DROP, and do not change the existing SSH allow rule. This repository intentionally has no firewall script.

Host example (only if you expose 8787; SSH is left alone):

```bash
# iptables: insert a single accept. Do not run iptables -F and do not change port 22.
sudo iptables -I INPUT -p tcp --dport 8787 -j ACCEPT

# nftables: the table and chain names depend on the image. Add one accept rule;
# do not replace the whole ruleset.
sudo nft add rule inet filter input tcp dport 8787 accept
```

In the Oracle console, add an ingress rule for TCP 8787 only when the process is bound beyond loopback. Prefer the office address of the Windows PC over `0.0.0.0/0`.

## Windows client

On a Windows machine with the .NET 8 desktop runtime:

```powershell
dotnet publish .\WaterReminder.Client\WaterReminder.Client.csproj -c Release -o .\publish-client
```

Put `client.json` next to the exe or at `%LocalAppData%\WaterReminderClient\client.json`. On startup the original desktop floating widget is shown (today's amount, progress, and **喝一杯**). Click the amount (tooltip 「点击修正今日已喝量」) to set today's total, including a lower total or 0. **记录** opens the records window (today editable; past days read-only). Both write to the server immediately. The widget reads `GET /api/status` and logs new drinks with `POST /api/drinks`. It does not run its own scheduler. Hide it from the widget; open it again from the tray item **打开悬浮窗**, or with a left click on the tray icon. A second launch activates the existing instance. Tray items **暂停 30 分钟** / **今日暂停** / **恢复提醒** call `PUT /api/settings`. Local preferences in `client.json`: floating window, auto-start, shared window position, and optional `tunnel`.

During work hours the tray shows a toast (`该喝水了` / `已喝 {已喝}/{目标} ml，建议 {建议} ml`) and **记录一杯** logs the work cup. After work end it does not show desktop toasts. Quiet hours stay silent. PushWard reminders offer **已喝水 · N ml** to log the quantity shown and **修改当前已喝水量** to set the day's total. The server resumes the answer stream from its saved cursor and applies each tracked notification only once.

## Settings

Open the tray icon and choose **设置**. The window is the original desktop settings screen. It loads `GET /api/settings` and saves with `PUT /api/settings`, using the API token already in `client.json` (sent as a header, not placed in the URL). Editable fields: daily goal, work cup, home cup, work start, work end, lunch on/off and times, minutes without a drink before work ends, minimum gap, quiet start, weekdays, and whether phone push is enabled. Saving writes `/var/lib/waterreminder/settings.json` and the scheduler uses the new values immediately. The window has no token fields. A request that includes a token is rejected and does not change the file.

## 部署说明（简体中文）

服务跑在 Linux 上，提醒时间固定为 **Asia/Shanghai**。默认监听 **127.0.0.1:8787**。PushWard 令牌和客户端 API 令牌只放在服务器的环境变量或数据目录（`/var/lib/waterreminder`），不要写入仓库或 Windows 客户端。Windows 客户端用 SSH 隧道连到本机 `127.0.0.1:8787`，`client.json` 里用占位符配置 `tunnel`（不要提交真实 host/密钥）。

自定义数值在 Windows 托盘菜单「设置」里改，其中「显示桌面悬浮窗」和「开机自动启动」是本机偏好。启动后桌面上会看到原来的悬浮窗。点击已喝数字可以改今天的总量；「记录」可看历史天（只读）和编辑今天。托盘可暂停 30 分钟 / 今日暂停 / 恢复。数据来自服务器，不在本机计时。窗口位置在精简/完整模式间共用左上角锚点。窗口沿用原来的桌面设置界面，读取并保存到服务器的 `settings.json`（`PUT /api/settings`）。窗口里不能看也不能改任何令牌。小程序没有单独的注册步骤。已关联到某个成员且服务端一致时，小程序仍用既有的 `GET/PUT /api/settings` 和 `POST /api/sync/operations` 读写该成员账本。手机访问不到默认的 `127.0.0.1`，正式环境需要已备案的 HTTPS 域名，并把它配进小程序的 request 合法域名。用户如何建个人账、如何关联电脑，见 [README.md](README.md)，不要把这里的令牌或 `client.json` 当成注册。

默认不对外暴露 8787；若改绑到 `0.0.0.0`，才需要在主机防火墙和云安全列表里放行。只新增允许规则，不要清空现有规则，也不要改 SSH。systemd 示例见 `deploy/waterreminder.service`。

## 家庭版使用与升级

- 同时升级服务器和 Windows 客户端。服务器继续使用原数据目录，原 API 密钥仍是管理员密钥，原有个人记录继续归「我」。升级前备份整个数据目录。家庭账户在 `family.json`，成员数据在 `members/`，请一起保留。
- Windows 客户端运行需要 .NET 8 桌面运行时。展开专业版，点击「记录」查看最近 30 天内有记录的日期，也可用「前一天」「后一天」查更早的日期。管理员从成员下拉框切换查看，其他成员的详情只读。
- 访问码和 `client.json` 只用于安装 Windows 客户端，不是小程序注册。管理员在「记录 → 添加成员」输入名称并生成访问码后，把 Windows 客户端交给家人。家人复制 `client.sample.json` 为 `client.json`，在 `serverUrl` 填同一个可访问的服务地址，在 `apiToken` 填自己的访问码。跨设备接入应使用 HTTPS 或为该成员单独配置的 SSH 隧道。停用后旧访问码失效，历史仍可查看。
- 管理员在「记录 → 家庭成员」选中家人，再点「成员设置」。可调整每日目标、工作杯和家庭杯、上下班、午休、免打扰开始时间、提醒间隔、提醒日期和手机推送。开机启动、悬浮窗及窗口位置由各台电脑自行设置。
- 手机用户扫码后取名注册。管理员使用手机「设置 → 关联我的电脑账本」生成配对码，再到电脑「记录 → 绑定我的微信」输入确认。配对码由同一后端签发和确认，手机和电脑须连接同一服务。详见 [README.md](README.md)。
- 管理员若要给某成员发 PushWard，在创建成员时填写该成员的集成密钥，或在「成员设置」里填写新密钥并开启手机推送。密钥留空会保留当前值。每人的集成应只投递到自己的设备。
- 在 iPhone 上展开提醒，点「已喝水 · N ml」记录按钮显示的水量，或点「修改当前已喝水量」输入当天累计水量。输入 `1200` 会将当天总量设为 1200 ml。同一条通知只能选择一次操作。请使用支持通知文本回复的 PushWard 版本。
- Windows 客户端继续可从旧服务读取个人逐日记录。家庭成员和手机文本回复需要新版服务器。
