# 停用旧版课堂临时策略入口

日期：2026-10-22  
TeacherConsole / StudentSetup：1.1.23  
Student Companion：0.4.68  
状态：本地 Release 包已构建；没有运行测试套件、安装或现场验收。

## 为什么清理

旧版“课堂练习”不是答题或测验功能，而是在课堂期间自动套用网站与应用限制、下课后尝试恢复的策略流程。这个流程不符合本项目的课堂教学方式，名称也容易造成误解。

## 改动

- 移除 TeacherConsole 的旧模式切换和应用影响确认窗口。
- 手机旧 `/api/classroom/mode` 启用请求返回 HTTP 410；旧版 Normal 恢复请求仍可安全恢复该课堂曾拥有的策略。
- 新课堂状态只同步互动在线状态；Student Companion 不显示旧模式标签。
- 保留旧状态文件、旧签名报文解析和按策略修订号恢复的账本。离线、身份未核验或策略版本发生变化时不强行修改；待恢复项仍可由教师重试。
- 网站、应用策略预设及独立策略管理功能不删除；开始课堂不会自动应用它们。

## 构建包

| 安装器 | 大小 | SHA-256 |
| --- | ---: | --- |
| [TeacherConsole 1.1.23 课堂清理试点包](../../../artifacts/test-build-phase7m-remove-classroom-practice-20261022/VeyonCampus-TeacherConsole-1.1.23-Classroom-Mode-Removal-Test.exe) | 69,335,066 bytes | `74AE9C0F8F62EAED05ABA2D9F2FB20A1FCFBEFFC28E552DA6CE755D9B7466ADF` |
| [StudentSetup 1.1.23 配套包](../../../artifacts/test-build-phase7m-remove-classroom-practice-20261022/VeyonCampus-StudentSetup-1.1.23-Classroom-Mode-Removal-Test.exe) | 67,189,016 bytes | `3F217E4320EEB2E46348A188F734B9A1F04D920D0D3D6203C59C4C9161370E0F` |

Release 全解方案编译成功，0 个错误、7 条警告（Companion XAML runtime-loader 1 条、NuGet 漏洞源不可达 1 条、检查项目平台分析 5 条）。TeacherConsole 打包另有两条既有 `TextBox.Watermark` 警告。两包均通过角色、Veyon 嵌入资源和 Release 公钥缺失时安全停用自更新的检查。教师端包启用了课堂快捷管控试点；仅用于可恢复测试设备。未配置 Developer Release PEM 公钥，因此包内自更新关闭。

## 后续人工验收

1. 开始课堂互动时不再出现旧课堂策略开关，也不自动应用网站/应用预设。
2. 手机课堂页保留互动、求助和课堂快捷管控；独立策略与预设页仍可用。
3. Student Companion 只显示课堂地点与电脑数量，不出现“练习”状态。
4. 对有旧版临时策略的可恢复测试机结束课堂，确认只恢复仍由本工具拥有且修订号未变化的策略；身份未知、设备离线或版本冲突时待恢复项仍保留。
5. 旧版课堂启用 API 返回 410；这只是兼容拒绝行为，无需现场手动调用接口。
