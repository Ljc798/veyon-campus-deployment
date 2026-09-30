# CloudBase API 运行与验收

整理日期：2026-09-30

本文记录教师免登录发布、学生检索与下载、每日心跳使用的 CloudBase API 代码包、接口契约、权限边界、部署步骤与验收项目。CloudBase 目标环境为国内上海 **veyon-control-d3gs8hmuyd09c00a7**。

## 当前状态

- CloudBase 环境状态为 NORMAL，PG 已启用，私有存储桶 deployment-package-artifacts 已存在。
- 远端数据库最新迁移为 20260930130000：已恢复免登录发布 RPC、新增私有发布教师姓名字段，并将 ZIP/对象桶限制为 64 KiB。教师发布授权迁移 20260930120000 是历史迁移，当前发布流程不使用它。
- HTTP API 默认域名为 veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com；该域名现已配置 `/` → `veyon-api` 的 HTTP 云函数路由。静态托管域名的 `/` 仍单独指向网站文件。
- `veyon-api` 已于 2026-09-30 部署，状态 Active，运行时 Nodejs20.19，类型 HTTP，代码包部署成功。
- 已验证 `GET /health` 返回 HTTP 200；只读 `GET /v1/deployment-packages` 返回 HTTP 200 和空目录。网关总限频已下调并核实为 100 QPS；未配置单客户端 IP 限频，以避免校区共享公网出口导致学生被合并限流。免登录发布、学生下载、心跳写入及存储桶读写仍待使用合成数据逐项验收。
- PostgreSQL 环境的 OPA 规则已限定为允许函数资源访问 `/health`、`/v1/...` API 路径；其余资源默认拒绝。教师发布不要求 CloudBase Auth 或校区预登记；函数校验上传内容、大小和必填字段。管理操作仍由管理员会话和数据库 RPC 控制。
- 云函数采用代码 ZIP，不依赖 TCR 镜像推送凭据。现有 .NET API 实现仍保留在 src/VeyonCampus.Telemetry.Server；对外发布使用的 Node.js 实现位于 cloudfunctions/veyon-api，修改接口行为时应同步核对两处实现和 OpenAPI 契约。

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

完整请求和响应见 src/VeyonCampus.Telemetry.Server/openapi/deployment-packages.yaml。健康检查成功只证明函数进程响应，不代表数据库、Auth、私有存储或发布链路正常。

## 数据和权限边界

### 教师发布资料

教师发布不登录、不设置账号，也不需要管理员预先登记授权。Teacher App 提交校区名称、教师姓名和手机号后四位；校区名称用于搜索，若能唯一匹配 active 校区，数据库会自动关联该记录，未登记或名称重复时仍可按填写的名称搜索。

教师姓名以私有字段保存在 PostgreSQL，仅供服务端运维归属，不在学生目录或下载响应中返回；手机号后四位不以明文保存，只保存服务端 HMAC 指纹，并作为学生下载校验值。姓名和手机号后四位都不证明发布者身份，也不能替代账号认证。管理员工作区仍需要管理员账号，但这与教师上传无关。

### PostgreSQL

运行时使用已迁移的表和 RPC，不在函数中创建表。服务端 API Key 仅供云端函数访问 PostgreSQL REST/RPC 与私有存储；目录读取和修改 RPC 由迁移限制为 service_role。浏览器和桌面端不能拿到此密钥。

### 存储桶

deployment-package-artifacts 保持私有，单对象上限为 64 KiB。对象键由服务端按包 ID 生成：deployment-packages/v3/<32位小写GUID>.zip。API 不返回对象键或永久公开 URL。下载通过教师手机号后四位校验后，由函数读取对象；返回前复核对象长度及 SHA-256。

### 上传命名与文件规则

- 包必须是 schema v3、Windows x64，包含 manifest.json、campus.json、Veyon 公钥和网站策略公钥。
- 两个 PEM 必须是 2048–4096 位 RSA 公钥；学生包不能带私钥、secret、admin.txt、目录、链接、额外文件或路径穿越。
- packageId 必须为非空 GUID；存储名和下载名由服务端生成。
- ZIP 不超过 64 KiB；每个配置文件不超过 16 KiB；HTTP multipart 正文不超过 128 KiB。它用于小型校区配置，不用于分发安装器。
- 服务端校验 JSON 重复字段、清单 SHA-256、压缩包 CRC、文件名、电脑名前缀可生成 1–150 号合法 Windows 名称。
- 教师填写本人手机号后四位作为学生下载校验值；数据库只保存 HMAC 指纹，不保存明文数字。

