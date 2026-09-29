# Veyon Campus 网站

网站包含公开产品介绍页、通过 CloudBase Auth 和 PostgreSQL 授权的管理工作区，以及需要登录的教师部署包发布入口。管理工作区不出现在公开导航；教师可从页脚打开 `/publish`，但只有获授权校区可以发布。

## 本地开发

需要 Node.js 20 或更新版本。首次进入网站目录安装依赖：

```sh
cd website
npm ci
```

将 `cloudbase.env.example` 复制为 `.env.local`，填写 CloudBase 环境 ID、地域、publishable key 和 API 前缀。环境 ID、地域和 publishable key 是浏览器客户端配置；不得在此文件写入 service role / service API key、数据库密码或其他服务端密钥。`.env.local` 已被 Git 忽略。

```sh
if [ ! -f .env.local ]; then cp cloudbase.env.example .env.local; fi
npm run dev
```

仅在首次本地配置时复制示例文件；不要覆盖已经填写真实 publishable key 的 `.env.local`。

Vite 默认监听 `http://127.0.0.1:5173`。公开页面可直接访问；管理工作区位于 `/admin`，教师发布入口位于 `/publish`，两者都使用 CloudBase 已启用的用户名密码账号。该网站没有本地虚构后台，开发与生产都运行同一套 Auth + PostgreSQL 页面。当前 CloudBase 套餐拒绝添加本地来源，因此本地 Auth/RDB 端到端验收须先解决安全域名限制。

本地 CloudBase 登录和数据库请求还要求将 `127.0.0.1:5173` 加入环境安全域名；生产使用前应将实际站点域名加入安全域名配置。本地开发服务器未配置 CloudBase 允许来源时，公开页面仍可预览，但认证和数据库请求会被浏览器拦截。

## 构建与预览

```sh
npm run build
npm run preview
```

输出位于 `dist/`。生产构建不包含虚构后台数据页，只包含使用 CloudBase 登录的真实管理工作区代码。公开静态文件发布之前仍需完成 CloudBase 环境、静态托管回退、API 服务和域名发布预检；本地构建不会自动部署。

## 前后端连接

- `app.js` 负责公共站点路由；进入 `/admin` 或 `/publish` 时才延迟加载 `src/cloud-admin.js`。
- `src/cloudbase.js` 使用 CloudBase Web SDK 初始化 Auth 和 PostgreSQL RDB，浏览器只使用 publishable key。
- `src/admin-data.js` 读取当前账号角色、校区列表和按日心跳汇总，并通过数据库写入校区资料。
- `src/cloud-admin.js` 要求有效 Auth 会话，再查询 `admin_profiles`；数据库行级安全策略（RLS）是实际授权边界。
- `/publish` 通过服务端 `/v1/deployment-package-publishers/me` 取得当前账号可发布的校区，上传 ZIP 或配置包文件夹，并可查询或撤回已发布包。教师上传权限仍由服务端按账号和校区验证。
- 学生桌面端直接访问 CloudBase HTTP 网关的 `/v1/deployment-packages` 目录与下载接口；默认服务地址见 `DeploymentPackageCatalogClient`，可通过 `VEYONCAMPUS_DEPLOYMENT_PACKAGES_API_BASE_URL` 覆盖。
- `src/VeyonCampus.Telemetry.Server` 接收匿名心跳，将按日 HMAC 摘要通过 CloudBase PostgreSQL HTTP API 写入数据库。service API key 只供服务端运行时读取。
- 页面使用同源相对 API 地址 `/api`；计划中的网关会把 `/api/health` 转发到服务端 `/health`，把 `/api/v1/heartbeat` 转发到 `/v1/heartbeat`。

## 路由

| 路由 | 页面 |
| --- | --- |
| `/` | 公开首页 |
| `/product` | 产品能力 |
| `/about` | 关于项目 |
| `/docs` | 使用指南 |
| `/contact` | 联系与反馈 |
| `/privacy` | 隐私说明 |
| `/admin` | 管理员登录 / 数据总览 |
| `/admin/campuses` | 校区管理 |
| `/admin/usage` | 匿名使用汇总 |
| `/admin/analytics` | 趋势分析 |
| `/admin/settings` | 账号、角色和数据保留说明 |
| `/publish` | 教师校区配置包上传、查询和撤回 |

## 相关资料

- `docs/开发规范.md`：页面、代码与内容边界。
- `docs/CloudBase接入与验收.md`：环境、身份、迁移、域名、HTTPS、发布门禁和验收记录。
- `docs/PostgreSQL数据库设计.md`：数据模型、RLS、保留期限和迁移流程。
- `docs/网站功能与验收说明.md`：页面行为、真实数据范围和后续人工验收单。
- 仓库根目录 `docs/anonymous-usage-telemetry.md`：桌面 Agent 心跳协议和遥测服务配置。
