# 管理员下载页版本同步：设计

## 组件

- 新增独立的 `/admin/downloads` 后台页，与 `/admin/releases` 的“触发构建发布”入口分开。
- 新增 CloudBase 私有 `site_download_settings` 单行设置表，按 `github`、`gitcode` 保存经验证的版本和 URL；只由 API 服务角色读写，不授予匿名或普通用户表权限。
- Cloud Function 提供公共只读下载页配置接口，以及 owner/admin 专用的读取和同步接口。
- GitHub Release API 与 GitCode Release API 仅由服务端访问。仓库所有者、仓库名和允许的资产名固定在代码中；对每个 Release 校验 tag、版本和两种角色的附件。
- 同步后先完整构造两个来源的候选记录，再通过单个数据库 RPC 事务写入；不能出现 GitHub 已更新而 GitCode 保留旧状态的半成功。
- 静态网站在 `/download` 请求公共配置接口，按来源渲染版本与链接。数据库尚无设置记录（首次同步前）或 API 暂时不可用时，继续使用网站内按来源维护的最近确认版本和链接，并显示相应提示；备用版本不从桌面 App 源码版本推导，因为 GitCode 镜像可能落后于 GitHub。管理员同步成功后，两个来源各自显示其最新正式版本。

## 接口边界

- `GET /v1/download-page-config`：匿名可读；只返回来源名称、版本、Release URL、Teacher URL、Student URL、更新时间。
- `GET /v1/admin/download-page`：owner/admin 可读当前配置与上次同步状态。
- `POST /v1/admin/download-page/sync`：owner/admin 可调用；服务端读取两个 Release，验证后事务性保存。
- 所有配置响应使用 `Cache-Control: no-store`，确保管理员保存后访客刷新就能读取新版本。
- 来源 URL 由服务端基于固定 owner/repository、tag 和经过校验的附件名构造；不接受调用者传入 URL，避免开放重定向和 SSRF。

## 管理界面

- 在现有后台框架内增加“下载页管理”导航，沿用现有墨蓝、薄荷绿、字体、卡片和按钮样式。
- 每个来源卡片列出版本、教师端/学生端附件状态和最近同步时间；页面顶部提供一个“同步最新版本”按钮。
- 同步期间禁用重复提交。成功时显示两个来源的版本和同步时间；失败时按来源显示原因并保留原配置。
- 入口仅对 owner/admin 显示；移动宽度下卡片纵向排列，版本、状态、按钮顺序保持清晰。

## 数据约束

- 数据库按 `source` 为 GitHub 与 GitCode 各保存一条记录；每条记录包含固定 HTTPS release URL 和角色附件 URL。
- `updated_by` 只保存 CloudBase 用户 ID；不保存访问令牌或上游原始响应正文。
- 服务端只允许当前仓库固定格式的 `vX.Y.Z` 版本与 `VeyonCampus-{Teacher|Student}-Setup-X.Y.Z-win-x64.exe` 文件。
- 数据库更新使用单个事务 RPC；数据库不可用时同步响应失败，不在内存中伪装为已保存。

## 失败处理

- 上游超时、限流、格式变化、非正式 tag、仓库错误或缺失任一 EXE 时，返回通用可操作错误及失败来源，不返回上游响应正文或秘密。
- 事务失败时保留旧配置；管理员可稍后重试。
- 公共读取失败时页面显示明确提示，并保留网站构建时生成的固定 HTTPS 下载链接。