### 心跳隐私

客户端发送安装 ID、App 版本与可选部署 ID。服务端使用稳定的 Telemetry__DailyHashKey 生成按日摘要，只将摘要和版本/部署分组传给 UTC+8 心跳 RPC；原始安装 ID 不写数据库或应用日志。已发布包 ID 能映射校区时，统计归属到对应校区；未映射部署 ID 只进入全站统计。

### 下载限错和日志

函数进程内按客户端地址与包 ID 计数：15 分钟内连续输错 10 次后封锁 15 分钟；成功校验会清零。键总数受 8192 项上限约束。此计数器是单实例内存状态，多实例之间不共享。HTTP API 根路由目标为 100 QPS 总限频，环境 QPS 上限为 500；没有按 ClientIP 限频，因为一所学校的许多学生电脑可能共用公网出口。4 位后缀本身不是强凭据，限错只增加猜测成本。函数不得记录密码、token、服务端 API Key、原始安装 ID、手机号后四位、HMAC 摘要、上传正文或 x-cloudbase-context。

敏感响应、包变更和下载设置 Cache-Control: no-store。CORS 对外允许跨域请求，并仅开放 GET、POST、OPTIONS 及 API 所需的 Authorization、Content-Type、Accept 请求头。

## 部署

### 代码包

函数部署配置位于根目录 cloudbaserc.json，部署脚本为 scripts/deploy-cloudbase-api.sh。脚本会：

1. 要求显式传入 --confirm-public-api，确认这是供教师和学生使用的公网 API。
2. 从 Git 忽略的 .env.cloudbase.local 读取 CLOUDBASE_SERVICE_ROLE_KEY、稳定的 TELEMETRY_DAILY_HASH_KEY 和旧服务密钥撤销标记；不会输出这些值。
3. 通过进程级国内 DNS 包装器运行 CLI；检查 isIntl=false、目标环境/地域、最新免登录发布迁移和代码包完整性。
4. 以 Nodejs20.19、HTTP 函数、ZIP 模式部署本地 cloudfunctions/veyon-api，不调用 TCR。
5. 退出时从部署脚本环境清除服务密钥变量。

    bash scripts/deploy-cloudbase-api.sh --confirm-public-api

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
6. 学生搜索后用正确手机号后四位下载 → 200 且 ZIP 摘要一致；错误后四位 → 403；同来源达到失败阈值后 → 429，响应包含 Retry-After。
7. 验证管理员可撤回包；匿名发布者不能用教师姓名或手机号后四位撤回包。
8. 用同一个合成心跳请求连续调用两次，检查 UTC+8 unique_devices 只增 1、heartbeat_signals 增 2；检查当前版本和 deployment ID 归组。
9. 验证无效 ZIP、额外文件、私钥、越界文件名、重复 manifest 字段、哈希错误、路径穿越、64 KiB 以上 ZIP、128 KiB 以上正文均被拒绝，且日志中没有手机号后四位、token、安装 ID 或请求正文。

上线初期只用合成测试数据；真实校区尚无目录数据。不要把一次健康检查或 CLI 部署成功记录为教师发布/学生下载端到端通过。

## 后续运维

- 发布接口契约变更时，核对 .NET 对照实现、Node 云函数和 OpenAPI 三处。
- 修改数据库时使用 cloudbase/migrations/ 迁移并核对远端列表，不在函数启动时自动建表。
- 轮换服务端 API Key 后同步更新 .env.cloudbase.local 并重新部署函数；轮换心跳 HMAC 根密钥会改变摘要，生产启用后需按专门的数据迁移方案处理。
- 查看函数状态和日志只使用 CloudBase 函数只读接口或 CLI；日志不应包含请求体、手机号后四位、Bearer token、摘要或任何密钥。
- CloudBase HTTP API 域名/静态托管域名/未来自定义域名用途不同。当前教师 App、学生 App 和心跳包继续使用 API 默认域名。
