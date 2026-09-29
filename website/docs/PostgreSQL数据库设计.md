# CloudBase PostgreSQL 数据库设计

记录日期：2026-09-29<br>
环境：veyon-control，上海 ap-shanghai  
CloudBase 远端迁移已应用至：20260929140000；部署包发布限制与教师下载码迁移也已应用。

本数据库现有结构服务于两个应用流程：管理员通过 CloudBase Auth 登录后维护校区资料、查看受控汇总；旧遥测服务以 UTC 日写入 HMAC 摘要，新心跳协议使用独立的 UTC+8 表和 RPC。原始安装标识不会进入数据库。部署包目录支持免登录上传、学生检索和下载；服务端 API 通过 CloudBase HTTP API 访问数据库。静态页面和桌面 App 不直接连接 PostgreSQL TCP 端口。

## 1. 设计边界

- 身份由 CloudBase Auth 管理；admin_profiles 只保存 Auth 用户 ID、显示名和站点角色。
- 校区资料是站点管理数据，不包含教师、学生或个人账号信息。
- 匿名遥测按 UTC+8 自然日去重。HMAC 摘要按日轮换；部署范围摘要也按 packageId 隔离。
- 心跳发送 manifest `packageId`；服务端用已发布包记录反查校区 ID，不接受客户端自报校区名称或数字 ID。
- 公共网站不使用数据库。浏览器用 publishable key 和登录会话访问 Web RDB API；PostgreSQL RLS 负责授权。
- 服务端 API 使用 CloudBase service API key 调用受限 RPC。该密钥只能放在服务端托管密钥配置中。
- 网站时间戳与遥测日期统一使用 UTC+8（`Asia/Hong_Kong`）；数据库时间戳仍用 `timestamptz` 保存绝对时间点。
- 新迁移新增 `day_hkt` 逐日表，不改名或删除旧 UTC 表和 RPC，以支持新旧服务滚动升级。部署包范围的逐日 HMAC 不支持跨日或跨包关联。

## 2. 实体关系

```mermaid
erDiagram
    AUTH_USER ||--o| ADMIN_PROFILE : "角色记录"
    TELEMETRY_DAY ||--o{ DAILY_DEVICE_DIGEST : "当日去重"
    TELEMETRY_DAY ||--o| DAILY_STATS : "聚合统计"
    RETENTION_STATE }o--|| TELEMETRY_DAY : "最近清理日期"
    CAMPUS {
        bigint id PK
        varchar name
        varchar region
        varchar city
        text status
        timestamptz created_at
        timestamptz updated_at
    }
    ADMIN_PROFILE {
        varchar user_id PK
        text display_name
        text role
        timestamptz created_at
    }
    DAILY_DEVICE_DIGEST {
        date day_utc PK
        char64 installation_digest PK
        timestamptz recorded_at
    }
    DAILY_STATS {
        date day_utc PK
        integer unique_devices
        bigint heartbeat_signals
        timestamptz updated_at
    }
```

AUTH_USER 是 CloudBase 内建认证表，不由本迁移创建。旧 UTC 心跳表没有校区外键，因为旧协议不提供校区 ID；新 UTC+8 分组表通过已发布部署包记录关联校区。

## 3. 表结构

以下表格描述旧 UTC 遥测表。迁移 `20260929140000` 已远端应用并并行新增 UTC+8 表；旧表及 RPC 保持可用，结构与新 RPC 见第 9 节。

| 表 | 粒度 | 关键字段 | 索引与用途 |
| --- | --- | --- | --- |
| public.admin_profiles | 每位后台用户一行 | user_id 主键、display_name、role | 角色限定为 owner、admin、editor、viewer；浏览器仅能读取当前用户自己的角色行 |
| public.campuses | 每个站点校区一行 | 自增 id、name、region、city、status、创建与更新时间 | 名称和地区长度/非空检查；状态为 active 或 paused；有状态更新时间及地区城市组合索引；更新时间由触发器维护 |
| public.telemetry_daily_devices | 每日每个安装一行 | day_utc、64 位大写十六进制 installation_digest、recorded_at | (day_utc, installation_digest) 复合主键，支持幂等去重；原始 32 位安装标识不保存 |
| public.telemetry_daily_stats | 每个 UTC 日期一行 | day_utc、unique_devices、heartbeat_signals、updated_at | 日期主键；原子累计去重安装数与请求次数 |
| public.telemetry_retention_state | 固定一行 | id=true、last_cleanup_utc | 控制每日首次心跳只执行一次旧数据清理 |

### 设计选择

- campuses 当前规模预期以学校/校区资料为主，常规 B-tree 索引足够支持按地区和状态筛选；后台每次最多读取 200 条列表，界面会显示总数。
- 心跳写入通过数据库函数在单个事务中插入逐日摘要并原子更新汇总，避免服务实例各自维护内存计数导致重启或多副本数据不一致。
- unique_devices 是单个 UTC 日内去重数。7/30/90 日相加得到的是“每日活跃数之和”，不是周期独立设备总数。
- 每日摘要表和每日统计表有不同保留期，避免把可用于单日去重的摘要长期保存。

