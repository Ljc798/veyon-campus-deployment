> 归档于 2026-09-29：历史记录，版本、命令与结论只适用于原文场景。当前工作请从 [文档索引](../../README.md) 开始。

> 当前架构更正：后端 API 使用 CloudBase HTTP 云函数 `veyon-api` 并由客户端经 HTTP 网关调用；CloudRun/云托管只属于本文件记录的旧试验，已弃用。现行部署说明见[网站与云端指南](../../网站与云端指南.md)。

# CloudBase、PostgreSQL、域名与 HTTPS 接入记录

记录日期：2026-09-29<br>
目标环境：veyon-control（veyon-control-d3gs8hmuyd09c00a7，ap-shanghai）  
目标域名：kidscode.fun

本文记录实际已执行的 CloudBase 前后端与 PostgreSQL 接入，以及仍待核验的资源状态。2026-09-29 已按序应用数据库迁移 `20260929041500`（发布限制）、`20260929093000`（教师信息与下载码）和 `20260929140000`（UTC+8 心跳）；CloudBase CLI 任务 `task-cb7a4f85` 成功，远端迁移历史已核对到 `20260929140000`。迁移前部署包目录为 0 条，因此教师校验码迁移没有撤回旧包。用户已删除 CloudRun 测试服务 `veyon-control-dev`；当前 Cloud Functions 列表为空，默认 HTTP 网关域名也没有 API 路由。新的 HTTP 自定义镜像函数 `veyon-api` 已写入本地部署配置，尚未首次部署，桌面端默认 API 地址暂不可用。

## 1. 当前资源状态

| 项目 | 状态 | 记录 |
| --- | --- | --- |
| CloudBase 环境 | 已确认 | veyon-control，上海 ap-shanghai |
| 环境套餐 | 体验版 | 控制台显示至 2027-03-28；生产前须复核费用和配额 |
| CloudBase Auth | 已配置 | 用户名密码登录开启；当前无公开注册入口 |
| 管理员角色 | 已初始化 | 已有 administrator Auth 账号被迁移为 owner；账号 ID 不写入仓库 |
| Publishable key | CloudBase 环境中已创建 | 仅供浏览器 SDK 使用，不是 service API key；本机 `website/.env.local` 当前不存在，重新构建网站前须从控制台配置 |
| PostgreSQL | 已开通并迁移 | 13 张业务表；含部署包目录、UTC 与 UTC+8 心跳表、RLS、索引和 RPC |
| 迁移版本 | 已应用并验证 | 远端历史最新为 `20260929140000`；`20260929041500`、`20260929093000`、`20260929140000` 均按序成功应用 |
| 部署包目录迁移 | 已应用并核对 | CloudBase CLI 3.8.4 任务 `task-7da92226` 成功；远端迁移历史有该版本，3 张表和 RLS 均已确认 |
| 网站 Auth + PG 前端 | 已实现 | 登录、角色检查、校区读写、每日汇总查询；需用有效管理员密码完成浏览器验收 |
| 教师发布 / 学生检索 UI | 本地源码已更新 | 目录展示名可用中文，不必等于配置包内 Veyon 校区标识；教师填写姓名及手机号后四位发布，学生下载时由部署教师输入；App v0.4.38 测试 ZIP 已本地构建 |
| 服务端 API 源码 | 已实现 | 含 HKT 心跳、匿名配置包发布、教师后四位下载校验、管理员撤回/授权管理、学生搜索/下载；ZIP 限 64 KiB、单文件限 16 KiB、请求体限 128 KiB |
| API 云函数 | 尚未部署 | CloudRun 已删除；本地已配置 `veyon-api`（HTTP、CustomImage、ap-shanghai、端口 9000、匿名访问、网关根路由），首次部署和密钥补齐后仍需验收 |
| 静态网站 | 未发布本项目构建 | CloudBase 仍有已有默认站点资源；不得清空或覆盖自带认证文件 |
| kidscode.fun | 尚未绑定 | 目前不代表根域名网站已迁到 CloudBase |
| HTTPS 证书 | 已签发、未绑定 | 90 天免费 DV 证书已签发，尚未部署到 CloudBase 入口 |

网站时间戳和新匿名心跳日界线按 UTC+8（`Asia/Hong_Kong`）。现有 `telemetry_daily_stats` 与旧 RPC 保留 UTC 口径；已应用的心跳迁移新增独立 UTC+8 表，并按已登记校区、包编号和学生工具版本汇总，不改名或删除旧列和旧 RPC。公开发布的校区名若唯一匹配一个 active `campuses` 记录，数据库会写入对应 `campus_id`；未登记或名称有歧义的包保留 `campus_id=NULL`，心跳只计入全站汇总。

