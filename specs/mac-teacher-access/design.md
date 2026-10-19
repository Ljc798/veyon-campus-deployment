# MacBook 连接教师机：设计

> 状态：2026-10-19 已撤回。当前版本不包含此功能；以下为旧设计留档。

## 方案

- 使用 Windows 自带的远程桌面主机和 Mac 上的 Microsoft Windows App。RDP 会话直接操作教师机上的完整 TeacherConsole，现有配置、密钥、目录写入和权限边界仍由 Windows 主机处理。
- TeacherConsole 增加独立帮助窗口：显示 `Environment.MachineName`、提供复制按钮、打开 Windows 远程桌面设置，并列出连接前提和本地显示器影响。
- 帮助窗口只读读取 `ProductName`/`EditionID` 与 `fDenyTSConnections`，标明“版本未知”和“系统开关已启用”而不把它们当成连接成功；不扫描网络或测试外部客户端可达性。
- 连接时在 Windows App 中手动添加电脑名称并由 Windows App 请求凭据。不生成 `.rdp` 文件，避免在连接配置中无意携带剪贴板、磁盘等设备重定向选项。
- 远程桌面设置入口只打开 `ms-settings:remotedesktop`；用户仍在 Windows 设置里确认版本、启用状态和允许连接的账户。
- 网络指引仅建议校园可信 LAN 或学校 VPN。不得建议直接将 3389 暴露到公网。

## 边界和限制

- Windows Pro、Enterprise、Education 及受支持的 Server 版本可作为 RDP 主机；Windows Home 不能作为主机。界面读取的版本字符串仅用于提示，未知 SKU 保持未知。
- RDP 连接会把交互会话切换到远程端，本机显示器不再显示正在操作的桌面。因此本功能提示教师避免在课堂投屏或其他人员正在本地操作时连接。
- 如果需求是在课堂投屏期间保持本地画面不变，则完整 RDP 控制不符合该约束；需要另行设计并审查可配对的浏览器管理 API，不能用本地服务端口替代身份认证。
- Mac 上当前手机网页控制仅覆盖已有的课堂操作，不等同于完整桌面配置。

## 验证

- 教师端 Release 构建验证新窗口和 Avalonia XAML。
- Windows 主机上核对显示的电脑名称与系统设置一致，并确认按钮只打开系统设置。
- MacBook 与校园 LAN 上的 Windows App 进行端到端连接，核实完整控制台、认证提示、断开/重连和本地显示器行为。