## 4. 数据写入与保留

旧版本 `record_telemetry_heartbeat(p_utc_day, p_installation_digest)` 写入步骤：

1. 要求传入日期等于数据库当前 UTC 日期。
2. 校验摘要为 64 位大写十六进制字符串。
3. 以 (UTC 日期, 摘要) 插入；重复摘要不会重复增加当日活跃安装数。
4. 每次有效请求均增加 heartbeat_signals；新摘要才增加 unique_devices。
5. 当日首次写入时清理超过 90 天的逐日摘要，以及超过 400 天的每日汇总。

应用服务先以服务端 Telemetry__DailyHashKey 对随机安装标识按日期执行 HMAC-SHA256，只将摘要传给该函数。日期密钥与 CloudBase service API key 分开保管。原始标识、摘要、API key 不写入应用日志。

旧写入端点只允许 service_role 执行函数；浏览器角色无法读取原始逐日摘要表。旧管理页可继续查询 `telemetry_daily_stats`；新后台按 UTC+8 读取 `telemetry_daily_hkt_stats`。

## 5. 角色和行级安全

| 角色 | 查询自身角色记录 | 读取校区与每日汇总 | 新增/编辑校区 | 删除校区 | 管理站点角色 |
| --- | --- | --- | --- | --- | --- |
| owner | 是 | 是 | 是 | 数据策略允许，当前 UI 未提供删除 | 不允许从浏览器修改 |
| admin | 是 | 是 | 是 | 数据策略允许，当前 UI 未提供删除 | 不允许从浏览器修改 |
| editor | 是 | 是 | 是 | 否 | 否 |
| viewer | 是 | 是 | 否 | 否 | 否 |
| 未登记 Auth 用户 | 无管理资料 | 否 | 否 | 否 | 否 |

所有业务表均启用 RLS。admin_profiles 的 SELECT 策略只返回当前 Auth 身份对应的一行；角色表没有 authenticated 写策略。校区的读写/删除与每日汇总 SELECT 均要求用户在 admin_profiles 中拥有站点角色。遥测明细表和保留状态表对浏览器没有策略与表授权。

当前没有校区级数据隔离。所有已授权站点角色可读取所有校区和汇总统计；角色策略不能替代未来可能需要的校区归属模型。应用页面里的按钮限制只是用户体验，实际安全依赖 PostgreSQL grants 与 RLS。

首次迁移从已有 CloudBase Auth administrator 账号写入一条 owner 角色。该账号 ID 不写进源代码或文档。当前没有公开注册或自助提权流程；后续管理员账号应先在 CloudBase Auth 中创建，再通过受控管理渠道登记最小权限角色。

## 6. 迁移执行记录与后续流程

初始版本文件：

    cloudbase/migrations/20260928141201_create_site_database.sql

CloudBase PostgreSQL 已记录该初始版本，迁移任务状态为 Succeed、verified=true。初始核对结果：五张业务表存在、种子管理员角色为 owner、校区当前 0 行、行级策略已创建。当前没有生产业务资料需要迁移。

后续数据库结构变更必须使用新的 14 位时间版本文件，不得改写已执行的 SQL：

1. 先写前向迁移与受影响表/权限说明。
2. 用 CloudBase PostgreSQL migration plan 检查语法、对象影响与可执行性。
3. 执行前确认计划只涉及预期对象；破坏性 DDL 要另外制定数据备份和回滚方案。
4. 通过 CloudBase migration apply 执行，并等待任务成功。
5. 重新读取表结构、索引、函数、grants、RLS 策略和行数，记录迁移版本及验证结果。
6. 再更新 Web RDB 代码和本文件。

不要直接对业务生产库运行未版本化的 DDL；不要为方便前端访问而开放匿名写权限。

## 7. 容量、备份与后续演进

当前 CloudBase 环境显示为体验版，已读到环境 QPS 配额，但 PostgreSQL 存储、连接池、自动备份/PITR、恢复点和可用性 SLA 尚未完成生产核对。本文没有把体验版额度视为学校生产容量承诺。上线前应在 CloudBase 控制台记录 PG 存储和连接规格、备份保留/恢复演练状态、当前套餐费用与 API 峰值估算。

有迹象显示数据量或并发接近配额时，按以下顺序演进：

1. 观察 PG 存储、慢查询、RPC 延迟和心跳错误率；为服务端写入配置重试上限与限速。
2. 若逐日摘要表增长超出保留窗可接受范围，评估按 day_utc 分区和分区级过期删除。
3. 若管理员列表超 200 条，改为基于 updated_at,id 的游标分页，而非增大单次全表读取。
4. 若要按教师/学生角色或校区做访问隔离，建立校区授权映射并通过 RLS 落实；不能通过 IP 或推测值补关联。
5. 后续可增加有界请求限速、迁移审计与管理员报表导出上限。

上线前应补充并演练数据库恢复流程；备份能力与保留策略目前尚未在本轮环境中验证。

## 8. 教师发布、学生检索的部署包目录

迁移文件：

    cloudbase/migrations/20260929021100_create_deployment_package_catalog.sql