CloudBase 默认静态域名为 `veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com`。HTTP 网关默认域名为 `veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com`。最近只读核对确认网关总开关已开启、鉴权关闭；HTTP 网关目前没有 API 路由。`cloudbaserc.json` 为 HTTP 函数声明公开根路由 `/`，由 CLI 部署时自动创建；它将原样转发 `/health`、`/v1/...` 等路径到 `veyon-api`。CloudBase 静态存储桶内已有平台自带认证辅助文件和 `cloud-admin/index.html`；后续静态发布应只上传网站构建目录内需要更新的静态资源，不删除整个存储桶。

2026-09-29 默认域名访问检查：静态托管的开发测试提示页是 CloudBase 的预期中间页；确认访问后根路径返回 `404 NoSuchKey`，缺少 `index.html`，说明项目构建尚未上传到站点根目录。HTTP 网关控制台显示的准确域名包含静态应用编号 `-1348081197`；漏掉这段编号的 `veyon-control-d3gs8hmuyd09c00a7.ap-shanghai.app.tcloudbase.com` 返回 `INVALID_ENV`。控制台路由列表为空，访问准确域名的 `/` 与 `/api/health` 均返回 `INVALID_PATH`。此前记录的 `.service.tcloudbase.com` 推测不正确，以控制台显示值为准。

2026-09-29 上传结果：MCP 上传接口在刷新认证时返回 `network timeout at https://iaas.cloud.tencent.com/tcb_refresh`；之后通过已登录的 CloudBase 控制台成功上传了 `index.html` 到根目录，以及 4 个资源到 `assets/`。逐项以 SHA-256 比对远端响应与本机构建，5 个对象完全一致。第一次选择整个 `dist` 文件夹时，控制台将其保留为额外的 `/dist/` 副本；未删除该副本。静态默认域名 `/` 返回 200；直接访问 `/admin` 与 `/admin/campuses` 返回 404，SPA 回退尚未配置。HTTP 网关仍无路由，`/api` 与遥测服务尚未接通。

## 2. PostgreSQL 数据库与身份授权

迁移文件：

    cloudbase/migrations/20260928141201_create_site_database.sql

五张应用表：

- admin_profiles：将 CloudBase Auth 用户绑定到 owner/admin/editor/viewer 站点角色。
- campuses：名称、地区、城市和 active/paused 状态，供授权管理员维护。
- telemetry_daily_devices / telemetry_daily_stats：现有兼容表，以 UTC 日期和 HMAC 摘要统计旧协议。
- telemetry_retention_state：控制旧 UTC 逐日明细按日清理。
- 新迁移新增 telemetry_daily_hkt_devices、telemetry_daily_hkt_stats 和 telemetry_hkt_retention_state，供 UTC+8 新协议使用。

RLS 与 grants 已在迁移中设置。后台必须先有 Auth 会话并读到 admin_profiles 中的角色；没有角色记录时拒绝访问。campuses 和每日汇总表的行策略只允许已登记站点角色读取；editor 可新增/编辑，owner/admin 另有删除策略但当前页面不提供删除操作。角色表没有浏览器写策略。遥测摘要明细不允许浏览器角色访问，只有服务端 service_role RPC 写入。

最初的管理员账号已被映射成 owner。由于本任务未提供管理员密码，本地浏览器认证及校区保存流程尚未以该账号完成真实登录操作；权限种子和策略已从 CloudBase PG 读取核对。

2026-09-29 已应用教师发布/学生检索的部署包目录迁移 `cloudbase/migrations/20260929021100_create_deployment_package_catalog.sql`。CloudBase MCP 的 `tcb_refresh` 仍超时；改用 CloudBase CLI 3.8.4，经本机系统代理完成登录、迁移预览与应用。任务 `task-7da92226` 状态为 Succeed。只读 SQL 确认新增三张目录表都启用了 RLS，预期策略、函数、触发器和索引均存在。

同日已应用 `cloudbase/migrations/20260929032900_create_deployment_package_api.sql`，CLI 任务 `task-156ede6a` 状态为 `Succeed`。迁移新增私有 PG Storage 桶和六个 API RPC。远端核对桶 ID `deployment-package-artifacts`、`public=false`、MIME 白名单 `application/zip`；初始 524288 字节大小限制已由 `20260929041500` 收紧为 65536 字节。`storage.buckets` / `storage.objects` 没有 `anon` 或 `authenticated` 对象策略。六个 RPC 只有 `service_role` 可执行，目录表仍启用 RLS。迁移前桶无对象，目录无真实包。

