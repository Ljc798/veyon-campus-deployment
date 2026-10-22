# 健康列表与聊天 Enter 发送修复

日期：2026-10-22  
版本：TeacherConsole / StudentSetup 1.1.30  
状态：两个 Windows x64 离线测试安装器已构建；用户确认 Enter 可发送。健康页首次显示仍有短暂空列表，已由 [1.1.31 修复记录](health-list-first-read-and-count-fix-20261022.md)继续处理。

## 修复内容

- 教师端重建 Veyon 电脑名单时，替换名单项前解除旧 `TeacherPolicyTargetChoice` 的 `PropertyChanged` 订阅；选择变更回调还会忽略已脱离当前名单的旧对象，避免旧名单的迟到事件清空新健康列表。
- 教师聊天框和学生 Companion 聊天框将 Enter 处理注册为输入路由的 Tunnel 阶段，让 Enter 发送逻辑先于多行文本框默认换行处理；Shift+Enter 仍交给文本框换行。
- 版本号升为 1.1.30。学生 InputGuard 版本同步为 1.1.30；Agent 仍为 0.4.55，Companion 仍为 0.4.68。
- 用户反馈 1.1.29 仍出现设备行短暂显示后消失；因此 1.1.29 健康页修复记录只表示构建时实现，不作为现场问题已解决的证据。本版要重点验证自动刷新稳定性。

## 本地测试安装器

| 安装器 | 用途 | 大小 | SHA-256 |
| --- | --- | ---: | --- |
| [TeacherConsole 1.1.30](../../../artifacts/VeyonCampus-Teacher-Setup-1.1.30-win-x64.exe) | 教师端；包含课堂快捷管控试点标记 | 69,335,943 bytes | `76086C067CBEFE4095742DFC24DBB66636FF82F8A4A42412E2E1B1A19F3F9EB3` |
| [StudentSetup 1.1.30](../../../artifacts/VeyonCampus-Student-Setup-1.1.30-win-x64.exe) | 学生端；Agent 0.4.55、Companion 0.4.68、InputGuard 1.1.30 | 66,951,863 bytes | `B0F5C49A397C265100FD9DA14BE6222A46394D56FBCA546BD5C516D327C6E3EB` |

教师端 Release 编译 0 错误，保留 2 条既有 `TextBox.Watermark` 过时警告；学生端 Release 编译 0 错误，保留 1 条既有 Companion XAML runtime-loader 警告。两个 Inno Setup 安装器均成功生成；内嵌 Veyon 文件、发行密钥缺失检查和更新助手安全停用检查通过。未启动应用、未安装、未运行测试套件、未连接真实设备、未上传远端。Developer Release PEM 公钥未配置，应用内更新安全停用。

## 用户验收

完整步骤见[下一阶段统一现场验收清单](../../下一阶段统一现场验收清单.md)。本轮重点：

1. 用四台已登记学生电脑打开教师端设备健康页，至少观察 30 秒，跨过多轮 5 秒轮询；列表、计数和状态应稳定，不应短暂出现后消失或全部归零。
2. 重新读取同一名单，并让一台 Agent 暂时断网后恢复；设备行应一直保留，未知状态明确显示，恢复后只更新对应设备。
3. 活动课堂中从教师端发一条消息、从学生 Companion 回复一条消息，分别用 Enter 发送；每条消息应只出现一次。Shift+Enter 换行，发送按钮继续可用。
4. 用中文输入法输入候选词，先按 Enter 确认候选字，再按 Enter 发送；候选确认不应提前发送消息。另检查空消息及连续快速发送。
5. 切换电脑选择或教室，确认健康页不混入旧目标的回执；稳定选择后各行持续显示，并按学生姓名标注。
