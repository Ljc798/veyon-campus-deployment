# CloudBase、PostgreSQL、域名与 HTTPS 接入记录

记录日期：2026-09-28  
目标环境：veyon-control（veyon-control-d3gs8hmuyd09c00a7，ap-shanghai）  
目标域名：kidscode.fun

本文记录实际已执行的 CloudBase 前后端与 PostgreSQL 接入，以及仍未公开发布的资源状态。CloudBase PG 迁移已执行；网站管理员认证、数据库读写代码已实现。静态网站部署、CloudRun 遥测服务部署、自定义域名和证书绑定仍待后续预检和发布确认。

## 1. 当前资源状态

| 项目 | 状态 | 记录 |
| --- | --- | --- |
| CloudBase 环境 | 已确认 | veyon-control，上海 ap-shanghai |
| 环境套餐 | 体验版 | 控制台显示至 2027-03-28；生产前须复核费用和配额 |
| CloudBase Auth | 已配置 | 用户名密码登录开启；当前无公开注册入口 |
| 管理员角色 | 已初始化 | 已有 administrator Auth 账号被迁移为 owner；账号 ID 不写入仓库 |
| Publishable key | 已创建并本机配置 | 保存在 website/.env.local（Git 忽略）；仅供浏览器 SDK 使用，不是 service API key |
| PostgreSQL | 已开通并迁移 | 8 张业务表；含部署包目录表、RLS、索引和心跳聚合 RPC |
| 迁移版本 | 已成功执行 | 最新版本 20260929032900；保留 20260928141201 与 20260929021100 |
| 部署包目录迁移 | 已应用并核对 | CloudBase CLI 3.8.4 任务 `task-7da92226` 成功；远端迁移历史有该版本，3 张表和 RLS 均已确认 |
| 网站 Auth + PG 前端 | 已实现 | 登录、角色检查、校区读写、每日汇总查询；需用有效管理员密码完成浏览器验收 |
| 教师发布 / 学生检索 UI | 已接入源码 | 网站 `/publish` 上传 ZIP 或配置文件夹；学生工具按校区/前缀搜索、下载并校验；CloudRun 未部署，运行态待验收 |
| 服务端 API 源码 | 已实现 | 含 HMAC 心跳、教师部署包上传/撤回/授权管理、学生搜索/下载；部署包下载前比对对象 SHA-256 |
| CloudRun | 未部署 | 尚无运行中的 API 服务；service API key 和 HMAC key 未注入托管环境 |
| 静态网站 | 未发布本项目构建 | CloudBase 仍有已有默认站点资源；不得清空或覆盖自带认证文件 |
| kidscode.fun | 尚未绑定 | 目前不代表根域名网站已迁到 CloudBase |
| HTTPS 证书 | 已签发、未绑定 | 90 天免费 DV 证书已签发，尚未部署到 CloudBase 入口 |

网站业务时间戳按 UTC+8（`Asia/Hong_Kong`）显示，PostgreSQL 继续以 `timestamptz` 保存真实时间点；匿名遥测的 `day_utc` 仍按 UTC 自然日统计并在页面注明。

CloudBase 默认静态域名为 `veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com`。HTTP 网关默认域名（按控制台“域名管理”读取）为 `veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com`。HTTP 网关当前路由列表为空；本项目的 `/` 与 `/api` 路由尚未创建。CloudBase 静态存储桶内已有平台自带认证辅助文件和 `cloud-admin/index.html`；后续静态发布应只上传网站构建目录内需要更新的静态资源，不删除整个存储桶。

2026-09-29 默认域名访问检查：静态托管的开发测试提示页是 CloudBase 的预期中间页；确认访问后根路径返回 `404 NoSuchKey`，缺少 `index.html`，说明项目构建尚未上传到站点根目录。HTTP 网关控制台显示的准确域名包含静态应用编号 `-1348081197`；漏掉这段编号的 `veyon-control-d3gs8hmuyd09c00a7.ap-shanghai.app.tcloudbase.com` 返回 `INVALID_ENV`。控制台路由列表为空，访问准确域名的 `/` 与 `/api/health` 均返回 `INVALID_PATH`。此前记录的 `.service.tcloudbase.com` 推测不正确，以控制台显示值为准。

2026-09-29 上传结果：MCP 上传接口在刷新认证时返回 `network timeout at https://iaas.cloud.tencent.com/tcb_refresh`；之后通过已登录的 CloudBase 控制台成功上传了 `index.html` 到根目录，以及 4 个资源到 `assets/`。逐项以 SHA-256 比对远端响应与本机构建，5 个对象完全一致。第一次选择整个 `dist` 文件夹时，控制台将其保留为额外的 `/dist/` 副本；未删除该副本。静态默认域名 `/` 返回 200；直接访问 `/admin` 与 `/admin/campuses` 返回 404，SPA 回退尚未配置。HTTP 网关仍无路由，`/api` 与遥测服务尚未接通。

