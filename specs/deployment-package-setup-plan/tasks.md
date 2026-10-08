# 校区配置包部署建议任务

- [x] 1. 定义 schema v6 `recommendedOperations` 严格契约，保留 v1–v5 解析和生成行为。
  - _Requirements: 1, 6_
- [x] 2. 接通 `PackageBuilder`、`PackageManifest`、`PackageContext` 与包指纹，验证生成输入和读回建议一致。
  - _Requirements: 1, 2, 6_
- [x] 3. 在 TeacherConsole 增加建议操作编辑项，并将默认值限定为仅建议 Veyon 安装/配置。
  - _Requirements: 2_
- [x] 4. 在 StudentSetup 载入 v6 建议；允许维护人员取消/编辑，管理员密码建议仅提示且永不自动勾选。
  - _Requirements: 3, 4, 5_
- [x] 5. 同步 Node/API、OpenAPI、CloudBase 对象版本路径与数据库迁移，拒绝非法 v6 包。
  - 代码与迁移源码支持 v6；本地迁移验证通过，2026-10-08 已在预备份的共享环境应用该迁移并部署 `veyon-api`/OPA。
  - _Requirements: 1, 6_
- [x] 6. 运行 .NET 与 Node/API 检查，构建 TeacherConsole 和 StudentSetup Release，并更新架构、任务主表及操作说明。
  - .NET 检查 53/53、Node/API 合同 21/21；双角色 Release 构建零警告、零错误；本地 PostgreSQL v6 迁移检查通过。
  - _Requirements: 7_
- [ ] 7. 应用 v6 迁移并部署 API 后，具备管理员交互认证时运行共享 CloudBase v5/v6 合成写入/下载/撤回清理 E2E；Windows 实机验收继续由 P7/P8/P11 单独跟踪。
  - runner 支持显式 `--schema-version 5` 和 `--schema-version 6`；v6 迁移和 API 已部署，v5/v6 live E2E 待本机管理员交互登录。
  - _Requirements: 7_
