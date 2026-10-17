# 管理员下载页 CloudBase 部署预检

日期：2026-10-17（Asia/Hong_Kong）

## 本地代码

- `npm run build --prefix website` 成功。Vite 报告 `cloud-admin` 异步 chunk 为 987.63 kB（gzip 253.68 kB），仍超过 500 kB 提示阈值；构建没有错误。
- 下载同步处理器仅向固定 GitHub/GitCode Release API 请求最新稳定版；校验版本 tag、教师/学生 EXE 的唯一附件和精确固定仓库 URL。任一来源失败时不会调用数据库 RPC；保存使用一个事务 RPC 同时写入两个来源。
- 管理接口调用 `requireOwnerOrAdmin`；公共接口只读最小版本及下载 URL 字段。服务端处理器不回显请求头、环境变量或上游原始响应。
- 本地 OPA 为公共下载配置 GET 和两个后台管理路由分别声明访问规则。数据库迁移只向 `service_role` 授权，启用 RLS，并仅开放服务端原子写入 RPC。

## 线上只读检查

- 目标环境：`veyon-control-d3gs8hmuyd09c00a7`，上海，状态 `NORMAL`；PostgreSQL 已启用，静态域名路由为启用且可访问状态。
- 远端已有 19 条迁移，最新为 `20261016120000`。`20261016130000_create_site_download_settings.sql` 的只读迁移计划为 `executable=true`，当前尚未应用。
- `veyon-api` 处于 `Active`，运行时 Node.js 20.19，最后修改时间 `2026-10-16 18:21:47`。查询时隐藏了所有环境变量值；没有读取、修改或覆盖凭据。
- 线上 `authz.user.rego` 仍缺少 `/v1/download-page-config`、`/v1/admin/download-page` 和 `/v1/admin/download-page/sync` 访问规则；当前本地 Rego 已包含它们。静态托管保留 `index.html` 及 SPA 404 回退配置。
- 没有写入 CloudBase。未以站点 owner/admin 身份运行下载同步，也未通过浏览器验证登录、后台操作和公开页面。

## 部署顺序与回退

部署需要按顺序执行：

1. 应用 `20261016130000_create_site_download_settings` 迁移，创建仅由服务端访问的配置表和双来源事务 RPC。
2. 更新 `veyon-api` 代码，同时保留线上现有函数环境变量及其值。
3. 将本地 `cloudbase/authz.user.rego` 设置为用户策略；该操作会立即影响函数网关访问。
4. 构建并发布 `website/dist` 静态站点，然后验证后台和公开下载页。
5. 使用有效 owner/admin 会话在后台执行一次双源同步，核对两个版本及四个角色直链。

发布前需要先把线上原 OPA 内容保存为本地回退文件，并备份静态托管根目录。CloudBase CLI 的静态托管上传会覆盖同路径文件，且没有内建版本历史；预检尚未下载这份备份。函数更新应仅更新代码、不改环境变量，并在操作前导出当前函数包或确认可用的旧发布版本；这一步也尚未完成。

如果部署后的接口或页面有问题，恢复刚保存的函数包、OPA 文件和静态站点备份。数据库迁移只新增表/RPC，旧应用不依赖它；回退时先保留该表即可。首次同步失败时 RPC 原子性应保持原有两源配置。若确需删除迁移对象，应先确认表中没有需要保留的同步记录，再另行审阅和执行回退 SQL。上述备份、恢复操作本次均未执行。

## 门禁

CloudBase `cloudbase` 与 `cloudbase-platform` 部署门禁要求对公开函数、数据库迁移、用户 OPA 策略及静态网站的线上变更先取得明确确认。本记录只完成准备和只读预检；线上部署与首次同步仍待确认和现场验收。`specs/admin-download-settings/tasks.md` 的第 6、7 项继续保持未完成。

## 追加只读复核（2026-10-17）

- CloudBase MCP 复查仍显示 PostgreSQL `public` 只有 12 个对象，没有 `site_download_settings`；`veyon-api` 仍为 Active、Node.js 20.19，修改时间仍为 `2026-10-16 18:21:47`。函数环境变量值保持隐藏。
- 线上 `authz.user.rego` 仍未放行 `/v1/download-page-config`、`/v1/admin/download-page`、`/v1/admin/download-page/sync`。`kidscode.fun/` 仍将根路径路由到启用的静态托管。
- 静态托管清单为 71 个对象、共 7,087,822 字节；列表包含站点根目录及历史哈希资源。只读取清单，没有下载、覆盖或删除对象。
- 为部署回退准备静态站点快照时，本机对 `kidscode.fun` 的直接 HTTP 读取持续等待，随后主动停止；CloudBase CLI 3.8.5 的 `tcb hosting download / ... --dir --env-id veyon-control-d3gs8hmuyd09c00a7` 在刷新凭据时因 `https://iaas.cloud.tencent.com/tcb_refresh` 网络超时退出，未创建备份目录。没有生成可用于回滚的静态文件副本。
- 本轮只执行 CloudBase 只读查询和失败的本机读取尝试，没有修改 CloudBase、托管文件或用户凭据。部署前仍须在网络可达的维护终端成功保存 OPA、函数包和全部静态站点文件，再按部署门禁取得明确确认。

