# 匿名设备使用统计

本文记录学生端匿名心跳、教师包生成开关、CloudRun API 与 CloudBase PostgreSQL 的实现和上线顺序。匿名统计默认关闭；教师生成 schemaVersion=3 学生配置包时可以明确开启。

## 1. 当前状态

- Agent 已有匿名心跳实现；此前服务端能按日 HMAC 去重全站安装标识，但日期口径为 UTC。
- 教师生成的学生包默认 `telemetryEndpoint` 为空；教师可显式启用固定项目地址。云端发布端点也已调整为接受空地址或这个固定地址。
- 新实现将心跳间隔改为 UTC+8 自然日，每日成功后写入本机状态文件，Agent 重启不会再次发送；失败时 15 分钟后重试。
- 管理员在教师端首次生成包时可勾选“启用匿名每日使用统计”。开关默认关闭。新生成的配置包每天按 UTC+8 日期发送；相同日期、版本和部署包只发送一次，更新版本或部署包后会发送新组合。
- 请求携带 StudentSetup 版本和 manifest `packageId`（即部署包编号）。服务器从 `deployment_packages` 反查 PostgreSQL `campus_id`，不采信客户端自报校区名称或数字 ID。教师公开发布时填写的校区名若唯一匹配一个 active `campuses` 记录，部署包会关联该 ID；未登记或存在重名时 `campus_id` 为空，心跳只进入全站汇总，不生成校区/包分组。
- 新数据库迁移 `20260929140000_add_daily_campus_version_telemetry.sql` 已在 CloudBase PostgreSQL 成功应用；CLI 任务 `task-cb7a4f85` 成功，远端迁移历史已核对到该版本。它新增 UTC+8 表和 v2 RPC，保留旧 UTC 表与 RPC。
- 用户此前报告 CloudRun 旧版本部署成功；当前源码已合并新心跳和部署包 API，但 CloudRun 尚需发布新版。HTTP 网关 `/v1` 路由仍待配置。

## 2. 启用与请求格式

教师端“首次设置 → 生成学生校区配置包”中勾选“启用匿名每日使用统计”。生成包的 `manifest.json` 会使用固定项目地址：

    https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com/v1/heartbeat

该 URL 要求 CloudBase HTTP 网关创建 `/v1` 路由并保留后续路径；测试时指向当前测试 CloudRun 服务，正式启用后再将该路由切到正式服务。当前网关尚无路由，配置路由前启用统计的包会重试但不会影响学生端部署或网站策略 Agent。

心跳请求示例：

```json
{
  "installationId": "32 个十六进制字符",
  "applicationVersion": "0.4.34",
  "deploymentId": "manifest 中的 packageId GUID"
}
```

`installationId` 是本机首次运行时生成的随机 128 位值，保存在 SYSTEM Agent 的受保护配置目录。Agent 在 UTC+8 日期成功发送后，在 `usage-installation-id.last-hkt-day` 保存成功日期、工具版本和部署包编号；同一天相同版本/包的 Agent 重启不会重复请求。若当天更新了工具或配置包，会发送新组合的一次心跳。网络错误或非 204 响应不会标记当天完成，会在 15 分钟后重试。旧配置没有版本或部署包编号时，服务器用 `unknown` 版本和空 deployment ID 保持全站汇总兼容。

## 3. 后端接口与数据归属

服务位于 `src/VeyonCampus.Telemetry.Server`，ASP.NET Core Minimal API 使用 CloudBase PostgreSQL HTTP RPC，不使用数据库 TCP 连接。

| 方法 | 路径 | 行为 |
| --- | --- | --- |
| GET | `/health` | 返回 HTTP 200 和 `{ "status": "ready" }` |
| POST | `/v1/heartbeat` | 校验字段、生成 UTC+8 当日 HMAC，调用 `record_telemetry_heartbeat_v2`；成功返回 204 |
| GET | `/v1/stats/today` | 不存在；管理工作区以 Auth/RLS 查询 PostgreSQL 聚合表 |

服务端验证版本格式与 GUID 后，以 `Telemetry__DailyHashKey` 产生两个摘要：全站当日摘要用于全站活跃安装数；部署包范围摘要用于指定校区/包/版本的每日活跃数。数据库只存摘要，不存原始安装标识。部署编号在 `deployment_packages` 找不到对应记录时，只累积全站数据，不产生校区归属统计。

数据库会在 UTC+8 当前日内以唯一键去重：

- 全站：`(day_hkt, installation_digest)`。
- 校区、部署包和版本：`(day_hkt, campus_id, deployment_id, application_version, installation_digest)`。

