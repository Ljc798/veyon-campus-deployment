# Mac 教师端首次设置与屏幕巡视

本文说明 Apple Silicon Mac 上运行 TeacherConsole、迁移现有学生机信任关系、准备 Veyon 截图 helper，并在配对手机上查看学生屏幕的步骤。Mac 教师端在本机运行，不需要 Windows 远程桌面。

## 当前范围

- Mac 教师端运行 Avalonia TeacherConsole；屏幕采集通过本机 Ubuntu 24.04 ARM64 容器中的固定 Veyon 4.11.4 CLI 完成。
- 手机控制服务只使用活动以太网或 Wi-Fi 接口上的私有 IPv4 地址，不把隧道接口用于手机连接或证书地址；启动手机服务前请确认 Mac 和手机在可互通的校园网。
- Mac 不能直接接管旧学生配置包中的策略私钥。首次使用前，需要在 Mac 上生成新的策略密钥，并让每台学生机重新应用更新后的配置包。
- 学生尚未更新并读回新信任公钥时，Mac 会把该设备显示为未信任或未知；不会把策略发送成功或设备健康状态伪报为已确认。
- 当前本机 `.app` 构建未做 Developer ID 签名和公证，只适用于维护者本机预览和试点。向其他 Mac 分发前需完成签名和公证。
- 编译 Mac 教师端需要 Apple Silicon Mac 和 .NET 10 SDK；运行屏幕巡视 helper 还需要 Docker Desktop。首次构建 helper 需要访问 Ubuntu 软件源和 Veyon 官方源码下载地址。

## 构建并启动

在仓库根目录运行：

```sh
VEYONCAMPUS_DOTNET=dotnet bash scripts/publish-teacher-macos.sh
```

如果 `dotnet` 不在 `PATH`，将 `VEYONCAMPUS_DOTNET` 改为本机 .NET 10 可执行文件的完整路径。脚本会生成 `artifacts/macos/` 下带版本号的 `Veyon Campus Teacher-*.app`。在 Finder 中打开该应用即可启动教师端。此应用包不会包含 Windows StudentSetup 或 Worker。

### 向其他 Mac 分发

默认构建不签名，只适用于维护者本机预览。要启用 macOS 登录项并向其他 Mac 分发，需要 Apple Developer ID Application 证书和已保存到钥匙串的 Apple 公证凭据。

1. 运行 `security find-identity -v -p codesigning`，复制完整的 `Developer ID Application: ...` 身份名称。
2. 使用 Apple 提供的 Apple ID、Team ID 和 app 专用密码创建钥匙串公证配置，例如：

   ```sh
   xcrun notarytool store-credentials veyon-campus-teacher \
     --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" \
     --password "$APPLE_APP_SPECIFIC_PASSWORD"
   ```

   不要把 app 专用密码、签名私钥或公证凭据写入仓库。
3. 设置发布脚本环境变量并重新构建：

   ```sh
   export VEYONCAMPUS_MACOS_SIGNING_IDENTITY='Developer ID Application: 完整证书名称'
   export VEYONCAMPUS_MACOS_NOTARY_PROFILE='veyon-campus-teacher'
   VEYONCAMPUS_DOTNET=dotnet bash scripts/publish-teacher-macos.sh
   ```

脚本会对应用内 Mach-O 文件启用 hardened runtime 和安全时间戳，签名后验证应用，再提交公证；只有 Apple 返回 Accepted、票据成功装订且系统评估通过时才会报告签名公证完成。缺少证书或公证配置时，脚本会明确拒绝相应操作。公证配置可留空以构建仅签名的应用，但该应用还不能作为已公证版本分发。

首次设置页可以开启当前用户的“登录时启动教师端”。该设置使用 macOS 13+ 的 `SMAppService`，需要代码签名后的 `.app`；首次启用后，若系统要求批准，按提示在“系统设置 > 通用 > 登录项”中允许应用。当前维护者本地构建未签名，不能完成登录项注册，界面会显示 macOS 返回的原因。登录启动只负责打开 TeacherConsole；如还要在登录后恢复手机服务，请先在教师端启动一次手机控制服务，让它保存自动恢复偏好。

## 迁移现有学生机信任关系

Mac 只支持基于原有学生配置包生成策略密钥更新包；不能把它当作一个没有 Veyon 信任关系的新校区包使用。

1. 在教师端“首次设置”第二步，创建地点、机房和学生座位，并将学生座位关联到现有学生电脑。
2. 在第三步选择原 Windows 教师端生成的完整学生配置包文件夹。应用会校验包清单，并读取原有校区、公钥和电脑编号前缀。
3. 从原 Windows 教师端安全导出与该学生包 Veyon 公钥匹配的教师私钥。在 Mac 界面显示的命令中复制准确的 `veyon-cli authkeys export .../private <临时 PEM 文件>` 命令后执行。不要通过聊天或邮件传输 PEM；按学校的密钥传输规则将临时文件交给 Mac 管理员。
4. 在 Mac 上选择该 PEM 并导入钥匙串。应用会先校验私钥与原包公钥一致，再写入当前登录用户的钥匙串；不匹配时不会导入，也不会覆盖已有密钥。如 macOS 显示钥匙串访问提示，只允许本次应用操作读取该密钥。导入成功后，按学校流程删除临时 PEM。
5. 选择一个尚不存在的空输出目录，生成策略密钥更新包。Mac 会保留原 Veyon 公钥、原电脑前缀和兼容信息，并使用本机钥匙串中的策略签名密钥生成更新包。
6. 先将 StudentSetup 更新到与该配置包兼容的版本，再由管理员在每台学生机重新应用更新包。逐台记录完成状态；学生端完成前，Mac 上该学生的策略、设备状态和手机操作必须保持未信任/未知。

