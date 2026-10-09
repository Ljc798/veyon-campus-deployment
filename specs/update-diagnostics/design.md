# 更新诊断：设计

## 组件边界

- `VeyonCampus.Core/UpdateDiagnostics.cs` 定义固定模块、操作、结果、严重性、错误码、脱敏事件和有界本机 JSON 存储。
- TeacherViewModel 与 StudentSetupUpdateViewModel 在更新操作完成时以 best-effort 方式记录事件；诊断写入异常不会改变更新结果。
- TeacherWindow 与 StudentSetupUpdateWindow 提供一个次要的“导出更新诊断”入口，由系统文件选择器决定导出路径。
- 导出只包含结构化白名单字段和批量计数，不包含自由文本。

## 稳定错误码

| 错误码 | 分类 | 严重性 | 可重试 | 建议 |
| --- | --- | --- | --- | --- |
| `UPDATE_NETWORK` | 网络不可达 | warning | 是 | 检查网络后重试 |
| `UPDATE_TIMEOUT` | 请求超时 | warning | 是 | 稍后重试 |
| `UPDATE_SIGNATURE_INVALID` | 签名或密码学验证失败 | error | 否 | 从可信发布页重新获取完整文件 |
| `UPDATE_ARTIFACT_INVALID` | 签名清单、摘要或载荷校验失败 | error | 否 | 重新获取可信发布包 |
| `UPDATE_PERMISSION_DENIED` | 本机访问被拒绝 | error | 否 | 检查管理员权限和目标目录权限 |
| `UPDATE_LOCAL_IO` | 本机文件读写失败 | warning | 是 | 检查磁盘空间及文件占用后重试 |
| `UPDATE_UNSUPPORTED` | 当前平台不支持 | error | 否 | 使用受支持的 Windows x64 版本 |
| `UPDATE_CONFIGURATION` | 发布信任或更新配置缺失 | error | 否 | 安装已配置签名公钥的正式构建 |
| `UPDATE_INPUT_INVALID` | 输入或格式无效 | error | 否 | 重新选择完整更新包 |
| `UPDATE_CANCELLED` | 用户取消 | information | 否 | 无需处理 |
| `UPDATE_UNKNOWN` | 未分类故障 | error | 否 | 保存错误码并联系维护人员 |
| UPDATE_HANDOFF_STARTED | 更新助手已启动，安装结果未知 | information | 否 | 重启后检查当前版本 |

成功、部分完成和仅成功启动更新助手分别使用 UPDATE_SUCCEEDED、UPDATE_PARTIAL 与 UPDATE_HANDOFF_STARTED；助手启动不表示安装已完成。分类器只检查异常类型，不读取异常消息、路径或堆栈。

## 存储与导出

默认目录为 `%LOCALAPPDATA%/VeyonCampus/Diagnostics/updates`。每条事件用独立临时文件写入并原子改名；目录或记录遇到重解析点时拒绝访问。最多保留 128 条，总存储上限 512 KiB，单条上限 4 KiB。导出再次校验字段并生成单个 JSON 文件，用户必须通过文件选择器手动选择位置。

事件的字段采用枚举和受限版本字符串，不设 `message`、`exception`、`stackTrace`、`path` 或设备标识字段。批量更新只允许记录不超过 150 台的汇总计数。

## 简约交互

保留现有更新布局、主题、按钮和默认行为。只在更新区底部增加一个次要导出按钮及一行“仅本机保存，不会自动上传”的说明。StudentSetup 窗口在现有底栏追加同一入口。无设置页、过滤器、导出范围选择或新确认步骤。

## 验证

- 核对分类器错误码、严重性、重试属性和不回显异常文本。
- 核对 JSON 字段、版本筛选、隐私字段缺席与批量计数。
- 写入超过保留上限后确认只保留最新 128 条，且总容量有界。
- 验证更新视图模型与两个角色 Release 构建。
- 自动化检查不等于 Windows 实机的网络、回滚或安装验收。
