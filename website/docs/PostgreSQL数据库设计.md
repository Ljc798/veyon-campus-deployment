# CloudBase PostgreSQL 数据库设计

记录日期：2026-09-30<br>
环境：veyon-control，上海 ap-shanghai  
CloudBase 远端迁移已应用至：20260930130000；部署包 64 KiB 限制和教师免登录发布迁移均已应用。2026-09-30 通过 MCP 核对迁移列表共 8 条；公开发布 RPC 仅授予 `service_role` 执行。

本数据库现有结构服务于两个应用流程：管理员通过 CloudBase Auth 登录后维护校区资料、查看受控汇总；旧遥测服务以 UTC 日写入 HMAC 摘要，新心跳协议使用独立的 UTC+8 表和 RPC。原始安装标识不会进入数据库。教师可免登录发布校区配置包，数据库不接收原始手机号后四位，只保存 keyed HMAC 指纹；教师姓名保存在仅供服务端访问的列。网站管理员登录与教师发布无关。HTTP 云函数 `veyon-api` 已部署，默认 HTTP API 路由已建立；只读空目录搜索返回 HTTP 200。教师发布、下载与心跳写入仍待端到端验收。服务端 API 通过 CloudBase HTTP API 访问数据库；静态页面和桌面 App 不直接连接 PostgreSQL TCP 端口。

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

当前 CloudBase 环境显示为体验版。2026-09-29 只读 SQL 核对得到 PostgreSQL 总大小 10,950,323 字节（`pg_size_pretty` 为 10 MB）；实例模式、连接规格、自动备份/PITR、恢复点和可用性 SLA 仍未完成生产核对。本文没有把体验版额度视为学校生产容量承诺。上线前应在 CloudBase 控制台记录 PG 存储和连接规格、备份保留/恢复演练状态、当前套餐费用与 API 峰值估算。

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
| public.deployment_package_publishers | 旧版教师登录授权关系，保留作迁移历史兼容 | 当前免登录发布流程不读取或写入此表 |
| public.deployment_packages | 每个 manifest `packageId` 对应一条不可变发布记录 | anon/authenticated 只能读取已授权目录列；不能写入、撤回或读取私有发布者姓名与身份指纹 |
| public.deployment_package_artifacts | 每个 package 一条私有对象键 | 仅 service_role；不向学生或教师浏览器返回实际存储路径 |

本结构当前只接受 `schemaVersion=3`、Windows x64 校区配置 ZIP。迁移 `20260930130000_restore_simple_campus_package_publication.sql` 将数据库约束和私有桶对象大小统一为 64 KiB；单个配置文件最多 16 KiB，请求体最多 128 KiB。数据库用清单 package UUID 生成文件名 `veyon-campus-config-v3-<32位小写GUID>.zip` 和私有对象键 `deployment-packages/v3/<32位小写GUID>.zip`。SHA-256 必须是 64 位大写十六进制。文件名、对象键都不由上传者输入。

电脑名前缀在数据库端采用与现有 Windows 命名器一致的 ASCII 字母/数字/连字符规则，最多 12 个字符，确保后续追加 1–150 编号后不超过 Windows 的 15 字符限制；合法前缀可像 `PC-` 一样以连字符结尾。远端现有约束错误地拒绝这类前缀，迁移 `20261001090000_align_package_prefix_validation.sql` 已在本地准备，应用前匿名发布可能返回 502。迁移 `20260930120000_require_authenticated_campus_package_publishers.sql` 曾短暂引入教师 Auth 发布要求；按用户确认，后续迁移 `20260930130000_restore_simple_campus_package_publication.sql` 已恢复免登录 API 发布。教师提交校区名称、教师姓名和手机号后四位，无需 Auth 账号、发布授权表记录或预登记校区。已登记校区名唯一匹配 active 记录时自动关联；未登记或重名校区仍以提交的名称发布和搜索。姓名存放在只供服务端读取的列，不进入 student catalog；手机号后四位只保存 keyed HMAC 指纹，用于学生下载校验，不是身份凭据。旧管理发布授权 UI 与教师自助端点已经从当前源码移除。云端修正、合成包和实际学生下载仍待验收。

学生目录只开放已发布包的名称、校区、前缀、schema、平台、大小、摘要、生成文件名、发布时间和下载计数。对象键表仅服务端可见；下载 API 应重新检查状态与对象存在后再返回短时链接/文件流。包撤回采用软状态，已发布包的校区、前缀、SHA 和大小不可修改；更新配置要生成新的 manifest packageId。