## MCP 只读复核与迁移计划（2026-10-17）

- 目标环境仍为上海 `veyon-control-d3gs8hmuyd09c00a7`，状态 `NORMAL`，个人版；PG 已启用。`public` schema 仍为 12 个应用表，没有 `site_download_settings`。
- `planMigration` 对 `20261016130000_create_site_download_settings.sql` 返回 `executable=true`：远端 19 条迁移均与版本匹配，最新为 `20261016120000`，新增迁移是唯一待应用项。未运行 `applyMigration`。
- `veyon-api` 仍为 Active、Node.js 20.19，代码状态 `success`，最后修改时间 `2026-10-16 18:21:47`。查询隐藏了 9 项环境变量的值；没有读取或修改凭据。
- 线上用户 OPA 仍未放行 `/v1/download-page-config`、`/v1/admin/download-page`、`/v1/admin/download-page/sync`。`kidscode.fun/` 路由仍启用并指向静态托管；`index.html` 首页与 SPA 404 回退配置保持可用。
- 托管文件仍是 71 个、合计 7,087,822 字节，最新对象修改时间为 `2026-10-16 03:49:04 UTC`。改用 CloudBase 默认静态域名读取 `index.html` 时，DNS 在 3 秒内解析超时；没有下载或写入文件。
- 本次仅完成上述只读检查和数据库迁移 dry-run。没有部署函数、OPA、数据库迁移或网站；备份条件仍未满足。

## 当前源码与线上静态对象对照（2026-10-17）

- CloudBase `queryHosting` 返回默认静态域名配置正常且 `accessUrlReachable=true`，并提供了托管对象清单；清单接口不提供对象字节。本机对默认静态域名和 `kidscode.fun` 的 HTTPS 请求都在 DNS 解析阶段超时，网页读取工具也无法打开两个域名。
- 重新构建当前工作区网站成功，但构建产物不能还原线上首页：本地 `index.html` 为 7,382 字节，线上为 7,433 字节；本地 `styles-B_5eG9u6.css` 为 70,313 字节，线上对象 `styles-X1ZmOwR2.css` 为 69,221 字节；本地 `cloud-admin-CTCrPYJg.js` 为 987,632 字节，线上 `cloud-admin-Cas_nhFC.js` 为 979,291 字节；OpenAPI 文件本地为 48,272 字节、线上为 43,897 字节。当前构建的哈希资源名也未出现在完整线上托管对象清单中。
- 因此当前源码构建不能代替线上回退副本。本次没有下载对象字节，没有修改 CloudBase，也没有部署网站；上线前仍需要从网络可达的维护终端取得并校验线上静态文件备份。

## 静态托管只读备份（2026-10-17）

- 用户明确确认仅备份环境 `veyon-control-d3gs8hmuyd09c00a7`，不部署或修改线上资源。
- CloudBase MCP 只读 `queryHosting` 确认静态域名为 `veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com`，清单为 71 个对象。通过 DNS-over-HTTPS 获取域名 IP 后，使用保留 HTTPS 主机名和证书校验的固定 IP 只读 GET，完成全量本地备份；普通首页、`__auth` 登录页和 `old/` 页面均返回 HTTP 200。
- 备份目录：`/Users/alex/Documents/veyon-campus-deployment-backups/cloudbase-hosting-20261017/`。包含 68 个实际文件和 `backup-manifest.json`；71 个清单对象中另有 3 个零字节目录标记。文件合计 7,087,822 字节。
- 每个文件的 HTTP 响应大小和 ETag 均与托管清单相符；本地逐个重算 SHA-256，68/68 全部通过，文件清单无缺失或额外文件。Manifest 保存每个对象的大小、ETag 与 SHA-256。
- 本次只读取线上静态对象并写入上述仓库外目录，没有部署、删除或更改 CloudBase。静态站点回退副本现已完成；线上 OPA 与 `veyon-api` 函数包备份仍未完成，不能据此认为生产部署已获授权或具备完整回退条件。
