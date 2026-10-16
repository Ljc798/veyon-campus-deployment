# 管理员下载页版本同步实现与验收边界

日期：2026-10-17

工作区：`develop`

功能实现：`1e83940 feat(website): sync download page releases from admin`
稳定 Release 校验修正：`293238e fix(downloads): reject GitCode prereleases during sync`

## 实现内容

- 网站新增 `/admin/downloads`，owner/admin 可读取当前 GitHub 与 GitCode 配置并请求同步。
- 云函数从固定仓库读取 Release，验证 `vX.Y.Z`、稳定状态和 Teacher/StudentSetup 两个 Windows x64 EXE 附件；链接由服务端按固定仓库和校验后的版本构造。
- GitCode API 请求显式使用 `type=latest`，并要求 `release_status=latest`；GitHub 发行必须明确 `draft=false` 且 `prerelease=false`。
- 只有两个来源都校验通过才调用单个事务 RPC。任一来源失败时不保存候选配置；公开下载页由匿名只读 API 读取两个来源各自的版本和链接。
- 新数据库迁移为 `cloudbase/migrations/20261016130000_create_site_download_settings.sql`；设置表仅授予服务端角色，公开页面不会直接访问数据库。

## 已完成的本地检查

- `npm run build --prefix website`：通过。
- `node --check cloudfunctions/veyon-api/index.js`：通过。
- `git diff --check`：通过。

本次未运行测试套件，也未做真实浏览器或 CloudBase 环境操作。当前会话没有可用浏览器标签，因此无法手工验证后台页面和公开下载页。

## 仍待验收

- 在 CloudBase 应用迁移，复核表、RPC、grants、RLS 与 OPA 路由；部署云函数和静态网站。
- 用真实 owner/admin、editor/viewer 和匿名会话检查权限与响应缓存头。
- 覆盖 GitHub/GitCode 上游失败、预发布版本、缺少附件和事务失败，确认原配置保持不变。
- 在浏览器打开公开下载页、两种 Release 页面与四个角色安装直链，核对实际版本。
- 项目路线清单已有用户工作区修改；本次没有编辑、暂存或提交该文件。项目级状态需在保留该修改的前提下另行更新。