部署包 API 源码位于现有 .NET 10 服务 `src/VeyonCampus.Telemetry.Server/DeploymentPackageEndpoints.cs`，将作为 HTTP 自定义镜像云函数运行。公开发布 API 校验校区名、教师姓名、手机号后四位、配置文件、哈希、大小和命名规则；展示用校区名不要求与配置包内供 Veyon 密钥使用的标识一致。数据库只存服务端密钥生成的身份摘要与手机号后四位摘要，不保存明文后四位。学生搜索不返回后四位，下载请求需提交教师提供的正确后四位；owner/admin 可撤回，授权管理仍由管理 API 执行。发布 API 本身不要求 CloudBase Auth，管理工作区仍需登录。心跳带配置包编号；校区名唯一匹配 active `campuses` 记录时按校区归属汇总，未登记或名称重复时仅进入全站统计。数据库迁移已应用；HTTP 云函数和网关根路由仍待首次部署。

同日排查 CLI 网络：npm 原 registry 指向 `registry.npmmirror.com`，直接域名解析失败；本机 macOS 已有 HTTP/HTTPS 系统代理，但 npm 未自动使用。通过命令级 registry 与 proxy 参数访问官方 npm registry 后，CloudBase CLI 登录成功。全局安装因 `@cloudbase/cloudbase-mcp` 已占用同名 `cloudbase-mcp` 可执行文件而冲突，因此使用 `npm exec --package=@cloudbase/cli` 运行 CLI，没有覆盖现有 MCP 命令，也没有改写持久 npm 配置。CloudBase MCP 的 `tcb_refresh` 请求仍超时；可在该 MCP 进程环境增加 `HTTP_PROXY` / `HTTPS_PROXY` 后重启并复测，这一代理配置尚未改动或验证。

CloudBase PG Storage 桶与静态网站托管桶是独立资源。部署包只写入私有 PG 桶，不进入网站静态文件桶。

更完整的数据粒度、字段、限制、保留期和扩容方案见 [PostgreSQL 数据库设计](../../../website/docs/PostgreSQL数据库设计.md)。

## 3. 网站本地运行与配置

网站目录 website 使用 Vite 和 CloudBase JavaScript SDK。首次安装和启动：

    cd website
    npm ci
    if [ ! -f .env.local ]; then cp cloudbase.env.example .env.local; fi
    npm run dev

Vite 默认地址是 http://127.0.0.1:5173。CloudBase 环境中已创建 publishable key，但本机 `website/.env.local` 当前不存在；重新运行 Vite 构建前，需从控制台取用该浏览器专用 key 并填入本地忽略文件。不要复制到示例文件或提交 Git。真实浏览器认证还需要将 `127.0.0.1:5173` 加入 CloudBase 环境安全域名。核对时只允许添加实际使用的本地来源，不要使用通配符；`kidscode.fun` 在最终域名接入前也要加入允许域名。

本轮已按实际 Vite 地址尝试添加 127.0.0.1:5173；CloudBase 返回“当前套餐无法执行此操作”。因此本地浏览器直连 Auth/RDB 的 CORS 验证暂时受套餐限制，不能用此失败推断前端身份代码或 RLS 不正确。没有为此升级套餐；后续需由环境管理员评估可用套餐或选用 CloudBase 已允许的托管来源进行验收。

前端环境变量如下：

| 名称 | 说明 | 可否进入浏览器构建 |
| --- | --- | --- |
| VITE_CLOUDBASE_ENV_ID | CloudBase 环境 ID | 可以 |
| VITE_CLOUDBASE_REGION | 环境地域 | 可以 |
| VITE_CLOUDBASE_PUBLISHABLE_KEY | 浏览器专用 publishable key，受 Auth 与 RLS 限制 | 可以 |
| VITE_API_BASE_PATH | Cloud Functions HTTP 网关 API 基址；生产静态网站与 HTTP 网关是不同域，应配置完整网关 URL，不加 `/api` | 可以 |
| CloudBase__ApiKey | 服务端 `service_role` API Key，用于 PostgreSQL RPC 和私有 PG Storage | 不可以 |
| CloudBase__DeploymentPackageBucket | 部署包 PG Storage 桶 ID；默认 `deployment-package-artifacts` | 不可以 |
| Telemetry__DailyHashKey | 服务端 HMAC 日期密钥 | 不可以 |

生产构建目录是 `website/dist`。`VITE_API_BASE_PATH` 应设为 `https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com`；网站会直接请求该 HTTP 网关下的 `/health` 和 `/v1/...`。相对值 `/api` 会请求静态网站自己的域名。不得将 `.env.local`、CloudBase server API key、数据库密码或 HMAC 密钥放入静态站点、Docker build args、学生安装包或版本库。

## 4. 应用数据流

### 管理工作区

1. 管理员直接打开 /admin；公开首页、导航、页脚和产品展示页没有后台链接。
2. 浏览器用 CloudBase Auth 用户名密码登录并恢复安全会话。
3. 页面按当前用户 ID 查询 admin_profiles；无角色记录即显示无权限。
4. 页面通过 CloudBase Web RDB SDK 读取真实校区与每日统计，新增和编辑校区时直接写入 public.campuses。
5. 数据库 grants 和 RLS 是实际授权边界；前端不持有管理员令牌或 service role key。

