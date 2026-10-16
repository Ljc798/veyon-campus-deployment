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