## 2. PostgreSQL 数据库与身份授权

迁移文件：

    cloudbase/migrations/20260928141201_create_site_database.sql

五张应用表：

- admin_profiles：将 CloudBase Auth 用户绑定到 owner/admin/editor/viewer 站点角色。
- campuses：名称、地区、城市和 active/paused 状态，供授权管理员维护。
- telemetry_daily_devices：仅保存 UTC 日期加 HMAC 摘要，按日去重。
- telemetry_daily_stats：每日活跃安装数、心跳请求数和更新时间。
- telemetry_retention_state：控制逐日明细按日清理。

RLS 与 grants 已在迁移中设置。后台必须先有 Auth 会话并读到 admin_profiles 中的角色；没有角色记录时拒绝访问。campuses 和 telemetry_daily_stats 的行策略只允许已登记站点角色读取；editor 可新增/编辑，owner/admin 另有删除策略但当前页面不提供删除操作。角色表没有浏览器写策略。遥测摘要表不允许浏览器角色访问，只有服务端 service_role RPC 写入。

最初的管理员账号已被映射成 owner。由于本任务未提供管理员密码，本地浏览器认证及校区保存流程尚未以该账号完成真实登录操作；权限种子和策略已从 CloudBase PG 读取核对。

2026-09-29 已应用教师发布/学生检索的部署包目录迁移 `cloudbase/migrations/20260929021100_create_deployment_package_catalog.sql`。CloudBase MCP 的 `tcb_refresh` 仍超时；改用 CloudBase CLI 3.8.4，经本机系统代理完成登录、迁移预览与应用。任务 `task-7da92226` 状态为 Succeed。只读 SQL 确认新增三张目录表都启用了 RLS，预期策略、函数、触发器和索引均存在。

同日已应用 `cloudbase/migrations/20260929032900_create_deployment_package_api.sql`，CLI 任务 `task-156ede6a` 状态为 `Succeed`。迁移新增私有 PG Storage 桶和六个 API RPC。远端只读 SQL 已确认桶 ID `deployment-package-artifacts`、`public=false`、大小上限 524288 字节、MIME 白名单 `application/zip`；`storage.buckets` / `storage.objects` 没有 `anon` 或 `authenticated` 对象策略。六个 RPC 只有 `service_role` 可执行，目录表仍启用 RLS。当前桶无对象，目录无真实包。

部署包 API 源码已加入现有 .NET 10 服务 `src/VeyonCampus.Telemetry.Server/DeploymentPackageEndpoints.cs`：教师需携 CloudBase Auth access token；上传可传一个 ZIP，或通过 `files` 多文件字段传单层配置文件夹；学生可搜索目录、下载已发布 ZIP；发布者可撤回，owner/admin 可管理教师校区权限。网站 `/publish` 通过 API 查询当前用户可发布校区、上传和撤回；学生 App 通过 CloudBase HTTP 网关搜索和下载，并复用本地 ZIP 校验流程。CloudRun 尚未部署，因此这些 HTTP 路由目前没有公网地址，云端功能不能端到端使用。

同日排查 CLI 网络：npm 原 registry 指向 `registry.npmmirror.com`，直接域名解析失败；本机 macOS 已有 HTTP/HTTPS 系统代理，但 npm 未自动使用。通过命令级 registry 与 proxy 参数访问官方 npm registry 后，CloudBase CLI 登录成功。全局安装因 `@cloudbase/cloudbase-mcp` 已占用同名 `cloudbase-mcp` 可执行文件而冲突，因此使用 `npm exec --package=@cloudbase/cli` 运行 CLI，没有覆盖现有 MCP 命令，也没有改写持久 npm 配置。CloudBase MCP 的 `tcb_refresh` 请求仍超时；可在该 MCP 进程环境增加 `HTTP_PROXY` / `HTTPS_PROXY` 后重启并复测，这一代理配置尚未改动或验证。

CloudBase PG Storage 桶与静态网站托管桶是独立资源。部署包只写入私有 PG 桶，不进入网站静态文件桶。

更完整的数据粒度、字段、限制、保留期和扩容方案见 [PostgreSQL 数据库设计](PostgreSQL数据库设计.md)。

## 3. 网站本地运行与配置

网站目录 website 使用 Vite 和 CloudBase JavaScript SDK。首次安装和启动：

    cd website
    npm ci
    if [ ! -f .env.local ]; then cp cloudbase.env.example .env.local; fi
    npm run dev

