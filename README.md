# 什么值得买榜单监控

.NET 10 后台服务，监控 `https://faxian.smzdm.com/h2s0t0f0c0p1/#filter-block`，每 **30 分钟**按页面顺序取主列表 **前 20 条**。默认每小时新发 **1 条 Telegram 榜单消息**，半小时采集后编辑同一条消息。北京时间 **01:00–07:00 静默**，继续采集但不发送或编辑。提供 Docker Compose 部署，不需要数据库或开放端口。

## 通知规则

| 情况 | 行为 |
| --- | --- |
| 首次启动且无历史状态 | 立即采集完整 TOP 20，非静默时发送一条消息并建立比较基线 |
| 新的小时开始 | 当小时首次成功采集时新发一条完整榜单，即使没有变化也发送 |
| 同一小时再次采集 | 使用已保存的 message_id 编辑原消息，更新榜单、最后采集时间和本时段变化摘要 |
| 新进 | 本轮进入前 20、上轮不在前 20；掉榜后重进也算新进 |
| 变化 | 同一商品名次、展示价格或标题变化，计入时段摘要；商品旁简写名次和价格变化 |
| 掉榜 | 上轮在前 20、本轮不在前 20 |
| 霸榜 | 连续 3 次采集均在前 20，标记“霸N次”；首次达到门槛计入本时段新增霸榜数 |
| 01:00（含）至 07:00（不含） | 不调用 Telegram 发送或编辑，包括失败重试；仍然采集并保存榜单 |
| 07:00 后 | 首次成功采集时只发一条最新榜单，不补发夜间各轮消息 |

默认按北京时间自然小时划分时段，例如 10:00–11:00。若服务在 10:17 启动，则 10:17 发新消息、10:47 编辑、11:17 发下一小时的新消息；并不额外在整点抓取。可将 `NOTIFY_INTERVAL_HOURS` 设为 `2` 或 `3`，采集频率仍保持半小时，其间持续编辑同一条消息。2 小时时段从 00:00、02:00 等开始，3 小时时段从 00:00、03:00 等开始。