迁移状态：已应用。CloudBase CLI 3.8.4 预览确认只有该迁移待执行，任务 `task-7da92226` 状态为 Succeed；远端迁移历史已核对。只读 SQL 确认三张表均存在且启用了 RLS，发布目录策略、服务端策略、索引、触发器和三个函数均已创建。

迁移包含三个新增表：

| 表 | 粒度 | 访问边界 |
| --- | --- | --- |
| public.deployment_package_publishers | 一个教师用户在一个校区的一条发布授权 | authenticated 用户只读自己的有效映射；写入和授权管理仅限服务端 |
| public.deployment_packages | 每个 manifest `packageId` 对应一条不可变发布记录 | anon/authenticated 只能读取 published 目录字段；不能写入、撤回或读取发布者身份 |
| public.deployment_package_artifacts | 每个 package 一条私有对象键 | 仅 service_role；不向学生或教师浏览器返回实际存储路径 |

本结构当前只接受 `schemaVersion=3`、Windows x64 校区配置 ZIP。数据库原有兼容性列约束为 512 KiB；教师端、网站、服务端和 `20260929041500` 应用后的私有桶限制新发布包为 64 KiB，单文件最多 16 KiB，请求体最多 128 KiB。数据库用清单 package UUID 生成文件名 `veyon-campus-config-v3-<32位小写GUID>.zip` 和私有对象键 `deployment-packages/v3/<32位小写GUID>.zip`。SHA-256 必须是 64 位大写十六进制。文件名、对象键都不由上传者输入。

电脑名前缀在数据库端采用与现有 Windows 命名器一致的 ASCII 字母/数字/连字符规则，并将最大值收紧到 12 个字符，确保后续追加 1–150 编号后主机名仍不超过 Windows 的 15 字符限制。教师发布账号必须拥有对应活跃校区的 `deployment_package_publishers` 授权；现有 owner/admin 可发布所有活跃校区。包正文、ZIP entry 路径、manifest 与资源摘要仍需上传 API 解包验证，数据库约束不能验证对象存储中 ZIP 的真实内容。

学生目录只开放已发布包的名称、校区、前缀、schema、平台、大小、摘要、生成文件名、发布时间和下载计数。对象键表仅服务端可见；下载 API 应重新检查状态与对象存在后再返回短时链接/文件流。包撤回采用软状态，已发布包的校区、前缀、SHA 和大小不可修改；更新配置要生成新的 manifest packageId。

数据库表结构落地并不代表远程分发已经接通。教师账号授权、ZIP 接收/严格校验、私有 CloudBase 存储和学生搜索/下载 API 已有源码，CloudRun 测试服务用户报告已部署成功；HTTP 网关路由与端到端验收仍待完成。旧局域网服务的移除需在云端链路验收后处理。

## 9. UTC+8 校区和版本遥测（迁移已应用）

迁移文件：

    cloudbase/migrations/20260929140000_add_daily_campus_version_telemetry.sql

目标结构包含：

| 表 | 粒度 | 权限 |
| --- | --- | --- |
| telemetry_daily_hkt_devices | UTC+8 日期、每日全站安装 HMAC 摘要 | 仅服务端 |
| telemetry_daily_hkt_stats | UTC+8 日期的全站活跃数与请求数 | service_role 写；已登记站点角色按 RLS 读 |
| telemetry_daily_deployment_devices | UTC+8 日期、校区、deployment packageId、StudentSetup 版本、每日 HMAC 摘要 | 仅服务端 |
| telemetry_daily_deployment_stats | UTC+8 日期、校区、deployment packageId、StudentSetup 版本的每日活跃数与请求数 | service_role 写；已登记站点角色按 RLS 读 |

迁移已在 CloudBase 按序应用，CLI 任务 `task-cb7a4f85` 状态为 `Succeed`，远端迁移历史最新版本为 `20260929140000`。迁移新增 `telemetry_daily_hkt_devices`、`telemetry_daily_hkt_stats` 和独立 UTC+8 清理状态表，以及校区/部署包/版本细项；保留现有 `telemetry_daily_devices`、`telemetry_daily_stats`、UTC 列和 `record_telemetry_heartbeat` RPC。新服务写入 `record_telemetry_heartbeat_v2` 与校区/版本细项。CloudRun 尚需部署调用新 RPC 的版本，并配置网关后做端到端验收。

RPC 原子写入全站每日汇总，并从 `deployment_packages.package_id` 反查 `campus_id`。已发布或已撤回的包编号都保留其历史校区映射；未知包编号只进入全站汇总，不会生成校区归属行。全站与部署范围摘要分开 HMAC，避免在同一天通过摘要跨包关联安装。

新 `record_telemetry_heartbeat_v2` 的全站 `unique_devices` 按 UTC+8 日期去重；校区细项按日期、校区、packageId 和版本去重。重复 API 请求只会增加对应 `heartbeat_signals`，不会虚增该分组活跃数。一个设备若在同一天使用不同版本或配置包，会在对应分组分别计数，因此分组相加不是校区总安装数。
