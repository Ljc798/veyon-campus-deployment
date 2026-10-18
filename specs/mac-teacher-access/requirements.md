# MacBook 连接教师机：需求

## 目标

让教师从 MacBook 进入校园 Windows 教师机的完整 Veyon Campus 控制台进行配置，继续在 Windows 主机本地运行既有功能，不在 Mac 端复制密钥、安装逻辑或高权限操作。

## 验收条件

1. 教师端提供独立的 Mac 连接帮助页，显示并允许复制当前教师机电脑名称。
2. 帮助页说明 Windows 主机需要支持并启用远程桌面、Mac 需使用 Microsoft Windows App、连接账号须是 Windows 主机允许的账户。
3. 提供打开 Windows“设置 → 系统 → 远程桌面”的入口；打开入口只导航到系统设置，不自动启用远程桌面、不修改防火墙或用户权限。
4. 明确说明远程桌面登录会切换教师机本地会话画面；不应在正在投屏或有人使用本地桌面时连接。
5. 连接仅建议在校园可信 LAN 或学校 VPN 上进行；不得自动开放公网 3389 端口、配置路由器转发或保存 Windows 密码。
6. Mac 连接后可以直接使用 Windows 教师控制台的全部桌面功能；手机网页控制继续保持为独立的有限功能集合。

## 非目标

- 不在 Mac 上重写 Veyon 配置器、密钥存储或高权限 Worker。
- 不由 Veyon Campus 自动开启远程桌面、修改本机防火墙/账户，或建立公网隧道。
- 不在应用或生成的连接文件中保存 Windows 凭据。

## 官方连接说明

- [Microsoft：如何使用远程桌面](https://support.microsoft.com/en-us/windows/experience/connectivity-networking/how-to-use-remote-desktop)
- [Microsoft：Windows App 连接到 Windows 设备](https://learn.microsoft.com/en-us/windows-app/get-started-connect-devices-desktops-apps?pivots=remote-pc&tabs=windows-avd%2Cwindows-w365%2Cwindows-devbox%2Cmacos-rds%2Cmacos-pc)
- [Microsoft：从网络外部访问电脑](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/clients/remote-desktop-allow-outside-access)
