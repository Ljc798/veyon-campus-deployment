# 课堂屏幕预览 PoC 实现记录

日期：2026-10-10（Asia/Hong_Kong）

代码状态：本功能独立本地提交，尚未推送；实机验收未完成。

## 实现范围

已配对手机在活动课堂卡片中主动点“查看屏幕”后，可查看最多 5 台当前课堂电脑的低分辨率单帧概览。只请求手机当前可见的缩略图；停止巡视、页面转入后台或课堂 session 变化时，前端取消请求、释放对象 URL，服务端撤销巡视租约。没有录像、回放、桌面远控或云端画面存储。

## 主要实现

- `src/VeyonCampus.App/ClassroomScreenPreviewCapture.cs`：复用 `veyon-cli feature start <host> Screenshot` 和 Veyon 已配置的认证通道。只接受本次唯一生成、路径无重解析点且指纹未变的原始 PNG；最多读取 32 MiB、限制源像素后缩到 320×180 以内，并在成功或失败时删除已确认属于本次的截图。若文件身份发生变化则保护该文件并终止返回。
- `src/VeyonCampus.App/TeacherMobileControlServer.cs`：新增开始巡视、停止巡视和 PNG 路由。要求已有手机配对凭据、有效随机数、活动课堂 session、当前课堂目标和 60 秒活动租约；每目标至少间隔 2 秒，单教师服务最多同时运行 2 个 Veyon CLI 采集进程，预览请求每设备及全局均限为 200 次/分钟。响应禁止缓存；审计只记录设备、目标和操作，不记录画面。
- `src/VeyonCampus.App/MobileWeb/`：活动课堂卡片提供单一开关；没有活动课堂时隐藏入口。缩略图最多 5 张，只刷新当前可见项目；屏幕巡视停止或页面隐藏时取消请求并回收临时 URL。Service Worker 不缓存 `/api/`。
- `tests/VeyonCampus.Checks/MobileControlApiChecks.cs`：增加授权/目标边界、租约撤销、session 结束、请求限频、缓存、最大并发、取消和尺寸上限检查。
- [手机控制使用指南](../../手机控制使用指南.md) 与[专项规格](../../../specs/classroom-screen-preview/requirements.md)。

## 自动验证

- `dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1`：成功，0 警告、0 错误。
- `dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -p:VeyonCampusRole=TeacherConsole -m:1`：成功，0 警告、0 错误。
- 完整 .NET 检查集：**71/71 通过**。
- `node --check src/VeyonCampus.App/MobileWeb/app.js`、`node --check src/VeyonCampus.App/MobileWeb/service-worker.js`、`git diff --check`：通过；本地 HTTP 静态页面返回 200。
- 当前运行环境未提供可用的 CUA 浏览器窗口，因此没有做桌面浏览器视觉交互验收；真实手机布局和操作需实测。

## 实机验收边界

仍需在 Windows TeacherConsole、Student Veyon 4.11.2 和真实手机的校园 LAN 上验证实际 CLI 输出文件命名、截图目录权限、原图清理、Veyon 认证/在线行为、缩放后的视觉质量、首帧等待时间及 5 台情况下的 CPU、内存和带宽。当前本地或现有 Release 安装包不包含这项尚未推送的代码；需要后续构建含本次提交的 Windows 安装包后再做现场验收。