时段摘要按商品去重并保留该时段内已观察到的新进、变化和掉榜事件，最新 TOP 20 始终完整展示。标题使用可点击链接，长标题、价格和变化说明按长度预算缩短，不拆分成多条消息；完整信息可点进商品页查看。编辑消息调用 [Telegram editMessageText](https://core.telegram.org/bots/api#editmessagetext)。

“霸榜”默认连续 3 次，即至少跨过两个 30 分钟间隔、观察约 60 分钟。可调整 `MONITOR_DOMINANCE_POLLS`。这是采样结果，不保证两个采样时刻之间始终在榜。成功采集间隔超过设定周期的 1.5 倍时，连续计数重新从 1 开始，不将中断时间计入霸榜。名次由主列表顺序决定，不另行按价格、热度或点赞数排序。`#filter-block` 是浏览器锚点，不影响 HTTP 抓取。

## Docker 部署

服务器需安装 Docker Engine 和 Docker Compose v2，能够访问值得买、Telegram API、NuGet 和微软镜像仓库。

1. 复制配置：

   ```bash
   cp .env.example .env
   ```

   Windows PowerShell 可用 `Copy-Item .env.example .env`。

2. 编辑 `.env`，填写：

   ```dotenv
   TELEGRAM_BOT_TOKEN=123456789:替换为实际BotToken
   TELEGRAM_CHAT_ID=替换为实际ChatId
   ```

   在 Telegram 向 [@BotFather](https://t.me/BotFather) 发送 `/newbot` 创建机器人并取得 Token。私聊时先给机器人发送 `/start`；群组先把机器人加入群；频道需给予机器人发消息权限。

   获取 ChatId：给机器人发消息后，在自己电脑上访问 `https://api.telegram.org/bot<你的Token>/getUpdates`，取 `result[].message.chat.id`；群组通常是负数，公开频道也可填写 `@channel_username`。不要把 Token 发到公开位置。已有 webhook 的机器人不能直接使用 getUpdates，建议为监控新建一个机器人。话题群组可额外填写 `TELEGRAM_MESSAGE_THREAD_ID`。

3. 构建并启动：

   ```bash
   docker compose up -d --build
   docker compose logs -f --tail=100
   ```

4. 查看状态、更新或停止：

   ```bash
   docker compose ps
   docker compose up -d --build
   docker compose down
   ```

数据保存在命名卷 `monitor-data` 的 `/data/state.json`，包括最近榜单、连续在榜次数、当前时段事件、已发送的 message_id 和一条最新待发布榜单。重启后继续编辑该时段原消息。普通重启、重建和 `docker compose down` 保留数据。`docker compose down -v` 会删除历史数据，下一次运行重新建立基线。只运行一个实例；数据目录有独占锁。

镜像以非 root 用户运行，根文件系统只读，写入仅使用 `/data` 和临时目录。没有 Web 端口。Docker 健康检查在三个采集周期内没有成功采集时报告 unhealthy；Telegram 故障通过日志和待发布状态体现，不影响采集健康状态。Docker 的 unhealthy 标记本身不会自动重启容器；进程异常退出由 Compose 重启策略处理。

## 网络、反爬与代理

若服务器无法直连 Telegram，可在 `.env` 设置容器可达的 HTTP / SOCKS 代理，例如：

```dotenv
HTTPS_PROXY=http://192.168.1.10:7890
```

此代理也会用于值得买的 HTTPS 请求。需要让值得买直连时，可在 `NO_PROXY` 追加 `faxian.smzdm.com`。容器中的 `127.0.0.1` 是容器自身，应使用宿主机可达地址。Docker Desktop 可使用 `host.docker.internal`；Linux 请使用容器可达的宿主机 IP。

若网站需要登录态，可选填 `SMZDM_COOKIE`，或以环境变量 `Monitor__UserAgent` 设置 User-Agent。`.env` 中的 Cookie/Token 若包含 `$` 等字符，使用单引号包围完整值。Cookie 无法保证解决 IP 风控或 JavaScript 验证。本服务直接解析服务端 HTML，不执行浏览器挑战。出现验证码、缺少主列表、有效商品不足 20 条时，本轮失败，保留原有榜单和待发通知，下一周期重试。

交付时本环境无法成功读取实时榜单，当前解析器依据主列表 `feed-main-list` 及商品标题链接结构实现，并有人工 HTML 样例测试；**实际站点解析仍需在部署机器上验证**。先执行只读预览，无需 TG 凭据，不写状态也不发送通知：

```bash
docker compose run --rm --no-deps smzdm-monitor --dry-run
```

Compose 会检查 `.env` 的必填变量，所以容器预览前仍应填好配置。若预览失败，应根据部署环境日志检查网络、登录态或实际 HTML，并调整 `SmzdmParser.cs`，不要将失败结果当作空榜单。

## 本地运行和验证

安装 .NET 10 SDK。使用 .NET 10 是因为它是当前的 LTS 版本，见[微软支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy)。在仓库根目录执行：

```bash
dotnet build SmzdmMonitor.sln
dotnet test SmzdmMonitor.sln

# 人工 HTML 样例预览；不访问网站，不需要 TG 配置
dotnet run --project src/Smzdm.Monitor -- --dry-run --html tests/Smzdm.Monitor.Tests/Fixtures/leaderboard.html

# 实时页面预览；不发消息，不修改状态
dotnet run --project src/Smzdm.Monitor -- --dry-run
```

本地运行不会自动读取 `.env`，需要设置环境变量，例如 PowerShell：

```powershell
$env:Telegram__BotToken = '你的Token'
$env:Telegram__ChatId = '你的ChatId'
dotnet run --project src/Smzdm.Monitor
```

程序启动后按历史快照的采集时间安排下一轮，避免重启后短时间内重复增加霸榜计数。无历史数据时立即采集。每轮目标间隔为 30 分钟，慢请求可能让实际时间略有延迟。

单次执行采集并发布（会更新状态，遵守静默时段；当前时段已有消息则编辑）：

```bash
dotnet run --project src/Smzdm.Monitor -- --once
```

`--once` 会强制采集，连续手动运行也会增加采样次数；正常监控使用默认长期运行模式。默认本地数据目录是仓库根目录的 `data/`。直接执行发布后的 DLL 时，相对数据目录和 `--html` 路径均相对于进程工作目录。

## 配置

Compose 读取 `.env` 并将以下变量映射到 .NET 配置。本地使用右列环境变量，或修改 `src/Smzdm.Monitor/appsettings.json`。

| `.env` 变量 | 默认值 | .NET 环境变量 |
| --- | --- | --- |
| `TELEGRAM_BOT_TOKEN` | 必填 | `Telegram__BotToken` |
| `TELEGRAM_CHAT_ID` | 必填 | `Telegram__ChatId` |
| `TELEGRAM_MESSAGE_THREAD_ID` | 留空 | `Telegram__MessageThreadId` |
| `MONITOR_INTERVAL_MINUTES` | `30` | `Monitor__IntervalMinutes` |
| `MONITOR_TOP_COUNT` | `20` | `Monitor__TopCount` |
| `MONITOR_DOMINANCE_POLLS` | `3` | `Monitor__DominancePolls` |
| `NOTIFY_INTERVAL_HOURS` | `1`，可选 1/2/3 | `Notifications__NewMessageIntervalHours` |
| `QUIET_START_HOUR` | `1`，含该整点 | `Notifications__QuietStartHour` |
| `QUIET_END_HOUR` | `7`，不含该整点 | `Notifications__QuietEndHour` |
| `SMZDM_COOKIE` | 留空 | `Monitor__Cookie` |

静默时段使用 `Asia/Shanghai` 时区，独立于宿主机时区；开始和结束设为相同小时可关闭静默，也支持 `22` 至 `7` 这样的跨午夜时段。本地可通过 `Notifications__TimeZoneId` 调整时区，Compose 默认固定为北京时间。

例如，保留半小时采集、每两小时发一条、凌晨静默：

```dotenv
MONITOR_INTERVAL_MINUTES=30
NOTIFY_INTERVAL_HOURS=2
QUIET_START_HOUR=1
QUIET_END_HOUR=7
```

修改 `.env` 后执行 `docker compose up -d --build` 应用配置。另外支持 `Monitor__SourceUrl`、`Monitor__DataDirectory`、`Monitor__UserAgent`。默认监控给定 URL，修改 URL 或 TopCount 后须使用新数据目录，或先备份并移走旧 `state.json`，避免把不同榜单进行比较。

## 持久化和故障语义

- 每次成功采集先将榜单、时段事件和最新待发布内容一同写入临时文件并刷新到磁盘，再原子替换状态文件，然后发送或编辑 Telegram；成功后保存 message_id 和已发布内容。
- Telegram HTTP 成功但 `ok=false` 仍视为失败；编辑返回“message is not modified”视为已成功应用。遵循 429 的 `retry_after`。请求、响应规则见 [Telegram Bot API](https://core.telegram.org/bots/api#sendmessage)。
- Telegram 不可用时继续采集，待发布内容合并为最新版本，约每分钟重试一次；限流时遵守服务端指定时间。过期时段或超过 1.5 个采集周期的旧内容丢弃，等待新一轮采集，避免恢复后刷屏。静默期间没有待发队列积压，网络重试前也会再次检查静默时段。
- 旧版 v1 状态首次加载时保留榜单及连续计数，丢弃旧的逐条待发通知并升级为 v2；无需清空数据卷。修改 Bot Token、ChatId 或话题后不会编辑旧目标的消息。
- 原消息被删除或无法编辑时，本时段不会另发一条替代消息，以保持每时段一条的规则；后续时段重新发消息。网络响应丢失，或 Telegram 已接收但 message_id 尚未写盘时进程退出，仍可能重复新发；sendMessage 不支持幂等键，无法在这类故障下保证严格的恰好一次。
- 状态损坏、目录不可写或写盘失败会停止进程并报错，不自动抹掉历史。先恢复备份，或备份损坏文件后移走它重建基线。日志不输出 Bot Token 或 Cookie。

当前验证覆盖解析、变化和霸榜检测、单条消息长度、每小时新发及半小时编辑、重启继续编辑、01:00/07:00 边界、跨午夜静默、重试跨入静默和过期时段、失败合并、状态迁移、锁、Telegram 返回值及限流。测试使用本地构造数据和模拟 HTTP，不会发送真实 Telegram 消息。当前开发环境未安装 Docker，镜像构建及真实 TG 收发尚未执行。
