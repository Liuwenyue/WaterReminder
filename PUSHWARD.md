# PushWard 手机推送（唯一手机通道）

> 本文件只说明用法。PushWard API Token 只放在服务器的环境变量
> `WATERREMINDER_PUSHWARD_TOKEN` 或数据目录的 `pushward.token`
> （旧版 WPF 仍可读本机 `%LocalAppData%\WaterReminder\settings.json`）。
> 不会写入本仓库，也不会出现在日志或 Windows 客户端里。
> 程序**不需要 ntfy，也不需要 Pushover**。

## 为什么是 PushWard

PushWard 支持自定义通知动作：按钮可以是 **Notification Answer**，点按只被 PushWard
记录下来，由服务器轮询取回。按钮没有 `url`、没有 `foreground`，所以不会打开 App，
也不会打开 Safari。服务器收到 Answer 后按当前时段记下工作杯或家庭杯，再发一条「已记录」确认。

## 产品规则

| 时间 | 行为 |
| --- | --- |
| 工作时段（`WorkStart`–`WorkEnd`，扣除午休和下班前 N 分钟） | PushWard **和** Windows 桌面气泡 |
| `WorkEnd` ≤ 现在 < `QuietStart` 且开启手机推送 | 只发 PushWard；不弹桌面 |
| `QuietStart` 到次日 `WorkStart`、周末/非提醒日、暂停提醒 | 完全不推送 |

- 家庭杯默认 800 ml；手机上点「喝水了」记录的就是家庭杯，来源标记为 `pushward`。
- 只有 PushWard API 接受了提醒（2xx）才算成功并重新计时；发送失败约 90 秒后重试。
- 同一条通知只记一次（按 `notification_id` 去重），重复的 Answer 不会重复记账。

## 一次性设置

1. iPhone 从 **App Store** 安装 **PushWard**。
2. 打开 PushWard App，创建一个集成（Integration），复制以 `hlk_` 开头的 API Token。
3. 打开本程序 → 设置 →「手机推送（PushWard）」：
   - 勾选「开启手机推送」；
   - 把 API Token 粘贴到「PushWard API Token」；
   - 点「保存」（首次使用是「开始使用」）。Answers 轮询从保存时启动；
   - 需要验证时，重新打开设置点「发送测试」，iPhone 应收到一条带「喝水了」按钮的测试通知。
4. 服务器需要一直运行：提醒由服务器发出，Answer 也由服务器轮询取回。Windows 电脑只在上班时间显示气泡，不保存 PushWard Token。

> **iOS 上按钮通常不是直接可见的**：长按通知，或在通知中心下拉/展开该通知，
> 才能看到「喝水了」按钮。这是 iOS 通知动作的显示方式，不是程序故障。

> 免费额度约为每月 500 条推送；付费约 **$24/年**（以 PushWard 官方价格为准）。
> 本程序每天最多推送数条，正常使用远低于免费额度。

## 通知长什么样

```json
{
  "title": "该喝水了",
  "body": "已喝 400/3000 ml，建议 370 ml",
  "source": "waterreminder",
  "source_display_name": "喝水提醒",
  "level": "time-sensitive",
  "collapse_id": "water-evening",
  "actions": [
    {
      "id": "drink",
      "title": "喝水了",
      "icon": "drop.fill"
    }
  ]
}
```

- 请求：`POST https://api.pushward.app/notifications`，头
  `Authorization: Bearer <PushWard API token>`，正文 `application/json`。
- 顶层没有 `url`；动作也没有 `url` / `foreground`，点按只会产生 Notification Answer。
- 设置页的测试通知使用 `collapse_id: water-test`，动作与真实提醒相同。

## 服务器如何取回「喝水了」

1. 启动时先 `GET https://api.pushward.app/notifications/answers`，取得当前 `cursor`。
2. 循环 `GET /notifications/answers?after={cursor}&wait=25` 长轮询。
3. 收到 `action_id == "drink"` 的条目时，按 `notification_id` 去重（字符串或数字都按同一 id），
   上班记工作杯、下班后记家庭杯（来源 `pushward`），然后发送「已记录 / 已记 N ml」确认（无 actions）。
4. 每个响应都推进 `cursor`；网络失败只做短退避重试，不会弹窗或打断提醒计时。

轮询只在「开启手机推送 + 服务器上已配置 API Token」时运行。旧版 WPF 保存设置时会重启或停止本机轮询；服务器则在每次调度时按环境变量或 `pushward.token` 决定是否轮询。

## 设置测试

「发送测试」使用与真实提醒相同的消息构造：一条 PushWard 测试通知 + 同一个「喝水了」
动作。发送成功只看 PushWard API 是否返回 2xx（正常为 201）。长按/下拉通知后点
「喝水了」，即可验证整条记账链路。

如果刚填写或修改了 Token，请先点「保存」——轮询只在保存后启动；测试按钮本身只
验证通知能否发出。令牌保存在服务器上之后，轮询才会把点按取回并记账。

## 限制与隐私

- **服务器必须开机联网**。PushWard 只负责把通知送到手机；Answer 的轮询和记账由服务器完成。Windows 客户端不持有 PushWard Token。
- API Token 只出现在发往 `https://api.pushward.app` 的 HTTPS `Authorization` 头里，
  不写日志、不写入 JSON 正文。
- 程序不会把凭证写入 README、测试、示例设置或任何受版本控制的文件。
- 真实 iPhone + PushWard 的按钮行为尚未在本仓库的开发环境验证，见 [ACCEPT.md](ACCEPT.md)。
