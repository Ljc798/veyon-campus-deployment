# 下一阶段 Phase 5：软件管控体验重构

实施日期：2026-10-22
状态：教师界面、Agent 读取协议与管理员恢复说明已落地；Release 编译成功。Windows 实机效果、教育软件兼容、Agent/Veyon/更新器保护及完整恢复仍待用户统一验收。

## 1.1.16 Phase 5 本地测试包

- 教师端：[VeyonCampus-TeacherConsole-1.1.16-Phase5-Test.exe](../../artifacts/test-build-phase5-software-control-20261022/VeyonCampus-TeacherConsole-1.1.16-Phase5-Test.exe) — 69,330,559 bytes；SHA-256 `819B820C5BDFB2327E5A72EFE8CCA96711AD4BCA2BE05BDC8A93A69AC486A003`。
- 学生端：[VeyonCampus-StudentSetup-1.1.16-Phase5-Test.exe](../../artifacts/test-build-phase5-software-control-20261022/VeyonCampus-StudentSetup-1.1.16-Phase5-Test.exe) — 67,186,888 bytes；SHA-256 `D3EF9311DF350B0D2F069F53DFB08E7D09AEE6FACBCEF2B0D988038AB7D67E15`。
- 使用 `scripts/package-windows-offline.ps1` 分角色构建；未提供 Developer Release 公钥，因此安装包安全停用在线应用更新。此包供本地验收，不能当作公开正式发行物。
- 完整解决方案 Release 构建成功，0 errors；有 6 个现存/环境警告（NuGet 漏洞源不可达、跨平台 CA1416，以及桌面/Companion XAML 警告）。未运行检查程序或自动测试，也未安装包或执行 Windows AppLocker 实测。

## 已实现

- 教师“应用限制”页把长期软件运行保护作为独立入口；无需先读软件目录。按钮直接跳转到“系统限制”，在已选学生账户上开启或关闭长期基线。提交仍要求查看影响并确认；Student Agent 在本机安全检查、持久事务与 AppLocker 读回完成后返回签名回执，教师逐台展示成功、失败或需核对结果。
- 精确课堂封禁改为两阶段。`/v2/application-inventory/catalog` 只读取最多 400 个登记 EXE 的产品、文件属性和候选 ID，不计算签名或哈希；教师按产品勾选后，`/v2/application-inventory/details` 只为选中项（每台最多 80 项）请求 AppLocker 发布者信息与文件哈希，并重新确认条目仍属于当前登记清单。
- 原 `/v1/application-inventory` 与 `/v1/student-accounts` 读取路径和 DTO 保持不变。完整扫描入口移至“管理员工具”；明确提示其 400 个候选上限、最长 45 秒和“不是完整软件盘点”的边界。
- 清单读取使用独立单并发只读闸门，不占用策略写入闸门，也不执行昂贵 ACL 审查。教师或 Agent 每次启用、变更、审核或恢复策略时，原有 Windows 版本/版次、域/MDM、账户/嵌套组、受保护二进制哈希、外部策略和放行目录 ACL 等检查仍必须完整执行；没有缓存能跳过这些检查。
- 界面说明 AppLocker 运行基线仅控制桌面 EXE；签名 Appx 继续放行；AppLocker MSI/Script 集合不由该功能修改。安装入口另由系统策略处理。预检超时或失败会失败关闭并返回具体原因，不写策略。
- 双角色 Release 构建包含该协议与界面。未运行检查程序、未安装包、未操作 Windows AppLocker 或真实学生电脑。

## 管理员完整恢复流程

### 在线解除长期软件运行保护

1. 在“策略与预设 → 系统限制”读取并选中需要解除的学生账户。
2. 取消“长期软件运行保护（含部分安装限制）”，保留其他仍需使用的系统限制选项。
3. 查看影响范围并提交。逐台确认 Agent 回执显示长期软件运行基线已关闭且本机状态已读回；任何“需核对”或失败设备都不能当作已恢复。
4. 需要解除课堂临时封禁时，再到“应用限制 → 应用”执行“解除应用限制”。课堂规则撤销与长期软件保护是两个动作。
5. 在可恢复测试机用管理员身份读取本机和有效 AppLocker 策略，确认不再有本工具拥有的活动限制；不要把 Agent 回执当成实际程序启动效果的独立证明。

### Agent 不可达或检测到外部策略冲突

