# 校区配置包部署建议任务

- [ ] 1. 定义 schema v6 `recommendedOperations` 严格契约，保留 v1–v5 解析和生成行为。
  - _Requirements: 1, 6_
- [ ] 2. 接通 `PackageBuilder`、`PackageManifest`、`PackageContext` 与包指纹，验证生成输入和读回建议一致。
  - _Requirements: 1, 2, 6_
- [ ] 3. 在 TeacherConsole 增加建议操作编辑项，并将默认值限定为仅建议 Veyon 安装/配置。
  - _Requirements: 2_
- [ ] 4. 在 StudentSetup 载入 v6 建议；允许维护人员取消/编辑，管理员密码建议仅提示且永不自动勾选。
  - _Requirements: 3, 4, 5_
- [ ] 5. 同步 Node/API、OpenAPI、CloudBase 对象版本路径与数据库迁移，拒绝非法 v6 包。
  - _Requirements: 1, 6_
- [ ] 6. 运行 .NET 与 Node/API 检查，构建 TeacherConsole 和 StudentSetup Release，并更新架构、任务主表及操作说明。
  - _Requirements: 7_
- [ ] 7. 具备管理员交互认证后运行共享 CloudBase v6 合成写入/下载/撤回清理 E2E；Windows 实机验收继续由 P7/P8/P11 单独跟踪。
  - _Requirements: 7_
