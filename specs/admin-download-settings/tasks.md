# 管理员下载页版本同步：实施任务

- [x] 1. 新增按来源隔离的私有设置表和安全事务 RPC；校验固定来源、版本、附件名和操作者。
- [x] 2. 在 Cloud Function 实现 GitHub/GitCode 服务端 Release 查询、完整校验和原子同步接口。
- [x] 3. 新增公共下载页配置接口，并在 OpenAPI、匿名 API 路由策略和 API 契约清单中登记。
- [x] 4. 在 `/admin/downloads` 提供当前值、每源版本、同步按钮、忙碌/失败/成功状态和 owner/admin 限制。
- [x] 5. 将公开 `/download` 页面改为显示从公共接口读取的每源版本和已验证链接，并提供读取失败状态。
- [ ] 6. 对网站做构建和手工界面核查；对 CloudBase 权限、事务更新、双源失败、直链与版本显示做独立检查。
- [ ] 7. 更新网站与 CloudBase 文档、路线图和实施记录；生产部署按 CloudBase 部署门禁另行处理。

进度记录（2026-10-17）：网站 `npm run build --prefix website` 已通过；下载同步云函数 `node --check` 和 `git diff --check` 已通过。GitCode 查询现在显式选择 `type=latest` 并验证 `release_status=latest`。本地浏览器、CloudBase 迁移/权限/事务及发布直链尚未验收，因此第 6 项保持未完成。网站与 CloudBase 文档及实施记录已补充；路线图文件已有用户工作区改动，本次未修改或暂存它，因此第 7 项仍待完成。