公开站点可以被直接输入 /admin，但会先显示登录界面；只有已授权登录用户才能取得站点数据。隐藏公共入口减少误点，不代替 Auth/RLS。

### 桌面 Agent 心跳

1. 学生包默认关闭匿名统计；教师生成 v3 学生包时可勾选启用。
2. 启用后每天发送 `installationId`、StudentSetup `applicationVersion` 和包清单 `deploymentId`；同一日期内相同版本/部署包组合成功后不再发送，版本或部署包变化时会发送新组合。客户端保存最近成功状态，失败后 15 分钟重试。
3. 服务端按 UTC+8 自然日 HMAC；全站摘要和按 deploymentId 隔离的摘要分别计算，原始安装标识不入库。
4. 数据库从已发布包编号反查校区 ID，并按校区、部署包编号、工具版本聚合。未知包编号只进入全站数据。
5. 新 API/PG 代码已在本地实现，migration `20260929140000...` 已应用；CloudRun 已删除，HTTP 自定义镜像云函数 `veyon-api` 与网关根路由尚待首次部署。
6. `/health` 只报告遥测进程存活，不证明 PG RPC 可写。

云函数部署后的网站和桌面端共用 HTTP 网关域名。静态网站域名与 HTTP 网关域名不同；网站通过完整 `VITE_API_BASE_PATH` 请求 API，学生 App 与匿名心跳直接访问 `/v1`：

| 公网路径 | 目标 | 去除前缀后的服务路径 |
| --- | --- | --- |
| `/` 根路由 | HTTP 自定义镜像云函数 `veyon-api` | 保留请求路径；例如 `/health` → `/health`，`/v1/heartbeat` → `/v1/heartbeat` |

