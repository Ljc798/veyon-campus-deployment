# 管理员下载页版本同步：实施任务

- [x] 1. 新增按来源隔离的私有设置表和安全事务 RPC；校验固定来源、版本、附件名和操作者。
- [x] 2. 在 Cloud Function 实现 GitHub/GitCode 服务端 Release 查询、完整校验和原子同步接口。
- [x] 3. 新增公共下载页配置接口，并在 OpenAPI、匿名 API 路由策略和 API 契约清单中登记。
- [x] 4. 在 `/admin/downloads` 提供当前值、每源版本、同步按钮、忙碌/失败/成功状态和 owner/admin 限制。
- [x] 5. 将公开 `/download` 页面改为显示从公共接口读取的每源版本和已验证链接，并提供读取失败状态。
- [ ] 6. 对网站做构建和手工界面核查；对 CloudBase 权限、事务更新、双源失败、直链与版本显示做独立检查。
- [ ] 7. 更新网站与 CloudBase 文档、路线图和实施记录；生产部署按 CloudBase 部署门禁另行处理。

进度记录（2026-10-17）：网站 `npm run build --prefix website` 已通过；下载同步云函数 `node --check` 和 `git diff --check` 已通过。GitCode 查询现在显式选择 `type=latest` 并验证 `release_status=latest`。本地浏览器、CloudBase 迁移/权限/事务及发布直链尚未验收，因此第 6 项保持未完成。网站与 CloudBase 文档及实施记录已补充；路线图文件已有用户工作区改动，本次未修改或暂存它，因此第 7 项仍待完成。

CloudBase 只读核对（2026-10-17）：共享环境 `veyon-control-d3gs8hmuyd09c00a7` 的 PostgreSQL 仍只有 12 个应用对象，尚无 `public.site_download_settings`；线上用户 OPA 策略未放行 `/v1/download-page-config`、`/v1/admin/download-page` 或 `/v1/admin/download-page/sync`。线上 `veyon-api` 当前为 Active，函数详情的修改时间为 `2026-10-16 18:21:47`。迁移预检返回 `executable=true`，19 条已应用迁移均匹配，仅 `20261016130000` 待应用。现有静态站点和 `kidscode.fun` 路由可用。以上均为只读核对，没有写入 CloudBase。下载同步要在线工作，仍需按部署门禁应用本地迁移、更新 OPA 策略并部署函数和静态网站；之后再由 owner/admin 执行首次同步并完成端到端验收。

静态代码复核（2026-10-17）：首次同步前 API 返回空配置时，下载页继续使用各来源最近确认的备用版本并提示“管理员尚未同步”；读取失败时也保留这些下载链接。该项通过代码阅读确认，未做浏览器手工验收；任务 6 仍保持未完成。

来源版本回退修正（2026-10-17）：GitHub `releases/latest` 返回正式 `v1.0.13` 且两个 EXE 已上传；CloudBase `application_releases` 也有 TeacherConsole、StudentSetup `v1.0.13` published 记录。GitCode 最后可核对的仓库记录仍是 `v1.0.12`，本机对 GitCode API 的只读请求超时，不能断言 `v1.0.13` 已上传。网站备用版本现按来源固定为 GitHub `1.0.13`、GitCode `1.0.12`，不再从当前桌面源码版本推导；管理员一键同步成功后仍显示两个来源各自的最新正式版本。`npm run build --prefix website` 已通过；CloudBase 代码审阅为静态复核，未运行测试。任务 6、7 因线上部署和现场验收仍保持未完成。

附件地址核验加强（2026-10-17）：服务端同步除校验附件唯一性外，还要求 GitHub/GitCode Release API 返回的 `browser_download_url` 与固定仓库、版本 tag、安装器文件名构成的 HTTPS 地址完全相等；缺失或偏离时整次同步失败，数据库保持旧值。此改动未改变 API 响应或数据库结构。GitCode API 当前在本机 DNS 解析超时，尚未能对 GitCode 实际返回地址做线上核验；本地语法检查和代码审阅完成，未运行自动化测试，任务 6、7 仍未完成。
