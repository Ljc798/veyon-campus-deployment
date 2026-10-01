# CloudBase API 运行与验收

整理日期：2026-10-01

本文记录教师免登录发布、学生检索与下载、每日心跳使用的 CloudBase API 代码包、接口契约、权限边界、部署步骤与验收项目。CloudBase 目标环境为国内上海 **veyon-control-d3gs8hmuyd09c00a7**。

## 当前状态

- CloudBase 环境状态为 NORMAL，PG 已启用，私有存储桶 deployment-package-artifacts 已存在。
- 远端数据库最新已应用发布迁移为 `20260930130000`：已恢复免登录发布 RPC、新增私有发布教师姓名字段，并将配置 ZIP/对象桶限制为 64 KiB。教师发布授权迁移 `20260930120000` 是历史迁移，当前发布流程不使用它。工作区新增 `20260930150000` release/Teacher heartbeat schema、`20261001090000` 电脑名前缀约束修正与 `20261001090001` 共享下载限错尚未部署。
- HTTP API 默认域名为 veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com；该域名现已配置 `/` → `veyon-api` 的 HTTP 云函数路由。静态托管域名的 `/` 仍单独指向网站文件。
- `veyon-api` 已于 2026-09-30 部署，状态 Active，运行时 Nodejs20.19，类型 HTTP，代码包部署成功。
- 已验证 `GET /health` 返回 HTTP 200；2026-10-01 只读探测确认随机条件的匿名目录搜索返回 HTTP 200、0 条结果。网关总限频已下调并核实为 100 QPS；未配置单客户端 IP 限频，以避免校区共享公网出口导致学生被合并限流。一次合成发布返回 502；只读核对发现远端 SQL 约束拒绝合法的 `API-` 前缀，而 Node 校验器接受该前缀。修正迁移待应用；暂不重试公网发布。
- 当前已部署版本尚不包含本工作区新增的应用版本查询、私有安装器下载跳转、Teacher 校区心跳、共享限错 RPC 及 `20260930150000` / `20261001090000` / `20261001090001` 迁移；2026-10-01 对 TeacherConsole 与 StudentSetup 的匿名 latest-release GET 均返回 HTTP 404。当前本地 Node 合约测试覆盖匿名配置包发布/搜索/下载、教师与学生版本隔离、签名 URL 对象键和心跳 RPC 参数，但不代表国内 CloudBase 业务验收。轮换先前被意外输出的服务 API key、通过部署门禁并依序应用迁移、重新部署后，才能执行 live synthetic E2E。
- 2026-10-01 后续只读复核确认：PG 远端最新迁移仍是 `20260930130000`；release/Teacher-heartbeat 两张新表及 RPC 均不存在，私有桶 `application-release-artifacts` 也不存在；`20260930150000`、`20261001090000` 与本地新增的 `20261001090001` 迁移计划均返回 `executable=true`，但均未应用。现有匿名配置包搜索仍为 HTTP 200 空结果，TeacherConsole/StudentSetup release 查询仍为 HTTP 404；本轮未执行任何云端写入或合成发布。
- 远端 PostgreSQL OPA 规则当前允许既有健康、Student 心跳和部署包路径，不允许新的 release 或 Teacher heartbeat 路径；本地 `cloudbase/authz.user.rego` 已准备精确新增路径规则，尚未发布。教师发布不要求 CloudBase Auth 或校区预登记；函数校验上传内容、大小和必填字段。管理操作仍由管理员会话和数据库 RPC 控制。
- 云函数采用代码 ZIP，不依赖 TCR 镜像推送凭据。当前 CloudBase 对外运行的唯一权威实现是 `cloudfunctions/veyon-api`；`src/VeyonCampus.Telemetry.Server` 是保留的 .NET 对照/旧服务实现，配置包接口与 Node 保持校区校验和明确拒绝时的对象回滚边界。release 查询、安装器下载跳转和 Teacher 校区心跳目前仅由 Node 实现；不得将 .NET 服务替换为线上运行目标，除非先补齐并验收这些路由。