每次有效 API 请求增加 `heartbeat_signals`；`unique_devices` 只在相应日/范围第一次收到摘要时增加。正常客户端一天成功发送一次；重复请求仍不会虚增活跃安装数。若同一安装在同一天更换了 App 版本或部署包，它会分别出现在对应分组中，因此不同分组的活跃数不能相加当作校区总设备数。

## 4. 隐私、权限与保留

- 发送字段：随机 installation ID、StudentSetup 版本、部署包 packageId。
- 服务端通过部署包记录关联校区 ID；校区名称仅由后台按关联结果显示。
- 心跳 JSON 正文不含姓名、账号、电脑名、IP 字段、浏览历史、软件使用记录或学生名单。HTTP 网络服务仍会接收连接源 IP，平台访问日志是否保存及其保留期需另行核实。
- 全站日摘要和部署包范围日摘要保留 90 天；每日汇总保留 400 天。
- 两种日摘要都使用按 UTC+8 日期轮换的 HMAC；部署包范围摘要还按 packageId 隔离，不支持跨日或跨包关联设备。
- API 请求 body、原始安装 ID、摘要和密钥不写入应用日志。浏览器不能读取摘要明细；只有 Auth/RLS 授权的站点角色可读聚合数据。
- CloudBase service API key 与 `Telemetry__DailyHashKey` 只保存在 CloudRun 服务端密钥配置。
- 心跳接口是匿名公开接口，没有终端身份认证；安装标识可以重置，请求也可以被伪造。汇总适合看趋势，不应当作完整物理设备清单、计费依据或安全审计证明。正式开放前应配置网关限频并观察异常请求。

## 5. 数据库迁移与发布顺序

`20260929140000_add_daily_campus_version_telemetry.sql` 已应用并在远端迁移历史核实。它新增全站 UTC+8 明细与汇总表、校区/部署包/版本的每日明细与汇总表，以及仅 `service_role` 可执行的 v2 RPC；旧 UTC 表和 RPC 保留，浏览器仅获得授权角色读取新汇总表的权限。

再按顺序发布：

1. 部署引用新 RPC 的 CloudRun 服务。
2. 创建 HTTP 网关 `/v1` 前缀路由，目标为测试服务 `veyon-control-dev`，保留 `/v1/heartbeat` 路径。
3. 用一次合成测试请求确认 API 返回 204、全站摘要增量为 1；同 ID、同日重复请求不再增加 `unique_devices`。
4. 确认带有效已发布 `deploymentId` 的请求，在校区/包/版本汇总表中增加一行；未登记的 GUID 不生成校区分组。
5. 构建新版本 TeacherConsole/StudentSetup；重新生成并发布一个测试包，明确勾选匿名统计。
6. 在受控测试学生机安装该包，检查管理后台趋势页出现校区、部署包编号、工具版本的日汇总。

旧数据库迁移文件不得改写。数据库迁移现已完成；HTTP 网关路由、新 CloudRun 服务版本和端到端心跳验收仍待完成。

## 6. 密钥与服务配置

| 环境变量 | 用途 | 规则 |
| --- | --- | --- |
| `PORT` | CloudRun 注入的监听端口 | 服务绑定 `0.0.0.0` |
| `CloudBase__EnvId` | CloudBase 环境 ID | 只放服务端 |
| `CloudBase__ApiKey` | PostgreSQL REST RPC 的 service API key | 机密，只放 CloudRun 密钥配置 |
| `Telemetry__DailyHashKey` | 按日 HMAC 的根密钥 | 至少 32 个随机字节后 Base64 编码；只放服务端 |

CloudBase publishable key 供浏览器 SDK 使用，不能替代服务端 API key。不要把服务端 API key、HMAC key、数据库密码放入网站静态文件、Docker build args、学生包、日志或版本库。

## 7. 验收命令

测试网关路由就绪后：

```bash
API='https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com'
curl -i "$API/health"
curl -i -X POST "$API/v1/heartbeat" \
  -H 'Content-Type: application/json' \
  --data '{"installationId":"0123456789abcdef0123456789abcdef","applicationVersion":"0.4.34","deploymentId":"替换为测试包packageId"}'
```

第一条应返回 200 ready；第二条应返回 204。数据库管理员应确认 HKT 当日 `telemetry_daily_hkt_stats.unique_devices` 增 1；同样请求再次执行后，`unique_devices` 不变、`heartbeat_signals` 增 1。若 deployment ID 属于已发布测试包，`telemetry_daily_deployment_stats` 对应校区/包/版本行的 `unique_devices` 只增 1。生产测试不要使用真实学生部署包或个人设备标识。
