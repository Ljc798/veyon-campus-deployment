# 学生姓名映射与 InputGuard 状态摘要

日期：2026-10-22  
版本：TeacherConsole / StudentSetup 1.1.21  
状态：本地测试安装包已构建；安装、桌面/手机显示及 Windows 行为待统一现场验收。

## 显示规则

- 教师端课堂快捷管控的逐台进度按当前课堂名单、座位映射和策略目标选择解析学生姓名。
- 设备健康读取流程将签名地址解析出的学生姓名写入并刷新健康卡片；后续姓名解析结果也会更新已显示的卡片标题。
- 策略名单、课堂事件、更新结果、课堂恢复记录以及手机设备、聊天、屏幕巡视和待恢复列表都按学生姓名显示。
- 找不到可用映射时统一显示“未填写姓名”，不回退显示 `PC-01`、其他电脑编号、Veyon 名称或网络地址。
- 设备别名只有唯一对应到一个学生时才显示学生姓名；同一电脑名或地址若对应多个不同学生，姓名映射将视为冲突并显示“未填写姓名”，避免把另一名学生的名字显示在设备上。
- Veyon 主机名和网络地址仍用于设备连接、身份核对及内部数据关联；本次修改只改变面向教师的学生设备显示名，不重命名学生电脑。

## InputGuard 健康状态

- 新版 Agent 将签名的 InputGuard 阶段和租约信息加入 v2 轻量运行摘要。TeacherConsole 设备健康卡片及手机状态页可以查看锁定、未锁定、切换中、租约到期、Guard 已退出和未知状态。
- 锁定、正常、切换、退出及未知状态使用不同文字/颜色；旧版 Agent 继续用 v1 摘要，教师端明确提示该版本没有提供 Guard 状态，不把缺失状态推断成已解锁。
- 本次只连接状态读回和展示；实际输入恢复、系统桌面行为、断网租约和安装升级生命周期仍由现场验收确认。

## 构建产物

| 安装器 | 用途 | 大小 | SHA-256 |
| --- | --- | ---: | --- |
| [TeacherConsole 1.1.21 试点包](../../../artifacts/test-build-phase7k-student-name-mapping-20261022/VeyonCampus-TeacherConsole-1.1.21-Student-Name-Mapping-Test.exe) | 姓名映射、健康状态、InputGuard 状态、课堂快捷管控试点 | 69,341,371 bytes | `F35C63463BC3C4D1128DF23A97730155BDA626CB2EE97D17CF808A685E0E9B1D` |
| [StudentSetup 1.1.21 配套包](../../../artifacts/test-build-phase7k-student-name-mapping-20261022/VeyonCampus-StudentSetup-1.1.21-Student-Name-Mapping-Test.exe) | Agent 0.4.54、Companion、InputGuard 1.1.21 | 67,193,448 bytes | `08599FAFBAA3DA4A295293B7357CDF4E05D00F476F8D181F46CC03570DD670F6` |

两款安装器的角色和版本标记均已核对。TeacherConsole 使用 `ClassroomQuickControlManualTest` 试点构建标记，StudentSetup 包含同版本 InputGuard。安装包已完成内嵌 Veyon 文件、发布密钥缺失检查及更新助手安全停用检查。

完整 Release 构建成功，0 个错误、7 条已有警告：Companion XAML runtime-loader 1 条、离线 NuGet 漏洞数据源 1 条、检查项目平台分析 5 条。StudentSetup 打包保留 1 条 Companion XAML 警告；TeacherConsole 保留 2 条 `TextBox.Watermark` 过时 API 警告。缺少 Developer Release 公钥，应用内自更新安全停用。没有运行测试套件、启动或安装应用，没有做教师机、学生机或手机实测，也没有发布到 GitHub、GitCode、CloudBase 或下载站。

## 待统一现场测试

完整验收步骤见[下一阶段统一现场验收清单](../../下一阶段统一现场验收清单.md)。姓名显示重点覆盖：已填写姓名、未填写姓名、改过 Veyon 电脑名、HostOverride 地址、不同地点重复编号、桌位调换及课堂开始后更改地点；确认每台设备始终显示正确学生姓名或“未填写姓名”。InputGuard 重点覆盖新旧 Agent、锁定/解除/到期/退出状态的显示及实际键鼠恢复。所有步骤先在一台可恢复设备上执行。