1. 不要删除 `%ProgramData%\VeyonCampus\ApplicationPolicy` 状态文件，也不要停止 `AppIDSvc`、清空本机 AppLocker XML 或反复重试覆盖。
2. 使用学生机本地管理员登录，记录 Windows 版本/版次、`AppIDSvc` 状态、本机与有效 AppLocker XML，并保留 `%ProgramData%\VeyonCampus\ApplicationPolicy\<校区 SHA-256>\state.json`。该状态目录只允许 SYSTEM 和本机管理员访问。
3. 对照状态中的 `InstalledXml`、`PendingPreviousXml` 和 `OriginalXml` 与当前本机/有效策略。只有当前策略仍与本工具记录的目标完全一致时，才能通过恢复流程写回原值；发现学校 GPO/MDM 或其他管理员改动时停止，交由该策略的所有者协调。
4. Agent 恢复通信后，优先使用教师端“系统限制”解除并等待签名回执；若事务仍待处理，保留状态和诊断信息，勿卸载 Agent。卸载器也会阻止清理活动策略所需组件。

## 之后统一进行的人工验收

仅在可恢复的 Windows 10/11 测试机和测试学生账户上操作，记录 EditionID、build/UBR、补丁、Agent/Setup 版本、AppIDSvc 原状态和逐台回执。

1. 打开教师端“应用限制”，确认长期软件保护入口可见；无需读目录即可转到系统限制。选择测试账户，启用保护并确认逐台签名结果明确报告基线读回。
2. 检查有效/本机 AppLocker XML 和 AppIDSvc。验证 Program Files 中的教学软件、Windows Shell、Veyon、Student Agent、Teacher/Worker 与更新/恢复工具可启动和升级。
3. 在非正式机上验证学生可写目录/临时目录 PE 启动受到预期限制；验证签名 Appx 可用，记录 MSI/Script 因其他系统设置产生的行为，不把它误归因于 AppLocker EXE 规则。
4. 快速读取软件目录；确认产品分组和电脑范围。只选择一个测试产品并生成候选，确认详情与哈希仅针对选中登记文件，变化或伪造 ID 会被拒绝。
5. 在“管理员工具”运行旧版完整扫描入口，确认旧读取 API 仍工作并显示上限/耗时说明；不得以其结果当作完整软件盘点。
6. 试验不受支持 SKU、域/MDM、非学生管理员账户、Program Files ACL 不安全/审查超时和外部 AppLocker 策略冲突。应失败关闭、返回设备级原因且不启动服务或覆盖外部策略。
7. 取消长期软件保护并确认本机/有效 XML 恢复；再分别测试课堂 Audit、Enforce、撤销、断网、重启、事务中断恢复和冲突保留。确认临时规则解除不会意外关闭仍需的长期基线。
8. 在开启基线期间更新 Student Agent/Setup/Veyon，验证更新清单能力门槛、受保护哈希和失败回滚；卸载时确认活动策略会阻止危险清理。

未完成上述 Windows 人工验收前，不把 AppLocker 实际命中、教育软件兼容或完整恢复标记为通过，也不发布正式版。

## 2026-10-22 后续：Agent 回执与现场效果分层

- TeacherConsole 设备健康、系统限制逐台结果及手机系统状态将签名 Agent 状态明确显示为“Agent 已报告/已读回”；Windows 实际限制效果同时标为待现场核验。
- 系统策略推送结果不再用“系统限制已生效”概括 Agent 回执；手机逐台操作原有的“电脑已回应，实际限制效果仍需在学生电脑上确认”说明继续保留。
- TeacherConsole 1.1.27 本地试点包已构建，含课堂快捷管控试点标记。此改动只澄清回执语义，不代表 AppLocker、Windows 或浏览器效果已通过实测。
- 现场核对入口见[统一现场验收清单](../../docs/下一阶段统一现场验收清单.md)第 5 节；当前仍未运行测试套件、安装应用或进行 Windows/手机实测。

## 2026-10-22 后续：课堂姓名按原教室解析

- TeacherConsole 1.1.28 在课堂消息和待恢复记录中按课堂会话保存的原教室、座位映射学生姓名；多个教室重复使用同一电脑编号时，不再跨教室误配姓名。
- 无法唯一关联的设备统一显示“未填写姓名”；PC 编号、Veyon 名称和连接地址仍只用于本地目标解析，不作为运行界面标签。
- 本地试点安装器和具体手动测试步骤见[统一现场验收清单](../../docs/下一阶段统一现场验收清单.md)第 1 节及[姓名映射实施记录](../../docs/records/2026-10/student-name-session-scope-mapping-20261022.md)。没有运行测试套件或进行现场测试。