## 运行结构

    Teacher App / StudentSetup / 网站后台
                        │ HTTPS
                        ▼
    CloudBase 国内 HTTP API 默认域名
                        │ 已验证根路由转发 /health 与 /v1/...
                        ▼
    HTTP 云函数 veyon-api（Nodejs20.19，监听 9000）
              ┌─────────┴──────────┐
              ▼                    ▼
    PostgreSQL REST/RPC       私有 PG Storage
    教师、校区、配置包、      deployment-package-artifacts
    UTC+8 心跳与每日去重      只存规范 ZIP

函数代码只使用 Node.js 内置模块，不需要 node_modules。scf_bootstrap 以 CloudBase Node.js 20 运行时启动 index.js。函数通过 CloudBase PostgreSQL REST/RPC 和 Storage HTTP API 访问后端；服务端 API Key 和心跳 HMAC 根密钥只注入函数运行环境，不进入 ZIP、浏览器或桌面 App。国内默认 DNS 解析曾在本机超时；CLI 和 Node smoke 请求通过 `scripts/with-domestic-dns.sh` 使用国内 DNS 后成功。桌面端和用户浏览器仍需在各自网络确认域名解析正常。

网站和桌面端使用 HTTP API 默认域名。kidscode.fun 尚未绑定至该 API；体验版当前不能用自定义域名。API 路由必须建在 DomainType=HTTPSERVICE 的默认 HTTP 域名上，不得误建在 STATIC_STORE 静态网站域名。

## HTTP 接口

| 方法 | 路径 | 调用方 | 用途 |
| --- | --- | --- | --- |
| GET | /health | 运维 | 返回固定的 ready 状态，不暴露环境变量或请求头 |
| POST | /v1/heartbeat | 学生端 | 校验安装 ID、App 版本和部署 ID；按 UTC+8 日期生成 HMAC 摘要并调用 record_telemetry_heartbeat_v2 |
| GET | /v1/deployment-packages | 学生端 | 按校区、校区名或电脑名前缀搜索已发布目录 |
| POST | /v1/deployment-packages | 教师 App，无需登录 | 提交校区名称、教师姓名、教师手机号后四位和 schema v3 配置包；服务端校验并上传私有 ZIP |
| POST | /v1/deployment-packages/{packageId}/download | 学生端 | 校验教师手机号后四位、包状态、对象大小和 SHA-256 后返回 ZIP |
| POST | /v1/deployment-packages/{packageId}/withdraw | 管理员 | 按数据库授权规则撤回包；匿名发布包只能由 owner/admin 撤回 |
| GET | /v1/releases/latest?role=TeacherConsole\|StudentSetup&architecture=win-x64 | Teacher/Student，免登录 | 只返回已发布的角色版本、签名清单和固定 API 下载地址 |
| GET | /v1/releases/{releaseId}/artifact | Teacher/Student，免登录 | 为已发布安装器签发短时私有对象 URL 并返回 302；安装器不经过 HTTP Function |
| POST | /v1/heartbeat/teacher | Teacher，免登录 | 校验已发布 `packageId`，按香港日期 upsert 校区版本和电脑总数；服务端只保存随机 Publisher ID 的每日 HMAC |

完整请求和响应见 src/VeyonCampus.Telemetry.Server/openapi/deployment-packages.yaml。健康检查成功只证明函数进程响应，不代表数据库、Auth、私有存储或发布链路正常。

## 数据和权限边界

### 教师发布资料

教师发布不登录、不设置账号，也不需要管理员预先登记授权。Teacher App 提交校区名称、教师姓名和手机号后四位；表单校区名称必须与配置包 `manifest.json` 中的 `campus` 一致，防止目录标签与包内配置错配。校区名称用于搜索，若能唯一匹配 active 校区，数据库会自动关联该记录，未登记或名称重复时仍可按填写的名称搜索。

教师姓名以私有字段保存在 PostgreSQL，仅供服务端运维归属，不在学生目录或下载响应中返回；手机号后四位不以明文保存，只保存服务端 HMAC 指纹，并作为学生下载校验值。姓名和手机号后四位都不证明发布者身份，也不能替代账号认证。管理员工作区仍需要管理员账号，但这与教师上传无关。

### PostgreSQL

