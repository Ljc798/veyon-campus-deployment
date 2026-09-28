# 匿名设备在线统计

此功能用于估算某一天有多少台已启用设备在线，不记录学生姓名、账号、主机名、IP 地址、浏览记录、应用使用记录或校区 ID。心跳默认关闭；只有管理员为学生 Agent 明确配置统计地址后才会发送。部署前应告知设备使用者并确认符合学校的告知与隐私要求。

## 设备发送什么

启用后，学生网站策略 Agent 启动时发送一次信号，之后每 24 小时发送一次。请求只包含本机首次运行时随机生成的 128 位安装标识。标识保存在 Agent 配置目录的 `usage-installation-id` 文件中，不从硬件、Windows 账户或网络地址派生。发送失败不影响网站策略 Agent；重装并删除该文件会生成新标识。

教师端生成 schemaVersion=3 学生包后，编辑包内的 `manifest.json`，将 `telemetryEndpoint` 从空字符串改为后端的 HTTPS 地址。这个配置随包分发，因此同一配置包部署的设备会使用同一个统计后端。保留空字符串可关闭统计。也可以在受保护的 Agent JSON 配置中手动添加 `TelemetryEndpoint` 字段：

```json
{
  "CampusId": "example-campus",
  "PublicKeyPem": "-----BEGIN PUBLIC KEY-----...",
  "TelemetryEndpoint": "https://stats.example.edu/v1/heartbeat"
}
```

该配置位于 `%ProgramData%\VeyonCampus\WebsitePolicy\<校区摘要>\agent-<校区摘要>.json`。仅允许 HTTPS；本机 `localhost`/回环地址可在开发时使用 HTTP。

## 后端统计内容

项目 `src/VeyonCampus.Telemetry.Server` 是无外部 NuGet 依赖的 ASP.NET Core Minimal API：

- `POST /v1/heartbeat` 接受唯一字段 `installationId`，格式为 32 位十六进制字符。
- `GET /v1/stats/today` 返回 UTC 日期、当天活跃设备数和收到的信号数。此接口仅在配置统计令牌后开放，并要求 `Authorization: Bearer <令牌>`。
- `/health` 提供存活检查。

服务端对安装标识使用仅保存在服务器上的密钥按 UTC 日期做 HMAC，然后丢弃请求中的原始标识。仅在内存中保留当天的 HMAC 摘要用于去重；午夜清除摘要。原始标识不写入应用日志或磁盘。进程重启会清空当日计数，因此这是轻量统计原型，不是持久化分析服务。服务器和托管平台的访问日志也应配置为不保留请求正文，并按部署需要最小化来源 IP 的留存。

## 启动后端

在托管环境的秘密配置中设置以下值；不要将密钥或统计令牌提交到仓库：

- `Telemetry__DailyHashKey`：至少 32 个随机字节的 Base64 编码值。
- `Telemetry__StatsBearerToken`：至少 32 个 UTF-8 字节的随机令牌。省略时统计接口返回 404。

然后发布并运行：

```powershell
dotnet publish src/VeyonCampus.Telemetry.Server/VeyonCampus.Telemetry.Server.csproj -c Release -o artifacts/telemetry-server
dotnet artifacts/telemetry-server/VeyonCampus.Telemetry.Server.dll
```

生产环境应在 HTTPS 反向代理或托管平台 TLS 终止层后运行，并把 `/v1/heartbeat` 配置到设备端的 `TelemetryEndpoint`。`/v1/stats/today` 只供受信任的管理端查询，不要将令牌嵌入学生包或浏览器前端。
