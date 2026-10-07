# 学生机应用限制设计

## 组件边界

`VeyonCampus.Core` 定义严格策略文档、RSA-PSS 签名、账户/规则编译、策略组合、审计查询、目标清单协议和事务状态。TeacherConsole 持有独立应用策略签名密钥，提供规则编辑、目标选择、影响审核、执行确认、签名推送和逐台结果。Student Agent 以 SYSTEM 运行，验证校区公钥、签名、版本、SID 与目标状态，再调用 Windows AppLocker 后端。CloudBase 只托管公开校区配置包和应用发行清单，不中继课堂策略。

Teacher 与 Agent 之间使用校园 LAN 上的签名请求/回执。设备回执另由 Student Agent 的持久机器身份密钥签名；教师端按校区和目标固定该身份公钥。手机 PWA 只向 TeacherConsole 发请求，复用桌面端已保存预设和相同策略流程。

## 策略编译与账户范围

- 课堂 Deny 以目标学生 SID 为主体；账户 SID 必须是本机普通账户范围，RID ≥ 1000。RID 500 管理员不能作为学生目标。
- 预检从本地组 SID 成员图递归展开嵌套关系。学生若属于 Administrators，拒绝应用策略；组图解析不完整时失败关闭。启用系统策略时还会拒绝 Account Operators 与 Network Configuration Operators 成员。
- 发布者规则可约束发布者、产品、应用名称和版本区间；无可信签名者时以 SHA-256 精确识别。安装路径只能用于管理员确认和受信任目录分析，不能单独信任学生可写路径。
- Deny 规则通过受保护文件名表，排除 Windows 登录、外壳、设置、安全、维护、恢复及本工具组件。当前分发组件和候选更新文件的哈希进入动态保护集合。

## AppLocker 单一所有者

课堂限制和“禁止安装软件”都由 `WindowsApplicationPolicyBackend` 通过同一策略 composer 生成 EXE/Appx XML，再由单一事务写入和读回，避免策略互相覆盖。

- 仅课堂 Deny 时使用 Everyone Allow 基线，并把具体 Deny 限定到选中 SID。
- 长期软件限制启用时，EXE 集合采用 Enabled 放行基线：允许受检查的 Windows/Program Files 目录，拒绝学生可写目录和可移动介质中的 PE 程序，并保留 SYSTEM、管理员和非学生本机账户的启动能力；课堂执行 Deny 合并进同一集合。
- Appx 集合维持已签名包放行。AppLocker MSI 与 Script 集合不配置；MSI、Store/Appx 安装入口由系统策略控制。
- 没有长期 allowlist 时审核读取真实 AuditOnly 事件。allowlist 启用后审核改用已登记程序清单进行影响模拟，UI 和结果必须标明“模拟”，不能声称观测到真实启动。

长期 allowlist 的放行路径须递归检查目录和文件 ACL、学生及本地组写权限、所有者、重解析点和读取完整性；检查失败或超过时间上限即拒绝写入，不自动修复 ACL。禁止路径会导致登录/恢复风险，必须由 Windows 实机验收核心程序和回滚路径。

## 预检、事务和服务启动顺序

运行时先读回 Windows 版本/SKU、域/MDM 事实、已有有效 AppLocker 策略、目标本地账户、完整嵌套组成员图和放行路径 ACL。任一事实未知、外部策略冲突或环境不支持时，保持原系统状态。

编译目标 XML 和注册表值后，持久化包含原值、期望值、目标 SID、策略版本和恢复阶段的 Pending 事务；确认预检仍成立后，才在紧邻 AppLocker 更新前启动 AppIDSvc。每项写入后读回；失败时按所有权逐项恢复。Agent 启动和命令处理都会检查未完成事务。当前值既非事务原值又非工具目标值时视为外部冲突并停止覆盖。

课堂期限由 Agent 本地时间检查和启动恢复流程执行；教师离线不影响已应用策略。撤销、到期、换校区或卸载只恢复本工具仍拥有且未被外部修改的资源。策略应用只影响之后的新启动，不结束已运行程序。

## 信任、兼容和更新

- 网站、应用、系统和 Developer Release 使用分离的 key/purpose。应用策略绑定校区、递增 revision、目标 SID、时间窗、规则及内容摘要，并拒绝重放。
- 配置包 schema v4 引入应用策略公钥和 Agent 兼容范围；schema v5 补充系统策略公钥及能力摘要。旧客户端不支持应用策略时必须返回不支持，不能把未知策略当成功。
- Teacher 状态回执必须经机器身份签名验证，绑定 nonce、命令/策略摘要和目标指纹。首次身份或换钥需显式审批；未固定时 UI 显示待复核。
- Teacher、Student Agent、Worker、UpdateHelper 和 Windows 修复组件必须继续可启动。更新器在文件替换前检查 Developer 签名、角色、版本、兼容范围、bundle 摘要及活动策略能力；失败回滚恢复旧文件、任务和信任/策略状态。
- CloudBase 管理配置包与发行清单，不收集逐台应用规则或审核事件；公开发行物不携带教师策略私钥或真实校区包。

## 状态与隐私

Teacher 保留有界的逐台结果、审核摘要与人工身份信任决策；Student Agent 在 SYSTEM 保护目录持有策略和事务状态。数据需绑定校区、设备和规则版本以便诊断，但不写入应用使用内容、学生文档或网页内容。Agent 回执只说明协议处理结果；实际 AppLocker 命中、事件准确性、学生用户边界和恢复能力仍以目标 Windows 的现场证据验收。

## 验证矩阵

| 层次 | 证据 | 能证明什么 |
| --- | --- | --- |
| 可移植 .NET 检查 | 当前 `VeyonCampus.Checks` 53/53 | 签名、规则/账户编译、组图预检、事务和失败边界 |
| Node/API 检查 | 当前 API 合同 19/19 | 配置包/发行接口、能力字段、迁移门槛和契约一致性 |
| 角色构建 | TeacherConsole、StudentSetup、Agent、UpdateHelper Release | 编译和角色打包边界 |
| Windows 实机 | P13-01–08、P12 相关任务 | AppLocker 服务/策略读回、真实用户令牌、程序拦截、Windows 核心兼容、更新/撤销/恢复 |

截至 2026-10-08 前三层有记录证据，Windows 实机层仍开放。详细设计和逐日状态见[实现设计文档](../../docs/应用程序限制设计.md)与[当日记录](../../docs/records/2026-10/续作核查记录-20261008.md)。