运行时使用已迁移的表和 RPC，不在函数中创建表。服务端 API Key 仅供云端函数访问 PostgreSQL REST/RPC 与私有存储；目录读取和修改 RPC 由迁移限制为 service_role。浏览器和桌面端不能拿到此密钥。

### 存储桶

deployment-package-artifacts 保持私有，单对象上限为 64 KiB。对象键由服务端按包 ID 生成：deployment-packages/v3/<32位小写GUID>.zip。API 不返回对象键或永久公开 URL。下载通过教师手机号后四位校验后，由函数读取对象；返回前复核对象长度及 SHA-256。

应用安装器使用单独的私有桶 `application-release-artifacts`，对象上限为 512 MiB，不能通过配置包 ZIP API 上传。匿名客户端只读已发布清单；发布操作由 `scripts/publish-application-release.cjs` 使用已轮换的 service API key 和本机 Developer Release 私钥完成。该私钥不得进入仓库、安装器或 CloudBase；发布脚本要求 PEM 公钥与签名私钥匹配，并在发布前生成 RSA-PSS/SHA-256 签名。

配置包和安装器均先上传对象，再调用数据库发布 RPC。只有收到明确的客户端拒绝（400、401、403、404、409、413、415 或 422）才自动删除对象；超时、断连、408/429 或其他状态时保留对象，因为数据库可能已提交但响应丢失。此类发布命令即使报错也可能已经成功，重试前应先查询目录/版本清单，避免重复发布；若最终确认数据库未提交，遗留对象需由运维核对后清理。

### 上传命名与文件规则

- 包必须是 schema v3、Windows x64，包含 manifest.json、campus.json、Veyon 公钥和网站策略公钥。
- 两个 PEM 必须是 2048–4096 位 RSA 公钥；学生包不能带私钥、secret、admin.txt、目录、链接、额外文件或路径穿越。
- packageId 必须为非空 GUID；存储名和下载名由服务端生成。
- ZIP 不超过 64 KiB；每个配置文件不超过 16 KiB；HTTP multipart 正文不超过 128 KiB。它用于小型校区配置，不用于分发安装器。
- 服务端校验 JSON 重复字段、清单 SHA-256、压缩包 CRC、文件名、电脑名前缀可生成 1–150 号合法 Windows 名称。
- 教师填写本人手机号后四位作为学生下载校验值；数据库只保存 HMAC 指纹，不保存明文数字。

### 心跳隐私

客户端发送安装 ID、App 版本与可选部署 ID。服务端使用稳定的 Telemetry__DailyHashKey 生成按日摘要，只将摘要和版本/部署分组传给 UTC+8 心跳 RPC；原始安装 ID 不写数据库或应用日志。已发布包 ID 能映射校区时，统计归属到对应校区；未映射部署 ID 只进入全站统计。

Teacher 校区心跳是独立的 opt-in 路径：Teacher 发送 `packageId`、随机本机 Publisher ID、Teacher/目标 Student SemVer 与 0–150 电脑总数；HTTP API 先读取已发布包，再以日密钥 HMAC Publisher ID，并生成服务端校区身份摘要。已登记包用 `campus_id`；匿名发布包因 `campus_id` 为空，改用规范化校区名和电脑名前缀的 HMAC 作为伪匿名归组键（不是已验证的学校身份）。新表以 `(day_hkt, campus_identity_digest)` 唯一键每日 upsert，同时保留可空 `campus_id` 和关联 `packageId`；不接收教师姓名、手机号、电脑名、IP 或学生安装 ID。Teacher 本机默认关闭；启用后于启动、启用或发布包时尝试发送，并在窗口保持打开时每小时检查到期状态。只有收到 HTTP 204 且保存本地发送日期后才跳过当日请求，失败时运行期间每小时重试。该新迁移/API 当前仅在工作区，尚未部署验收；旧 Student 心跳路径不会被这项新端点替代。

### 下载限错和日志