**不要**在学生尚未应用新配置包时把旧的学生信任状态当作 Mac 已能读取或控制的证明。原策略恢复、Mac 新签名状态读回、手机策略回执和身份固定都要在每台目标机逐项确认。

## 准备并恢复 Veyon helper

1. 启动 Docker Desktop，打开 TeacherConsole 的“设备健康”页，选择“准备 Veyon helper”。
2. 首次准备会构建 Ubuntu 24.04 ARM64 镜像并编译 Veyon 4.11.4，可能需要较长时间、网络访问和 Docker 磁盘空间。构建或下载失败时，查看界面给出的错误并在网络恢复后重试。
3. helper 使用固定容器名 `veyon-campus-helper-arm64`，以非特权 bridge 模式运行且不向主机发布端口。启动脚本和健康检查会拒绝开启特权模式、端口映射或不符合要求的 tmpfs 配置。截图目录和 Veyon 密钥目录挂载在 `/run/veyon-helper` tmpfs 中；私钥从当前用户钥匙串临时注入 helper，容器重启后会重新注入。容器停止或重建时，tmpfs 中的密钥和截图随之清除。
4. 健康页显示 helper 组件就绪，只证明本机 Docker、镜像、容器和固定版本 CLI 可用；它不代表私钥已匹配、校园网已连通或学生截图已验证。
5. Docker Desktop 重启后，打开 TeacherConsole 并查看健康页状态。应用会尝试恢复已有 helper 和钥匙串密钥；未恢复时重新点“准备 Veyon helper”。不要手动删除钥匙串中的校区密钥或手机 TLS 根证书，否则可能使学生信任或已配对手机失效。

## 连接课堂并在手机查看屏幕

1. 确认教师 Mac 和学生电脑处于可互通的校园网；学生电脑已运行 Veyon 服务，且配置包中的 Veyon 公钥与 Mac 钥匙串中导入的教师私钥匹配。请由网络管理员按学校规则确认 Veyon 所需的 LAN 访问和防火墙策略。
2. 在教师端选择正确校区、地点和活动课堂，核对学生姓名、座位和电脑绑定。先读取设备签名状态；未固定身份、状态未知或未完成密钥迁移的设备不能作为成功验收。
3. 在教师端“手机控制”启动服务并生成二维码。手机连接说明、证书安装与信任、教师批准配对步骤见[手机控制使用指南](手机控制使用指南.md)。教师 Mac 和手机必须处于同一可互通的校园网。
4. 手机进入“看屏幕”，点“开始查看全部”。页面按当前课堂座位显示已关联学生的缩略图，只读取当前屏幕可见的卡片；查看单台画面时点该学生卡片上的“看实时画面”。
5. 点“停止查看屏幕”、离开画面页或结束课堂后，巡视会停止。座位或学生绑定变化时，服务端会撤销旧巡视租约并清除会话缓存画面。

手机控制服务与 Veyon 截图使用不同的 LAN 连接。手机页面能够打开或成功配对，不等于 Mac 到学生机的 Veyon 截图链路已通。

## 故障定位

| 现象 | 检查步骤 |
| --- | --- |
| helper 无法准备 | 确认 Docker Desktop 已启动、磁盘空间足够，并可下载 Ubuntu 软件包和固定校验值的 Veyon 源码；之后在健康页重试。 |
| helper 就绪但没有学生画面 | 确认已导入与所选学生配置包公钥匹配的私钥、helper 状态能读回对应校区密钥、课堂目标和座位关联正确，并由网络管理员确认 Veyon LAN 连通性。 |
| 手机显示未信任或状态未知 | 确认此学生已用最新 StudentSetup 重新应用 Mac 生成的策略密钥更新包，再读取签名状态并核对固定身份。不要重复发送策略来绕过信任检查。 |
| 重启 Docker 后 helper 缺少密钥 | 确认当前 macOS 登录钥匙串已解锁、教师端可读取该校区密钥，然后在健康页重新准备 helper。不要把 PEM 留在 helper 容器或普通目录中。 |
| 手机配对或证书失败 | 按[手机控制使用指南](手机控制使用指南.md)核对二维码安全码、手机证书信任、同一校园网和教师服务状态。 |

## 现场验收记录

Mac Release 构建和 helper 固定版本检查不能替代真实课堂验收。试点时至少记录：Mac 型号与 macOS 版本、TeacherConsole 版本、helper 状态、学生 StudentSetup/Agent 版本、校区公钥迁移结果、教师端与学生端 LAN 连通性、手机型号与系统版本、逐台签名状态、截图首帧耗时、刷新间隔、停止/离页后取消结果以及课堂规模。当前仍需在真实 Windows 学生机、校园网和 Android/iPhone 上逐项完成这些检查。
