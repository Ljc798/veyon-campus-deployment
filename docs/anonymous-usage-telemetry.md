# 匿名设备使用统计

本文说明 Agent 心跳、遥测 API 和 CloudBase PostgreSQL 的当前实现。数据库迁移已执行；遥测容器源码已连接 PostgreSQL RPC，但 CloudRun 尚未部署，因此 kidscode.fun 线上心跳地址目前不可用。管理端从受 Auth/RLS 保护的 PostgreSQL 每日汇总表读取数据，不再依赖进程内存统计接口。

## 1. 启用方式和请求字段

统计默认关闭。只有管理员为学生 Agent 显式配置 HTTPS 遥测地址后才会发送心跳。教师端生成 schemaVersion=3 学生包后，可编辑包内 manifest.json，将 telemetryEndpoint 从空字符串改为：

    https://kidscode.fun/api/v1/heartbeat

也可以在受保护的 Agent JSON 配置中设置 TelemetryEndpoint：

    {
      "CampusId": "example-campus",
      "PublicKeyPem": "-----BEGIN PUBLIC KEY-----...",
      "TelemetryEndpoint": "https://kidscode.fun/api/v1/heartbeat"
    }

配置位于 %ProgramData%\VeyonCampus\WebsitePolicy\<校区摘要>\agent-<校区摘要>.json。仅允许 HTTPS；本机 localhost/回环地址可在开发时使用 HTTP。发送失败不影响网站策略 Agent。重新安装并删除本机安装标识文件会生成新标识。

Agent 发送 POST JSON：

    {
      "installationId": "32 个十六进制字符"
    }

安装标识是本机首次运行时生成的随机 128 位值，保存在 Agent 配置目录的 usage-installation-id 文件中。不从硬件、Windows 账户或网络地址派生。

## 2. 后端接口

遥测服务位于 src/VeyonCampus.Telemetry.Server，是 ASP.NET Core Minimal API，使用 CloudBase PostgreSQL HTTP API，不使用数据库 TCP 客户端。

| 方法 | 路径 | 行为 |
| --- | --- | --- |
| GET | /health | 返回 HTTP 200 和 { "status": "ready" } |
| POST | /v1/heartbeat | 校验安装标识、按 UTC 日期 HMAC 后调用数据库 RPC；成功返回 204 |
| 其他 | /v1/stats/today | 不存在；管理 UI 使用用户身份和 RLS 查询日汇总表 |

心跳拒绝格式错误的 JSON/安装 ID，数据库调用失败或超时返回通用 503。响应不包含原始标识、HMAC 摘要或数据库错误。HTTP 503 时 Agent 保持自身策略服务运行，下次定时心跳可再尝试。

当前 /health 是静态进程健康响应，不会尝试连接 PostgreSQL。因此它不能证明 API key 有效、PG RPC 可写或认证管理页面可用。应在部署后用受控测试心跳和 PostgreSQL 汇总行单独验收。

## 3. 标识转换和数据库事务

服务端读取仅存在于服务器运行时的 Telemetry__DailyHashKey。对当前 UTC 日期生成日期密钥，再以该密钥对安装标识做 HMAC-SHA256，生成 64 位大写十六进制摘要。原始标识不会写入数据库或应用日志。

服务端向以下 CloudBase REST RPC 发送日期与摘要：

    https://{环境 ID}.api.tcloudbasegateway.com/v1/rdb/rest/rpc/record_telemetry_heartbeat

请求带服务端 CloudBase service API key。PostgreSQL 函数会再次验证日期是当天 UTC、摘要格式正确，并在同一个事务内：

1. 根据 (day_utc, installation_digest) 主键幂等写入摘要；
2. 只有第一次插入才增加 unique_devices；
3. 每次有效请求都增加 heartbeat_signals；
4. 每个 UTC 日第一次写入时清理超过 90 天的每日摘要及超过 400 天的汇总。

数据库中保存的 HMAC 每天变化，无法跨日关联同一安装。daily stats 中的 7/30/90 天活跃数之和不是周期独立设备数。心跳不含校区 ID、客户端版本或姓名，因此后台不会按校区或版本推导遥测数量。

## 4. 数据保留和查看权限

- telemetry_daily_devices 保存逐日摘要，清理目标为最近 90 天范围。
- telemetry_daily_stats 保存按 UTC 日期汇总的活跃安装数、信号数，清理目标为最近 400 天范围。
- 原始 installationId 只在 API 请求处理时短暂存在，不进入数据库或应用日志。
- 浏览器不能读取逐日摘要、CloudBase service API key 或日期 HMAC key。
- 已登录且在 admin_profiles 中登记角色的管理员可读每日汇总；RLS 是授权边界。
- 尚未实现校区级隔离、按校区统计、客户端版本统计、审计日志界面或数据库恢复流程。

完整表结构、角色策略、迁移与容量说明见 website/docs/PostgreSQL数据库设计.md。

## 5. 服务端配置和构建

通过 CloudRun 或其他受控运行平台的密钥管理注入以下配置。双下划线是 .NET 环境变量的配置分层写法：

| 环境变量 | 用途 | 规则 |
| --- | --- | --- |
| PORT | 监听端口 | CloudRun 通常注入；应用绑定 0.0.0.0 |
| CloudBase__EnvId | CloudBase 环境 ID | 仅服务端 |
| CloudBase__ApiKey | CloudBase PostgreSQL REST API 的 service API key | 机密，仅服务端密钥配置 |
| Telemetry__DailyHashKey | 用于按日 HMAC 的根密钥 | 至少 32 个随机字节后 Base64 编码，机密，仅服务端 |

不要设置前端 VITE_ 前缀给上述机密，不要写入 Dockerfile、前端构建参数、网站静态文件、学生包、日志或仓库。CloudBase publishable key 是浏览器认证配置，不具备替代 service API key 的写权限。

Dockerfile 位于仓库根目录。CloudRun Git 部署以仓库根目录作为构建上下文。建议服务名 veyon-telemetry、端口 9000、探针路径 /health。

本地编译：

    /tmp/veyon-dotnet/dotnet build src/VeyonCampus.Telemetry.Server/VeyonCampus.Telemetry.Server.csproj

容器化部署前要准备单独的服务端密钥。本仓库不保存真实 API key 或 HMAC key。

## 6. 网关规划

目标同域 URL：

| 公网 URL | 网关目标 |
| --- | --- |
| https://kidscode.fun/ | 静态网站 |
| https://kidscode.fun/api/health | 遥测服务 /health |
| https://kidscode.fun/api/v1/heartbeat | 遥测服务 /v1/heartbeat |

网关应去掉 /api 前缀后再转发。当前路由尚未配置，自定义域名也尚未绑定。

## 7. 发布前运营检查

匿名心跳 API 不需要用户登录即可被 Agent 调用，因此上线前还应配置可接受的请求速率/限额、观察异常写入和成本、限制日志内容、明确管理员启用前告知与退出方案。CloudBase PG 和体验套餐可用容量、备份与恢复点目前尚未验证为生产容量。

对外发布需要单独预检 CloudRun 费用和副本、API key 权限、HMAC key 生成/轮换、网关路由、域名 DNS/TLS、回滚方案和备份状态。完成预检并通过发布确认前，不要将代码构建成功误认为线上服务已接通。
