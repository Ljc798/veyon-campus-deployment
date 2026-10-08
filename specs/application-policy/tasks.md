# 学生机应用限制任务

- [x] 1. 定义独立应用策略协议、RSA-PSS purpose/key、版本/时间/校区绑定、账户 SID 与规则编译边界。
  - 证据：`VeyonCampus.Checks` 应用策略签名、篡改、重放、账户 SID、规则和序列化检查；项目任务 P13-03。
- [x] 2. 实现 AppLocker EXE/Appx composer、长期软件 allowlist 与课堂 Deny 合并、影响审核和事务状态。
  - 证据：长期 allowlist 启用时保持 EXE 集合 Enabled；审计使用登记程序清单模拟；AppLocker/MSI/Script 边界见设计文档。
- [x] 3. 实现 Student SYSTEM Agent 验签、身份签名回执、审核事件、到期/撤销、冲突保护和失败恢复。
  - 证据：53 项本地可移植检查覆盖签名回执、事务和恢复；Windows SYSTEM 任务/ACL 与实际 AppLocker 仍待验收。
- [x] 4. 实现 Teacher 规则编辑、应用清单扫描、审核确认、推送/撤销、目标状态和手机预设入口。
  - 证据：P13-06 描述扫描上限、规则生成、签名回执和身份固定；Teacher UI/扫描读回仍待 Windows 验收。
- [x] 5. 将应用策略信任和能力门槛接入 schema v4/v5、Node/ASP.NET API、OpenAPI、发布清单及双端更新。
  - 证据：API 合同检查 21/21、临时 PostgreSQL 夹具和角色构建记录；共享体验环境 v5/v6 完整写入/读取 E2E 仍按 P13-05 跟踪，v6 迁移与 API 已部署。
- [x] 6. 完成可移植检查、角色构建和架构/操作文档同步。
  - 证据：53/53 .NET 检查、21/21 Node/API 检查、TeacherConsole/StudentSetup Release 构建和 2026-10-08 实施记录。
  - 2026-10-08 后续：schema v6 增加首次部署建议字段，不改变应用策略签名边界；.NET 53/53、Node/API 21/21、双角色 Release 构建零警告/零错误及本地 v6 PostgreSQL 迁移检查通过。共享 CloudBase 已部署 v6；截至 2026-10-08 10:02 HKT，v5 runner 正在本机 Terminal 等待管理员用户名输入，尚未取得令牌或发起线上写入；v5 成功并清理后才运行 v6。
- [ ] 7. 在可还原且受支持的 Windows 10/11 设备完成目标账户、嵌套组、AppLocker AuditOnly/Enforce、长期 allowlist、程序兼容、更新、断网、重启、冲突、撤销和卸载恢复验收。
  - 证据要求：记录 EditionID、版本/UBR/补丁、域/MDM 状态、Agent/应用包版本、逐项操作及系统策略/事件读回；未取得现场证据前保持未完成。
  - _Requirements: 1–13；项目任务：P13-01–08、P12-01–08。_