工作区新版下载限错由 PostgreSQL `get_deployment_package_download_with_rate_limit` RPC 原子执行：按包 ID 与客户端地址 HMAC 计数，15 分钟窗口内连续输错 10 次后封锁 15 分钟；正确校验会清零。表只保存 64 位 HMAC，不保存原始 IP，并按 1% 请求概率批量清理超过 1 天的记录。该共享状态尚未应用至 CloudBase；部署后仍需验证跨实例并发。当前线上函数尚未包含此 RPC。网关仍只按路由总量限制 100 QPS，没有按 ClientIP 限频，以避免校园共享公网出口误伤。4 位后缀仍不是强凭据，限错只增加猜测成本。函数和数据库不得记录密码、token、服务端 API Key、原始安装 ID、手机号后四位、客户端原始 IP、HMAC 摘要、上传正文或 x-cloudbase-context。

敏感响应、包变更和下载设置 Cache-Control: no-store。CORS 对外允许跨域请求，并仅开放 GET、POST、OPTIONS 及 API 所需的 Authorization、Content-Type、Accept 请求头。

PostgreSQL 迁移验证脚本会创建测试表和角色，只能对一次性空测试数据库执行；必须显式设置 `PGDATABASE`，不要指向 CloudBase 或任何生产数据库。`scripts/check-release-heartbeat-migration-postgres.sh` 验证 release 元数据、私有桶、Teacher 心跳香港日去重及 ACL；`scripts/check-download-rate-limit-postgres.sh` 验证共享下载限错和并发封锁。GitHub Actions 在临时 PostgreSQL 服务中运行这两项检查。

## 部署

### 代码包

函数部署配置位于根目录 cloudbaserc.json，部署脚本为 scripts/deploy-cloudbase-api.sh。脚本会：

1. 要求显式传入 --confirm-public-api，确认这是供教师和学生使用的公网 API。
2. 在读取任何本地密钥文件前，要求交互输入确认，或由调用环境设置 `CLOUDBASE_SERVICE_ROLE_KEY_ROTATED=yes`；非交互运行缺少此确认时立即退出。
3. 通过轮换门后，才从 Git 忽略的 .env.cloudbase.local 读取 CLOUDBASE_SERVICE_ROLE_KEY、稳定的 TELEMETRY_DAILY_HASH_KEY 和 `CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW=yes` 标记；不会输出这些值。
4. 通过进程级国内 DNS 包装器运行 CLI；检查 isIntl=false、目标环境/地域、所需迁移和代码包完整性。
5. 以 Nodejs20.19、HTTP 函数、ZIP 模式部署本地 cloudfunctions/veyon-api，不调用 TCR。
6. 退出时从部署脚本环境清除服务密钥变量。

bash scripts/deploy-cloudbase-api.sh --confirm-public-api

应用版本发布需另行在受控运维终端设置 `VEYONCAMPUS_RELEASE_PRIVATE_KEY_PATH`、`VEYONCAMPUS_RELEASE_PUBLIC_KEY_PATH` 和已轮换的 `CloudBase__ApiKey`，再明确调用 `node scripts/publish-application-release.cjs --role TeacherConsole --version X.Y.Z --installer artifacts/VeyonCampus-Teacher-Setup-X.Y.Z-win-x64.exe --confirm-publication`。PKCS#8 私钥可用 `VEYONCAMPUS_RELEASE_PRIVATE_KEY_PASSPHRASE` 环境变量解密；应从受控凭据存储注入，不放入命令参数、仓库或日志。不得通过命令行传递私钥内容或 service API key。

该脚本创建或更新函数；`cloudbaserc.json` 的 `gatewayPath: "/"` 也会让 CLI 收敛默认 HTTP API 域名的根路由。2026-09-30 首次部署已创建此路由。脚本不绑定 kidscode.fun。Secrets 由 CLI 从本机受限权限的 `.env.cloudbase.local` 传入云函数环境变量；不要把密钥填进 CLI 参数、MCP 参数、代码、网站 .env 或本文件。

### 公网权限与路由

函数代码发布后需保持以下 CloudBase 配置：

1. PostgreSQL 环境的 `authz.user.rego` 已开放函数访问，并限定为本 API 的 `/health`、`/v1/...` 路径；未开放其他资源。教师发布、学生搜索/下载与心跳 API 可公开调用；撤回操作仍验证管理员身份。不需要启用 CloudBase 匿名登录。
2. 默认 HTTP API 域已有 `/` → `veyon-api` 的 `WEB_SCF` 路由，路由已启用且 `auth=false`。目前 CLI 创建的 `enablePathTransmission=false`，实测仍可访问 `/health` 和 `/v1/deployment-packages`；不要改静态托管域名已有 `/` 路由。