数据库表结构落地并不代表远程分发已经完成。Teacher App 免登录发布、ZIP 严格校验、私有 CloudBase 存储及 Student 搜索/下载有本地源码；后端计算入口为 Nodejs20.19 HTTP ZIP 云函数 `veyon-api`，HTTP API 默认域名根路由已建立，目标总限频为 100 QPS。远端迁移 `20260930130000_restore_simple_campus_package_publication.sql` 已应用；一次合成上传因数据库拒绝合法尾随连字符前缀返回 502，修正迁移 `20261001090000_align_package_prefix_validation.sql` 尚未应用。2026-10-01 已确认函数 Active、`/health` 返回 200、只读部署包搜索 RPC 返回空目录 200；release/Teacher-heartbeat schema、匿名上传、私有对象存储、下载和 UTC+8 心跳尚未端到端验收。当前工作区移除 Windows 文件共享配置分发入口；本机磁盘导入保留为离线维护方式。

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

迁移已在 CloudBase 按序应用，CLI 任务 `task-cb7a4f85` 状态为 `Succeed`；2026-09-30 追加的教师发布 Auth 迁移和其后的免登录恢复迁移也已通过 CloudBase MCP 应用，远端最新版本为 `20260930130000`。迁移新增 `telemetry_daily_hkt_devices`、`telemetry_daily_hkt_stats` 和独立 UTC+8 清理状态表，以及校区/部署包/版本细项；保留现有 `telemetry_daily_devices`、`telemetry_daily_stats`、UTC 列和 `record_telemetry_heartbeat` RPC。新 API 写入 `record_telemetry_heartbeat_v2` 与校区/版本细项。API 云函数和网关已部署，心跳写入仍待端到端验收。

RPC 原子写入全站每日汇总，并从 `deployment_packages.package_id` 反查 `campus_id`。已发布或已撤回的包编号都保留其历史校区映射；未知包编号只进入全站汇总，不会生成校区归属行。全站与部署范围摘要分开 HMAC，避免在同一天通过摘要跨包关联安装。

新 `record_telemetry_heartbeat_v2` 的全站 `unique_devices` 按 UTC+8 日期去重；校区细项按日期、校区、packageId 和版本去重。重复 API 请求只会增加对应 `heartbeat_signals`，不会虚增该分组活跃数。一个设备若在同一天使用不同版本或配置包，会在对应分组分别计数，因此分组相加不是校区总安装数。

## 10. PostgreSQL 当前体量与用量核对（2026-09-30）

数据库体量、表结构和行数来自此前成功的 CloudBase 只读查询；2026-09-30 另通过控制台只读复核资源账单与运行状态，没有执行数据库写入：

| 项 | 核对结果 | 解释 |
| --- | --- | --- |
| 数据库总大小 | 10,950,323 字节；`pg_size_pretty` 显示 10 MB | 包含 PostgreSQL 与 CloudBase 系统 schema 的基础占用 |
| 校区、部署包数据 | `campuses` 与 `deployment_packages` 当前 0 行 | 目前没有大量业务数据或教师上传的 ZIP |
| 部署包对象 | `storage.objects` 当前 0 行；桶 `deployment-package-artifacts` 存在且 `public=false` | 文件暂未上传，桶为私有 |
| 业务表大小 | 当前最大用户表 `auth.users` 约 208 KiB；应用表多为数十 KiB | 没有明显可清理的大表或索引 |
| API 连通性 | CloudBase `veyon-api` HTTP 函数 Active；`GET /health` 为 HTTP 200；`GET /v1/deployment-packages` 经默认 API 域名返回空目录 HTTP 200；免登录发布 RPC ACL 经只读查询核对；网关限频待调至 100 QPS | Teacher 发布、对象存储读写、Student 下载和 heartbeat 尚未做合成数据端到端验收 |

CloudBase 控制台当前账期为 `2026-09-28` 至 `2026-10-28`。PostgreSQL 显示容量使用量 316 MB·小时、CU 使用量 820 核秒、消耗 78.28 点；820 核秒按 342 点／核小时约为 77.9 点，316 MB·小时按 0.5 点／GB·小时约为 0.15 点，账单主要来自数据库计算区间。MB·小时是容量乘以时间的累计值，不是当前有 316 MB 数据；数据库实际大小仍约 10.95 MB。云托管类别另累计 355.69 点、2.98 核小时和 5.97 GB·小时内存，但当前控制台服务列表为 0 个服务；明细没有按服务名拆分，不能据此断定具体来源。整体体验版额度 3,000 点已用 434.82 点。用量汇总未按 SQL 或调用者归因，不能仅凭这些总量判断某次查询造成了多少消耗。

目前不建议删除表、索引或调整保留期来追求这几十 MB 以内的差异：业务表几乎为空，改变结构的节省空间很有限，却会影响校区发布、学生下载和 UTC+8 心跳。先观察完整账期的 PostgreSQL 细项；若计算点数继续增长，再按查询记录和计量明细定位原因。实例类型和容量计费规则以控制台为准；CloudBase 文档说明共享实例按实际占用计量，独享实例按已分配容量与运行时长计量，独享实例存储不能缩容，见[数据库管理说明](https://docs.cloudbase.net/database/postgresql/database-management)。