根路由需保留子路径；CloudBase CLI 从 `cloudbaserc.json` 读取 `gatewayPath: "/"` 并自动配置路由。心跳包固定使用环境默认 HTTP 网关 `/v1/heartbeat` 地址。HTTP 网关配置参考[官方路由文档](https://cloud.tencent.com/document/product/876/122894)和[路由数据结构](https://cloud.tencent.com/document/product/876/34822)。部署包接口契约见 [部署包云端分发设计](部署包云端分发设计.md)，心跳详情见仓库根目录 docs/anonymous-usage-telemetry.md。

HTTP 网关域名：`veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com`。HTTP 网关鉴权关闭；函数 `public: true` 将放通匿名访问，公开目录、搜索、下载和发布由服务端校验业务字段，撤回及管理员接口由 API 校验 CloudBase Auth 管理员身份。函数根路由保留所有路径。网站会跨域访问该地址，API 已启用不带 Cookie 的 CORS；浏览器来源安全域名仍按 CloudBase Auth/RDB 的要求单独管理。

## 5. 域名、DNS 与 90 天证书

DNSPod 中 kidscode.fun 当前状态：

| 主机记录 | 类型 | 已读到的值 | 处理 |
| --- | --- | --- | --- |
| @ | A | 216.198.79.1 | 暂时保留；实际用途待核实，不能直接改到 CloudBase |
| finance | A | 117.72.166.158 | 与本次网站接入无关，保持不变 |
| _dnsauth.ai | TXT | 原有验证值 | 保持不变 |
| _dnsauth | TXT | 本次证书校验记录 | 已写入；当前证书已签发，后续删改需确认续期要求 |
| www | 无记录 | — | DNS 尚未指向网站 |

根域名现有 A 记录可能承载其他网站或服务。本轮没有改动根域名、www、finance 或邮件解析。域名流量迁移前应先确认 216.198.79.1 的用途与切换影响。

免费 DV 证书记录：

- 证书 ID：b9uky5gN。
- 覆盖域名：kidscode.fun 和 www.kidscode.fun。
- 有效期 90 天，控制台到期时间 2026-12-27 19:59:59。
- DNS TXT 验证已完成，证书已于 2026-09-28 签发。
- 证书没有部署到 CloudBase 静态托管或网关；当前不能据此认定两个域名的 HTTPS 已启用。

最终接入前要确认 CloudBase 自定义域名和 TLS 的具体绑定位置，导入/关联已签发证书，再逐个验证主域名与 www。证书有效期短，应设置续期计划。绑定自定义域名和更改 DNS 会影响公网流量，须列入单独发布预检。

## 6. HTTP 云函数 API

项目位置：`src/VeyonCampus.Telemetry.Server`。现有 .NET 10 API 使用仓库根目录 `Dockerfile` 构建为 Linux amd64 自定义镜像，作为 HTTP 云函数 `veyon-api` 运行。HTTP 函数固定监听 `0.0.0.0:9000`，健康检查为 `/health`；心跳、教师发布、学生检索/下载和管理端 API 都由同一函数处理。

函数必须配置：

| 环境变量 | 用途 | 要求 |
| --- | --- | --- |
| PORT | 本地容器可选监听端口 | 云函数镜像仍固定监听 9000 |
| CloudBase__EnvId | CloudBase 环境 ID | 仅服务端 |
| CloudBase__ApiKey | CloudBase server API Key | 仅云函数服务端密钥；调用 PG RPC 和私有 PG Storage，不进入前端 |
| Telemetry__DailyHashKey | HMAC 日期密钥 | 至少 32 个随机字节的 Base64 编码；当前相关新表为空，本机 `.env` 已生成新密钥，首次写入后保持稳定 |

CloudRun `veyon-control-dev` 已删除，Cloud Functions 列表当前为空，HTTP 网关下没有 API 路由。根目录 `cloudbaserc.json` 已声明函数配置、公开访问和网关根路由。不要把浏览器 publishable key 错当服务端 API key。部署包 API 的 `CloudBase__DeploymentPackageBucket` 可省略（默认值为 `deployment-package-artifacts`）。匿名心跳与公开上传 API 上线后可被公网调用，需保持文件大小限制并使用平台限频与访问日志监控；应用和平台不得记录请求正文、原始安装标识、HMAC 摘要或密钥。

### 6.1 当前云函数部署配置

`cloudbaserc.json` 将 `veyon-api` 配为 `type: HTTP`、`runtime: CustomImage`、`buildStrategy: cloud`，地域为 `ap-shanghai`，镜像端口 9000，内存 512 MiB、超时 60 秒。`public: true` 会允许匿名访问，`gatewayPath: "/"` 会自动建立 HTTP 网关根路由并保留 API 子路径。

首次部署前将根目录 `cloudbase.env.example` 复制为 `.env`，填入个人版 TCR UIN/密码和 CloudBase server API key。当前部署包、发布者和新 UTC+8 心跳表为空；本机 `.env` 已生成新的 32 字节随机 HMAC key。首次写入后必须保持该 key 稳定。镜像仓库与云构建、云函数必须都在 `ap-shanghai`；首次部署还需先授权函数角色拉取个人版 TCR 镜像。

从仓库根目录执行：

```powershell
tcb login
tcb fn deploy veyon-api --env-id veyon-control-d3gs8hmuyd09c00a7 --httpFn --deployMode image
```

CloudBase CLI 根据 `public` 和 `gatewayPath` 自动设置匿名访问规则与根路由。函数部署不会应用数据库迁移；现有迁移已完成。首次部署后检查 `/health`、学生目录、教师 ZIP 上传与后四位下载，再验证两次合成心跳的 PostgreSQL 写入。

### 历史 CloudRun 部署与调试记录（已废弃）

以下内容仅记录旧 CloudRun 测试阶段的做法和构建故障排查。CloudRun 已删除；不要照此重新创建或部署容器服务。当前部署方式见上一节 `6.1 当前云函数部署配置`。

当前 GitHub 仓库 `Ljc798/veyon-campus-deployment` 已授权。`develop` 是 API 功能分支，当前工作区位于 `develop`；`main` 当前还没有部署包 API 文件。因此先从 `develop` 创建测试服务，待测试通过后再将功能合并到 `main` 并创建/更新正式服务。不要在未合并功能的 `main` 上创建正式 API 服务。

CloudBase Git 部署从 GitHub 拉取代码，不会读取本机工作区。创建服务前，确认根目录 `Dockerfile`、`.dockerignore` 和 API 代码已提交并推送到所选分支；首次验收保持自动部署关闭。

#### A. 确认 CloudRun 环境已开通

- [ ] 在 CloudBase 控制台确认当前环境为 `veyon-control`、地域为 `ap-shanghai`。
- [x] 2026-09-29 CLI 只读列表已看到 `veyon-control-dev` 状态正常；不要重复创建测试服务。仍需在控制台复核套餐、资源授权、运行版本和密钥。
- [ ] 开通前核对套餐和计费。腾讯云 API 文档说明 `CreateCloudRunEnv` 会创建环境并开通资源；CloudRun 文档说明开通后按实际用量计费且要求账户余额为正。[创建环境 API](https://cloud.tencent.com/document/api/1243/75707) · [计费说明](https://cloud.tencent.com/document/product/1243/48037)

#### B. 测试与正式服务

Git 部署会从 GitHub 选定分支拉取代码；CloudBase 的 GitHub 部署支持分支选择和按分支触发自动部署。[Git 部署说明](https://docs.cloudbase.net/en/run/deploy/deploy/deploying-git)

| 用途 | 服务名称 | 分支 | 当前建议 |
| --- | --- | --- | --- |
| 测试 | `veyon-control-dev` | `develop` | 已存在且 CLI 列表状态 normal；新源码手动部署后再验收健康、匿名发布、学生搜索下载 |
| 正式 | `veyon-telemetry` | `main` | 等 `develop` 验收并合并到 `main` 后再创建/更新 |

两个服务可以位于同一个 CloudBase 环境，但会共享该环境的 Auth、PostgreSQL 和 PG Storage。若 `develop` 在此环境发布测试包，它会写进当前部署包目录；需要彻底隔离测试数据时，应分别部署到独立的 CloudBase 测试环境和正式环境，并在两边各自应用数据库迁移、创建私有桶和密钥。

#### C. 部署表单

| 控制台字段 | 填写值 |
| --- | --- |
| 部署来源 | GitHub 仓库 `Ljc798/veyon-campus-deployment` |
| 测试服务名称 | `veyon-control-dev` |
| 分支 | `develop` |
| 自动部署 | 初次验收时关闭 |
| Dockerfile 目录 | 仓库根目录 |
| Dockerfile 名称 | `Dockerfile` |
| 服务端口（容器实际监听端口） | `9000` |
| 健康检查路径 | `/health`（如表单提供该项） |

正式服务使用同一仓库、服务名 `veyon-telemetry`、分支 `main`。本项目容器监听 `0.0.0.0:9000`；容器端口必须填 `9000`，不能填页面默认值 `80`。CloudBase 要求代码包包含 Dockerfile；仓库根目录 Dockerfile 会构建两个 .NET 项目。

#### D. 服务端环境变量

部署前或创建版本时，在云托管“环境变量设置”中只填写服务端变量。不要填写到网站前端环境变量，也不要上传到代码包。

| 变量 | 值 / 获取方式 | 必需 |
| --- | --- | --- |
| `CloudBase__EnvId` | `veyon-control-d3gs8hmuyd09c00a7` | 是 |
| `CloudBase__ApiKey` | 专用 CloudBase `service_role` / 服务端 API key；不要使用浏览器 `publish_key` | 是 |
| `Telemetry__DailyHashKey` | 至少 32 个随机字节的 Base64 字符串 | 是 |
| `CloudBase__DeploymentPackageBucket` | `deployment-package-artifacts`；源码有同一默认值，可省略 | 否 |
| `PORT` | 由云托管注入；应用也默认监听 9000 | 平台注入 |

`CloudBase__ApiKey` 是高权限服务端凭据；测试时建议专门创建、设置短期限（例如 30 天），只放在云托管环境变量中，测试完成后轮换或撤销。不得放入 ZIP、`website/.env.local`、静态资源、学生包或 Git。

Windows PowerShell 可生成 `Telemetry__DailyHashKey`：

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 32
$rng.GetBytes($bytes)
[Convert]::ToBase64String($bytes)
```

将生成值保存到服务端环境变量，不要把它写入本文或提交到 Git。缺少 `CloudBase__ApiKey` 或 `Telemetry__DailyHashKey` 时，服务会拒绝启动。

#### E. 创建服务后确认

- [x] CloudBase 只读核对显示测试服务 `veyon-control-dev` 状态为 `normal`、在线版本 `veyon-control-dev-007`、流量 100%、端口 9000；必需环境变量存在（值已脱敏）。
- [x] 已按序应用数据库迁移 `20260929041500`、`20260929093000`、`20260929140000`；迁移前部署包目录为 0 条，没有旧包被撤回。新版 API 仍需部署；`20260929093000` 替换了旧发布/下载 RPC，旧 CloudRun 版本需切换到新版代码后才能继续提供部署包接口。
- [ ] 部署本轮源码并验证新版本日志及 `/health` 返回 `{"status":"ready"}`。
- [ ] 在默认 HTTP 网关域名创建无需鉴权、子路径透传的 `/v1` → `veyon-control-dev` 路由，再验证学生端实际使用的部署包搜索和下载请求。
- [ ] 若构建失败，检查本地代码包是否将 `Dockerfile` 放在 ZIP 根目录，以及 ZIP 中是否包含根级 `Directory.Build.props` 和 `src/` 目录。

#### F. 构建错误 `NU1004`：NuGet 锁文件过期

若构建日志在 `dotnet restore ... --locked-mode` 阶段报告项目引用或 RuntimeIdentifier 与 `packages.lock.json` 不一致，先确认服务端 Dockerfile 使用 Core 的通用 `packages.lock.json`。Core 同时供 Linux 服务与 Windows 桌面端引用：通用锁文件只含 `net10.0`，教师/学生 Windows 打包使用 `packages.win-x64.lock.json`。`package-windows-offline.ps1` 会通过 `VeyonCampusCoreLockFile` 自动选择 Windows 锁文件；CloudRun 的 Server restore 不应选择它。

```sh
dotnet restore src/VeyonCampus.Telemetry.Server/VeyonCampus.Telemetry.Server.csproj --locked-mode
dotnet restore src/VeyonCampus.App/VeyonCampus.App.csproj -r win-x64 --locked-mode -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusCoreLockFile=packages.win-x64.lock.json
```

首次构建日志 `veyon-control-dev-002` 对应的差异是：Server 锁文件缺少 `VeyonCampus.Core` 项目引用，Core 锁文件还包含当前服务不使用的 `net10.0/win-x64` 条目。修正后，该版本的 `restore --locked-mode`、Release publish、镜像构建和推送均已通过；随后暴露的是容器启动配置问题，见下节。

后续 `veyon-control-dev-004` 日志又出现 `NU1004`：Windows RID restore 把 Core 通用锁文件改成含 `net10.0/win-x64`，而 CloudRun Server restore 不带 RID。当前 `develop` 已将 Windows RID 锁文件与 Server 通用锁文件分开；重新部署后应分别确认 Server 的 locked restore 和 Windows 打包 restore。构建阶段报错时容器尚未启动，因此不能据此判断端口、CloudBase 密钥或健康检查配置。

#### G. 容器启动错误：缺少 HMAC 密钥

`veyon-control-dev-002` 的启动日志报 `配置 Telemetry:DailyHashKey（至少 32 字节的 Base64 密钥）后才能启动`。在 CloudRun 服务环境变量中设置 `Telemetry__DailyHashKey`，值必须是至少 32 个随机字节编码的 Base64 字符串。macOS/Linux 可在本机运行 `openssl rand -base64 32` 生成，然后直接粘贴到 CloudRun 密钥配置；不要把生成值发到聊天、写进仓库或放入前端配置。

同时确认 `CloudBase__EnvId=veyon-control-d3gs8hmuyd09c00a7` 和 `CloudBase__ApiKey`（专用服务端 `service_role` key）已设置。程序会在校验 HMAC key 后继续校验这两个变量；若它们缺失，启动会报对应配置错误。`CloudBase__DeploymentPackageBucket` 可选，默认使用 `deployment-package-artifacts`。用户随后报告测试服务部署成功；部署后的健康、数据库和业务 API 验收仍需完成。

### 6.2 云函数部署后的 API 验收顺序

1. 确认 HTTP 云函数 `veyon-api` 为运行状态，默认网关域名的 `/` 路由已创建，`GET /health` 返回 `{"status":"ready"}`。
2. 确认网站 `VITE_API_BASE_PATH`、教师端和学生端使用同一个 HTTP 网关域名；网站跨域请求应正常，不依赖 Cookie。
3. 验证公开目录、教师发布和学生搜索。发布校区展示名可以使用中文，也不必与包内供 Veyon 密钥使用的标识一致；ZIP 限 64 KiB、单文件限 16 KiB、请求限 128 KiB。下载时检查错误后四位被拒绝，正确后四位成功，且返回文件的 SHA-256 与 ZIP 校验通过。
4. 用授权管理员账号验证撤回与教师授权管理路由；未登录或无管理员身份的请求必须被拒绝。
5. 用一次合成心跳验证 API 返回 204 与 PostgreSQL 写入；同一安装 ID、日期和包编号重复请求不得重复增加 `unique_devices`。
6. API 访问日志和应用日志中不得出现请求正文、原始安装标识、手机号后四位、HMAC 摘要或密钥。错码限频是进程内状态，云函数横向扩展时不提供全局限频保证；应同时配置网关 QPS 限频并观察异常请求。

### 6.3 发布影响与回滚

**影响范围：**首次部署会创建 HTTP 自定义镜像云函数并公开 HTTP 网关根路由。任何人都可以调用心跳、目录和发布 API，并可发布不超过 64 KiB 的公开配置包；新包会进入当前 CloudBase 环境的学生可搜索目录和私有桶。手机号后四位是低强度共享校验码，不是强身份认证；函数内错码限频按实例计数，不是集群级保护，需同时配置网关 QPS 限频。计算入口切换不需要更改 `kidscode.fun` DNS 或静态网站根目录。

**回滚：**先禁用 HTTP 网关根路由并撤销函数匿名访问规则；若仅需回滚代码，则把函数镜像版本切回上一个已验证版本。对测试发布包使用管理员撤回；保留 PostgreSQL 表、迁移和私有桶，不执行删表或删桶。恢复时重新部署经验证的镜像并重新启用根路由和所需访问规则。

## 7. 尚待核实与发布门禁

- [ ] 识别根域名 A 记录 216.198.79.1 的现有用途，确认是否把根域名流量迁入 CloudBase。
- [ ] 确认部署地区适用的 ICP/网站备案要求。
- [ ] 由环境管理员评估可用套餐或托管来源，完成安全域名配置，再进行 Auth + RLS 浏览器登录与校区写入验收。
- [ ] 确认当前套餐是否允许将最终浏览器 origin kidscode.fun 加入 CloudBase 安全域名；前端 RDB/Auth SDK 直接访问 CloudBase，需要该来源许可。
- [ ] 复核 PostgreSQL 存储/连接额度、备份与恢复能力、套餐费用以及预期遥测请求量。
- [ ] 准备 HTTP 云函数的 TCR 凭证、函数角色镜像拉取权限、CloudBase server API key 与原有 `Telemetry__DailyHashKey`；不得轮换 HMAC key。
- [ ] 部署 `veyon-api` 自定义镜像云函数，核对 `ap-shanghai`、Linux amd64、端口 9000、`/health` 和公开根路由设置。
- [ ] 检查静态托管 SPA fallback，准备仅更新 website/dist 的上传清单并保留平台现有文件。
- [ ] 保存当前网关路由、域名和证书资源状态，编制分阶段发布及回滚步骤。
- [ ] 检查匿名心跳告知、启用选择、限速、日志留存和数据保留说明。
- [ ] 准备完整发布预检摘要。

CloudBase 部署门禁要求在部署或公开云函数、发布静态网站、绑定自定义域名/HTTPS 前，先提交具体影响与回滚方案并获得最终确认。CloudRun 已删除；数据库迁移已应用；函数尚不存在、HTTP 网关没有 API 路由。待部署内容为 `veyon-api` HTTP 自定义镜像函数和公开根路由；上线后任何人都能调用公开 API 并发布 64 KiB 以内的配置包，写入当前环境目录和私有桶。

## 8. 当前验收状态

| 项目 | 状态 | 证据与限制 |
| --- | --- | --- |
| PostgreSQL migration plan/apply | 已通过 | 远端迁移历史包含 `20260928141201` 至 `20260929140000` 共 6 个版本；最后 3 个版本按序应用，任务 `task-cb7a4f85` 成功 |
| 表、角色种子与 RLS | 已核对 | 已登记站点角色及策略已核对；迁移前校区表与部署包目录为空 |
| PG Storage 部署包桶 | 已创建并核对 | 私有桶上限 64 KiB；没有 anon/authenticated 对象策略 |
| Web 代码与配置 | 已实现 | 管理区保留 Auth 登录与角色检查；`/publish` 改为公开无登录上传 |
| 网站生产构建 | 已通过 | Vite 生成公开 bundle 与延迟加载的管理模块；无浏览器构建内的 service API key |
| Auth 与实际数据库写入 | 待账号操作验收 | 管理员密码未提供；请后续使用真实账号验证，不在文档记录密码 |
| 本地 CloudBase 安全域名 | 当前套餐不支持添加 | 已尝试精确来源 127.0.0.1:5173，CloudBase 返回“当前套餐无法执行此操作”；未升级套餐 |
| 心跳与部署包 API | 数据库迁移已应用；云函数待首次部署 | 迁移历史已核对到 `20260929140000`；`veyon-api`、HTTP 网关根路由及端到端发布/下载/心跳仍待验收 |
| 域名和证书 HTTPS | 未完成 | 证书已签发，但域名未绑定、证书未部署，www DNS 也不存在 |

## 9. 发布后人工验收

1. 先以已有 administrator 账号登录并确认 owner 角色页可读。
2. 创建一条测试校区、刷新确认仍存在，再编辑并删除测试资料（正式环境删除操作须经数据库受权管理路径；当前 UI 无删除按钮）。
3. 使用 viewer/editor 角色分别确认只读和可编辑策略；确认匿名及无角色用户无法查询业务表。
4. 部署云函数并配置网关后检查 `/health`，再用测试安装 ID 发两次心跳，确认 unique_devices 加一、heartbeat_signals 加二；检查应用日志无原始 ID。
5. 确认静态站点 /admin 不在公共导航可见，未登录时显示登录页，数据库返回仍由 RLS 控制。
6. 在自定义域名绑定证书后分别检查 https://kidscode.fun 和 https://www.kidscode.fun 的 DNS、TLS 链、首页资源及 API 路由。
7. 确认回滚可恢复原 A 记录和网关规则，且 CloudBase 桶内平台文件仍在。
8. `/publish` 无需登录；使用测试校区名、教师姓名和后四位上传小于 64 KiB 的 ZIP。确认搜索结果不包含手机号后四位；错误后四位下载应失败，教师输入正确后四位后下载成功，并核对 SHA-256 与本地包校验。测试服务共用目录，操作前需取得发布确认并准备可撤回的测试包。

管理后台当前没有生产校区资料，学生包也尚未连接已验收的心跳协议。心跳数据库结构已迁移，CloudRun 当前版本与网关路由仍待核验；验收时看到空列表和零统计是当前预期状态。