创建后分别查询函数详情、权限和网关路由。对外地址仍是 CloudBase 国内 HTTP API 默认域名。

## 验收顺序

代码语法检查不代替云端验收。当前已完成健康检查和空目录只读查询；其余项目留给后续用合成数据验收：

1. **已完成**：GET /health → 200，响应仅包含固定状态。
2. **待复核配置**：HTTP API 根路由下调至 100 QPS（环境额度上限 500 QPS）；无单客户端 IP 限频。OPTIONS /v1/deployment-packages → 正确返回 CORS 头。
3. **已完成只读空目录查询**：公开搜索 → 200、空 items、没有私有对象键。
4. 不带 Authorization 上传合成 schema v3 包，并提交校区名、教师名和 4 位手机号后缀 → 201；缺字段/无效字段 → 400，不应返回 401/403。
5. 在 CloudBase 检查包目录字段和私有对象；确认发布教师姓名不在学生目录中，数据库没有明文手机号后四位，文件大小与 SHA-256 一致。
6. 学生搜索后用正确手机号后四位下载 → 200 且 ZIP 摘要一致；错误后四位 → 403；同来源达到失败阈值后 → 429，响应包含 Retry-After。本地 Node 合约测试注入合成 `X-Forwarded-For` 链，改变调用者提供的前置地址与旧 `X-Original-Forwarded-For`，验证仍按末段来源累计失败；10 次错误后临时封锁、正确后缀仍被封锁且另一末段来源不受影响。共享限错 RPC 的跨实例原子性仍需在应用 `20261001090001` 后通过真实 PostgreSQL 验收；此处只模拟数据库返回，不是线上限错验收。
7. 验证管理员可撤回包；匿名发布者不能用教师姓名或手机号后四位撤回包。
8. 用同一个合成心跳请求连续调用两次，检查 UTC+8 unique_devices 只增 1、heartbeat_signals 增 2；检查当前版本和 deployment ID 归组。
9. 验证无效 ZIP、额外文件、私钥、越界文件名、重复 manifest 字段、哈希错误、路径穿越、64 KiB 以上 ZIP、128 KiB 以上正文均被拒绝，且日志中没有手机号后四位、token、安装 ID 或请求正文。

教师免登录配置包真实 E2E 可在新迁移、OPA 和函数代码部署并通过验收后运行：`npm run check:live --prefix cloudfunctions/veyon-api -- --confirm-live-synthetic-test`。脚本需要受保护环境中的 `CloudBase__EnvId`、已轮换的 `CloudBase__ApiKey`、`CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW=yes` 和仅用于清理的管理员 `VEYONCAMPUS_LIVE_TEST_ADMIN_BEARER_TOKEN`；不得把这些值写入命令参数或日志。脚本不为发布、搜索或下载发送 Authorization，使用随机合成校区及 `API-XXXXXXX-` 前缀，验证错误后缀拒绝、正确下载、SHA-256、本地 ZIP 解析，再由管理员撤回并删除私有对象、复查目录已清除；发布响应丢失时也会尝试清理。当前尚未运行；若清理失败会报告合成 packageId 供运维处理。

上线初期只用合成测试数据；真实校区尚无目录数据。不要把一次健康检查或 CLI 部署成功记录为教师发布/学生下载端到端通过。

## 后续运维

- 发布接口契约变更时，核对 .NET 对照实现、Node 云函数和 OpenAPI 三处。
- 修改数据库时使用 cloudbase/migrations/ 迁移并核对远端列表，不在函数启动时自动建表。
- 轮换服务端 API Key 后同步更新 .env.cloudbase.local 并重新部署函数；轮换心跳 HMAC 根密钥会改变摘要，生产启用后需按专门的数据迁移方案处理。
- 查看函数状态和日志只使用 CloudBase 函数只读接口或 CLI；日志不应包含请求体、手机号后四位、Bearer token、摘要或任何密钥。
- CloudBase HTTP API 域名/静态托管域名/未来自定义域名用途不同。当前教师 App、学生 App 和心跳包继续使用 API 默认域名。
