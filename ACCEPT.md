# 验收说明（PushWard 唯一手机通道 + Notification Answers）

> 这份记录对应「只保留 PushWard」那一次改动。现在的运行方式是 Linux 上的无界面服务加上 Windows 托盘客户端，说明见 [README.md](README.md) 和 [DEPLOY.md](DEPLOY.md)。



## 改了什么

- 手机推送只保留 **PushWard**：删除 ntfy 与 Pushover 的服务、通道枚举、设置字段、
  界面和测试，不再保留“旧版可选”死代码。
- 设置只剩 `MobilePushEnabled` 和 `PushWardApiToken`（`hlk_…`）。
  旧 `settings.json` 里的 `MobilePushChannel`、`Ntfy*`、`Pushover*` 字段在读取时被
  System.Text.Json 忽略，下次保存时不再写回；不会因为旧键导致加载失败。
- `PushWardMessageBuilder` 的动作只剩
  `{ "id":"drink", "title":"喝水了", "icon":"drop.fill" }`：
  **没有 `url`、没有 `foreground`、没有 method/body**，所以点按只会产生
  Notification Answer，不会打开 App 或 Safari。
- 提醒 POST `https://api.pushward.app/notifications`，
  `Authorization: Bearer hlk_…`，`collapse_id=water-evening`；
  设置页测试使用 `collapse_id=water-test` 和同一个动作。
- `PushWardService` 新增 Answers 轮询：
  1. `GET /notifications/answers` 取 `cursor`；
  2. 循环 `GET /notifications/answers?after={cursor}&wait=25`；
  3. 对 `action_id=="drink"` 的条目按 `notification_id` 去重，记录家庭杯，
     来源 `pushward`，再发「已记录 / 已记 N ml」确认（无 actions）；
  4. 每个响应都推进 `cursor`；失败只做 2–30 秒退避重试。
- 轮询生命周期：启动时 `Start()`，保存设置时 `Restart()`；条件为
  `MobilePushEnabled && PushWardApiToken` 非空，否则停止。
- 记账来源标签只保留 `pushward` →「手机 Pushward」。
- 设置页：PushWard Token 使用 PasswordBox；删除 Pushover/ntfy 输入框、通道下拉框和
  「高级」折叠区。测试发送成功只看 PushWard API 是否返回 2xx（正常 201）。
- 文档：`PUSHWARD.md` 是唯一的手机推送指南；README/ACCEPT 已改为
  **无需 ntfy / Pushover**，并说明 iOS 需长按或下拉通知才能看到按钮、
  免费约 500 条/月、付费约 $24/年。

## 测试与编译

```powershell
dotnet test .\WaterReminder.sln -c Release
```

本次执行环境是 Linux，没有 `Microsoft.WindowsDesktop.App` 运行时，WPF testhost 无法启动。
完整解决方案仍可编译：

```bash
dotnet build WaterReminder.sln -c Release -p:EnableWindowsTargeting=true
# Build succeeded. 0 Warning(s), 0 Error(s)
```

为运行全部纯逻辑测试，把不依赖 WPF 的源码和全部测试文件链接到一个临时 `net8.0`
测试工程（fake `HttpMessageHandler`，无网络、无真实密钥）：

**139 个测试通过，0 失败，0 跳过。**

临时工程位于 `/tmp`，不属于交付内容；Windows 上请按上面的原命令再跑一次。

新增/重写的离线测试：

- `PushWardServiceTests`
  - 提醒 JSON 的动作只有 `id/title/icon`，没有 `url`/`foreground`/`method`/`body`；
  - `Authorization: Bearer` 头，令牌不进入正文；
  - 201 成功、4xx/5xx 失败、网络异常静默；
  - 空令牌不发请求；`IsConfigured` 只看 Token；
  - Answers 解析：`cursor` + `items`，忽略缺失 id 的条目，接受数字游标；
  - 轮询：bootstrap 后收到 `drink` → 只记一次并发送确认；
    同一 `notification_id` 重复出现 → 仍只记一次；非 `drink` 动作不记账；
    请求带 `after=…&wait=25`。
- `PersistenceAndPolicyTests`
  - `ReminderService` 仅在「手机推送开启 + PushWard 已配置」时发布；
  - 记录来源标签为「手机 Pushward」。
- `EveningPushTests`
  - 原有晚间计划/提醒决策测试保留；
  - 旧 JSON（含已删除的通道/ntfy/Pushover 键）可正常加载，保存后旧键消失。

## 真机测试步骤

1. iPhone 从 App Store 安装 PushWard，创建集成并复制 `hlk_…` Token。
2. 程序 → 设置 →「手机推送（PushWard）」：勾选开启，粘贴 Token，先点「保存」启动
   Answers 轮询；再打开设置点「发送测试」。
   - 预期：iPhone 收到一条 PushWard 测试通知。
   - 通知上可能只显示标题/正文；**长按通知或下拉通知中心**后应看到「喝水了」。
3. 点「喝水了」：
   - 预期：不打开 PushWard，也不打开 Safari；电脑记录 `+800 ml`（来源「手机 Pushward」）；
     iPhone 再收到「已记录 / 已记 800 ml」确认（无按钮）。
4. 再点一次同一条通知的「喝水了」（或断网重连触发重放）：
   - 预期：不会重复记账（按 `notification_id` 去重）。
5. 把「下班时间」调到一分钟后，等待晚间提醒：
   - 预期：电脑不弹窗、不响铃；iPhone 只收到**一条**提醒「该喝水了 / 建议补充 N ml」。
6. 等到「免打扰开始」之后，确认不再推送。

## 未验证（需要真实环境）

- 真实 `hlk_` Token 调用 PushWard API（测试只使用 fake handler，无网络）。
- PushWard `/notifications/answers` 的实际响应字段是否与实现的
  `{ "items": [ { "notification_id", "action_id" } ], "cursor" }` 完全一致；
  解析器同时接受 `answers` 数组和数字游标，但真实服务仍需联调确认。
- iPhone 上 Notification Answer 的按钮是否确实不跳转 App/Safari。
- iOS 长按/下拉显示按钮的实际交互。
- PushWard 在目标 App Store 区域是否可安装，以及免费/付费额度的最新官方价格。
- WPF 设置页的实际视觉效果；本环境只能编译，无法启动 Windows UI。