Vite 默认地址是 http://127.0.0.1:5173。已取得的 publishable key 存在本机忽略文件 website/.env.local；不要复制到示例文件或提交 Git。真实浏览器认证还需要将 127.0.0.1:5173 加入 CloudBase 环境安全域名。核对时只允许添加实际使用的本地来源，不要使用通配符；kidscode.fun 在最终域名接入前也要加入允许域名。

本轮已按实际 Vite 地址尝试添加 127.0.0.1:5173；CloudBase 返回“当前套餐无法执行此操作”。因此本地浏览器直连 Auth/RDB 的 CORS 验证暂时受套餐限制，不能用此失败推断前端身份代码或 RLS 不正确。没有为此升级套餐；后续需由环境管理员评估可用套餐或选用 CloudBase 已允许的托管来源进行验收。

前端环境变量如下：

| 名称 | 说明 | 可否进入浏览器构建 |
| --- | --- | --- |
| VITE_CLOUDBASE_ENV_ID | CloudBase 环境 ID | 可以 |
| VITE_CLOUDBASE_REGION | 环境地域 | 可以 |
| VITE_CLOUDBASE_PUBLISHABLE_KEY | 浏览器专用 publishable key，受 Auth 与 RLS 限制 | 可以 |
| VITE_API_BASE_PATH | 同域遥测 API 前缀，默认 /api | 可以 |
| CloudBase__ApiKey | 服务端 `service_role` API Key，用于 PostgreSQL RPC 和私有 PG Storage | 不可以 |
| CloudBase__DeploymentPackageBucket | 部署包 PG Storage 桶 ID；默认 `deployment-package-artifacts` | 不可以 |
| Telemetry__DailyHashKey | 服务端 HMAC 日期密钥 | 不可以 |

生产构建目录是 website/dist。不得将 .env.local、CloudBase service API key、数据库密码或 HMAC 密钥放入静态站点、Docker build args、学生安装包或版本库。

## 4. 应用数据流

### 管理工作区

1. 管理员直接打开 /admin；公开首页、导航、页脚和产品展示页没有后台链接。
2. 浏览器用 CloudBase Auth 用户名密码登录并恢复安全会话。
3. 页面按当前用户 ID 查询 admin_profiles；无角色记录即显示无权限。
4. 页面通过 CloudBase Web RDB SDK 读取真实校区与每日统计，新增和编辑校区时直接写入 public.campuses。
5. 数据库 grants 和 RLS 是实际授权边界；前端不持有管理员令牌或 service role key。

公开站点可以被直接输入 /admin，但会先显示登录界面；只有已授权登录用户才能取得站点数据。隐藏公共入口减少误点，不代替 Auth/RLS。

### 桌面 Agent 心跳

1. 若学生包的 telemetryEndpoint 为空，Agent 不发送心跳。
2. 已配置时使用 POST /api/v1/heartbeat，body 只包含随机 installationId。
3. 服务端按当前 UTC 日执行 HMAC-SHA256，丢弃原始安装标识。
4. 服务端调用 CloudBase PostgreSQL HTTP RPC；原子更新每日汇总。
5. /api/health 只报告遥测服务存活，不能证明 PG 管理后台的 Auth 或 RLS 正常。

计划中的同域网关路由：

| 公网路径 | 目标 | 去除前缀后的服务路径 |
| --- | --- | --- |
| 网站静态文件 | STATIC_STORE | 原路径 |
| /api/health | CloudRun veyon-telemetry | /health |
| /api/v1/heartbeat | CloudRun veyon-telemetry | /v1/heartbeat |
| /api/v1/deployment-packages 及子路径 | CloudRun veyon-telemetry | /v1/deployment-packages 及子路径 |
| /api/v1/deployment-package-publishers | CloudRun veyon-telemetry | /v1/deployment-package-publishers |

该网关配置尚未创建，API 公网路径现在不可用。部署包接口契约见 [部署包云端分发设计](部署包云端分发设计.md)，心跳详情见仓库根目录 docs/anonymous-usage-telemetry.md。

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

## 6. 遥测服务容器

项目位置：src/VeyonCampus.Telemetry.Server。容器构建文件：src/VeyonCampus.Telemetry.Server/Dockerfile。CloudRun 建议服务名 veyon-telemetry、容器端口 9000、健康检查 /health；Docker build context 需为仓库根目录，以包含 Directory.Build.props。

服务启动必须配置：

| 托管环境变量 | 用途 | 要求 |
| --- | --- | --- |
| PORT | CloudRun 注入的监听端口 | 可由运行平台注入；应用绑定 0.0.0.0 |
| CloudBase__EnvId | CloudBase 环境 ID | 仅服务端 |
| CloudBase__ApiKey | CloudBase `service_role` API Key | 仅托管平台密钥；调用 PG RPC 和私有 PG Storage，不进入前端 |
| Telemetry__DailyHashKey | HMAC 日期密钥 | 至少 32 个随机字节的 Base64 编码 |

