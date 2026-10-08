# 学生机网站限制任务

- [x] 1. 定义域名规范化、黑名单/白名单/停用策略模型及 Edge/Chrome/Firefox 规则编译器。
  - 证据：`VeyonCampus.Checks` 覆盖 IDNA、无效 URL/路径/IP、去重、数量边界和三种浏览器规则语义；Microsoft/Google/Mozilla 规则格式已在设计文档引用。
- [x] 2. 实现 RSA-PSS 签名、校区绑定、递增 revision、防重放和最长 24 小时自动到期。
  - 证据：策略 schema v1/v2；Agent 到期时只移除仍由工具拥有且未被外部修改的值。
- [x] 3. 实现 Student SYSTEM Agent、受保护启动任务、浏览器注册表事务、冲突检测、读回、启动恢复和卸载路径。
  - 证据：P7-13 与 P12-04–08 记录代码/fixture；2026-10-08 修正 Chrome URL-list 规则格式，并加入旧 `[*.]domain` Chrome 策略的受事务保护启动迁移；实际 SYSTEM 任务 ACL、注册表和浏览器效果仍待 Windows 验收。
- [x] 4. 实现 Teacher 域名编辑、目标选择、时长/停用、直接 LAN 推送、逐台签名结果和有界历史。
  - 证据：P12-02/03 记录桌面入口、45/60/90/120 分钟选项、超时待核对、不自动重试及最多 50 次历史。
- [x] 5. 将网站预设接入手机配对服务，并让手机复用桌面签名、目标身份固定和结果流程。
  - 证据：[手机控制任务](../mobile-teacher-control/tasks.md)与[手机长期策略任务](../mobile-system-policy-control/tasks.md)的代码/API检查通过；真实手机/校园网仍待验收。
- [x] 6. 完成可移植检查、操作说明和实现记录同步。
  - 证据：2026-10-08 .NET 检查 53/53、手机 PWA JavaScript 语法检查及当前操作/验收文档。
- [ ] 7. 在目标 Windows 10/11 和 Edge/Chrome/Firefox 版本完成浏览器策略读回、实际黑/白名单匹配、手动刷新/重启、断网、睡眠/重启、到期、外部冲突、普通用户防篡改和卸载恢复验收；另在 Android/iOS 与真实校园网完成手机配对和预设操作验收。
  - 证据要求：记录 Windows 版次/UBR/补丁、浏览器版本、Agent/Teacher 版本、策略页/注册表读回、逐项步骤和结果；不得只用 Agent 回执证明网站效果。
  - _Requirements: 1–13；项目任务：P12-01–08、P7-13、手机实机验收。_