当前还没有创建或注入 CloudBase service API key，也没有部署 CloudRun。不要把 publishable key 错当服务端密钥。部署包 API 的 `CloudBase__DeploymentPackageBucket` 可省略（默认值就是已创建的桶 ID）。匿名心跳 API 上线后可公开写入，部署前还需确定限速、滥用监控和托管访问日志保留方式；应用和平台不得记录请求正文、原始安装标识、HMAC 摘要或密钥。

## 7. 尚待核实与发布门禁

- [ ] 识别根域名 A 记录 216.198.79.1 的现有用途，确认是否把根域名流量迁入 CloudBase。
- [ ] 确认部署地区适用的 ICP/网站备案要求。
- [ ] 由环境管理员评估可用套餐或托管来源，完成安全域名配置，再进行 Auth + RLS 浏览器登录与校区写入验收。
- [ ] 确认当前套餐是否允许将最终浏览器 origin kidscode.fun 加入 CloudBase 安全域名；前端 RDB/Auth SDK 直接访问 CloudBase，需要该来源许可。
- [ ] 复核 PostgreSQL 存储/连接额度、备份与恢复能力、套餐费用以及预期遥测请求量。
- [ ] 为 CloudRun 生成独立服务端 API key 和 HMAC key，配置托管密钥。
- [ ] 核对 CloudRun 构建上下文、端口、健康探针、服务最小副本和日志设置。
- [ ] 检查静态托管 SPA fallback，准备仅更新 website/dist 的上传清单并保留平台现有文件。
- [ ] 保存当前网关路由、域名和证书资源状态，编制分阶段发布及回滚步骤。
- [ ] 检查匿名心跳告知、启用选择、限速、日志留存和数据保留说明。
- [ ] 准备完整发布预检摘要。

CloudBase 部署门禁要求在发布静态网站、部署/暴露 CloudRun、绑定自定义域名/HTTPS 之前，先提交具体影响与回滚方案并获得最终确认。本轮完成私有 PG Storage 桶、数据库 RPC、API 源码与文档；尚未部署公网 CloudRun、创建网关路由或改 DNS。

## 8. 当前验收状态

| 项目 | 状态 | 证据与限制 |
| --- | --- | --- |
| PostgreSQL migration plan/apply | 已通过 | 版本 20260928141201、20260929021100、20260929032900 均已应用 |
| 表、角色种子与 RLS | 已核对 | 8 张业务表，owner 角色存在，校区表与部署包目录为空，已检查策略 |
| PG Storage 部署包桶 | 已创建并核对 | 私有桶、512 KiB 上限、ZIP MIME 白名单；没有 anon/authenticated 对象策略 |
| Web 代码与配置 | 已实现 | CloudBase Web SDK、Auth 登录、角色检查、校区数据读写和统计查询 |
| 网站生产构建 | 已通过 | Vite 生成公开 bundle 与延迟加载的管理模块；无浏览器构建内的 service API key |
| Auth 与实际数据库写入 | 待账号操作验收 | 管理员密码未提供；请后续使用真实账号验证，不在文档记录密码 |
| 本地 CloudBase 安全域名 | 当前套餐不支持添加 | 已尝试精确来源 127.0.0.1:5173，CloudBase 返回“当前套餐无法执行此操作”；未升级套餐 |
| 心跳与部署包 API | 源码已实现，运行态待发布 | CloudRun 与 service API key 尚未设置；没有公网网关路由，无法端到端调用 |
| 域名和证书 HTTPS | 未完成 | 证书已签发，但域名未绑定、证书未部署，www DNS 也不存在 |

## 9. 发布后人工验收

1. 先以已有 administrator 账号登录并确认 owner 角色页可读。
2. 创建一条测试校区、刷新确认仍存在，再编辑并删除测试资料（正式环境删除操作须经数据库受权管理路径；当前 UI 无删除按钮）。
3. 使用 viewer/editor 角色分别确认只读和可编辑策略；确认匿名及无角色用户无法查询业务表。
4. 部署 API 后检查 /health，再用测试安装 ID 发两次心跳，确认 unique_devices 加一、heartbeat_signals 加二；检查应用日志无原始 ID。
5. 确认静态站点 /admin 不在公共导航可见，未登录时显示登录页，数据库返回仍由 RLS 控制。
6. 在自定义域名绑定证书后分别检查 https://kidscode.fun 和 https://www.kidscode.fun 的 DNS、TLS 链、首页资源及 API 路由。
7. 确认回滚可恢复原 A 记录和网关规则，且 CloudBase 桶内平台文件仍在。

管理后台当前没有生产校区资料，桌面 Agent 也尚未接入线上遥测服务。验收时看到空列表和零统计是正确初始状态。
