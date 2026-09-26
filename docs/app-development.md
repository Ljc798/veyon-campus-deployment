# App 开发与运行说明（0.4.4）

更新日期：2026-09-26。当前为 **Windows 本地离线部署实验版**。教师安装、教师密钥生成与学生 Veyon 安装／配置会产生真实系统修改；电脑改名、学生账户和管理员改密的本地实现已接入执行器，组合执行入口已开放，Windows 实机验收仍待完成。尚未完成机房交付验收。

[文档导航](README.md) · [任务主清单](开发路线与任务清单.md) · [架构复审](reviews/2026-09-25-0.4.4架构复审.md) · [验证记录](reviews/2026-09-25-验证与变更记录.md)

## 当前能力

| 入口 | 当前行为 | 限制 |
| --- | --- | --- |
| 学生计划 | 四项独立选择、1–150 命名、目标与步骤预览 | 组合执行入口已开放，Windows 实机验证待补 |
| 学生“仅安装” | 预览、预检后运行内嵌 Veyon 安装器，再读回安装、版本和服务；已接入任务租约与资源快照 | 当前也要求校区配置包；不导入公钥；Windows 实机验证待补 |
| 学生“安装并配置” | 消费冻结计划；未安装时先安装，同版跳过安装，再设认证、导公钥、处理服务；支持改名与账户组合执行 | 公钥独立读回需 Windows 实机核对；组合顺序 ADR-010 复核仍待完成 |
| 改名／账户 | `WindowsRenameAdapter`/`WindowsAccountAdapter` 已实现读回优先的本地执行；组合入口可接受 | 域成员改名阻断；账户 SID 读回需 Windows 实机核对；重启后名称生效需实机验证 |
| 教师安装 | 提取校验内嵌安装器，安装 Master，核对版本与 Master 文件 | 已有或未知安装状态会停止；不完成教师认证、私钥权限或电脑目录；不等于服务运行验收 |
| 教师生成配置包 | 按校区 KeyId 查询密钥库，复用完整密钥对，缺失时创建，只导出公钥 | 残缺或异常清单停止；迁移、备份及 CLI 故障恢复未完成 |
| 教师机房预览 | 前缀、起始编号、数量，最多 150 条名称 | 不写入 Veyon 电脑目录，不发现设备或验证连接 |
| 页面与资料 | 文件夹选择、单个文件夹或清单拖入、指纹摘要、失效提示、说明按钮 | 原生拖放、键盘、高 DPI 与真实 UI 验收待补；不支持 ZIP 导入 |

Windows 修改操作要求手动以管理员身份启动整个 App，受限 UAC Worker 尚未实现。预检有效期五分钟，绑定当前表单、资料与事实；执行前重新核对。2026-09-26 起四个修改入口统一在首次 await 前获取任务租约（`TaskLease`/`TaskGate`），并发第二请求被拒绝；安装器与公钥在执行期间使用受控快照（`PackageResourceSnapshot`），源文件替换不影响已验证字节。安装超时仍需核对状态、崩溃恢复与跨进程互斥仍未完成，见复审 AR-02 及 P3-05。暂不用于生产机房部署。

本轮 Release 构建与 24 组本地检查通过（2026-09-26 新增任务租约、资源快照、进程结果三组检查）；不代表 Windows 安装、教师学生连接或 150 台实机通过。macOS 仅用于界面／计划开发，真实部署入口被平台检查阻断。

## 资料格式与离线交付

固定 Veyon `4.11.2.0` x64 安装器嵌入 `VeyonCampus.Core.dll`。App 提取到当前用户的本地应用数据缓存，检查固定文件名、大小、SHA-256；Windows 还检查 Authenticode 和签名证书指纹。部署期间不下载安装器。

| 资料类型 | 读取规则 | 当前 App 的安装来源 |
| --- | --- | --- |
| legacy `campus.json` + `*-public.pem` | 支持 UTF-8 BOM；不读取 `admin.txt`；验证配置与 RSA 公钥 | App 内嵌固定安装器 |
| `schemaVersion=1` manifest | 校验公钥及清单内安装器的大小、摘要与路径 | 当前 UI 仍使用 App 内嵌安装器，不执行旧包任意 EXE |
| `schemaVersion=2` manifest | 当前生成格式；仅清单化公钥，不允许 `installer` 字段 | App 内嵌固定安装器 |

同目录有 manifest 时优先读取 manifest，选择其中的 `campus.json` 不会绕过它。新包实际含 `<校区ID>-public.pem`、`campus.json`、`manifest.json`、`README.md` 四个文件；不含 EXE、密码或教师私钥。当前校区 ID 采用 1–100 位 ASCII 字母、数字、连字符、下划线；导入中文显示名称不代表可直接用它创建 Veyon KeyId，正式 ID／显示名分离仍待实现。

离线交付由 **完整 App 发布文件夹 + 对应校区配置包** 组成，两者都要复制。App ZIP 不含具体校区配置，配置包不含运行程序。只复制 EXE 或只复制配置包均不足以部署。

公钥检查支持 RSA 2048–4096 位，拒绝私钥、假 PEM、重复／错误字段及路径越界等。摘要用于检测内容变化，不证明校区资料发布者身份，也不等于已建立执行期间不可替换的资源副本。

## 开发与检查

安装 .NET 10 SDK；`global.json` 指定 10.0.100 并允许 `latestFeature`，本轮实际使用 10.0.401。首次依赖还原需要网络，锁文件约束 NuGet 依赖；目标电脑运行自包含发布版无需 SDK。

在仓库根目录执行：

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
dotnet restore VeyonCampus.slnx --locked-mode
dotnet build VeyonCampus.slnx -c Release --no-restore
dotnet run --project tests/VeyonCampus.Checks -c Release --no-build --no-restore
dotnet run --project src/VeyonCampus.App
```

检查程序写入临时测试目录并读取平台状态，不运行真实部署。Windows GitHub Actions 配置已存在，远程运行结果未核实。本轮使用已有依赖的构建不等于干净检出或完全离线构建已验证。

## 打包与分发

在仓库根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-windows-offline.ps1
```

脚本按项目版本生成 `artifacts\windows-x64-v0.4.4` 与 `artifacts\VeyonCampus-0.4.4-win-x64-offline.zip`，发布 Windows x64 自包含 App，并从发布后的 Core 程序集重读安装器资源验证大小及 SHA-256。

已有同名输出时默认拒绝。`-Clean` 用于本地重建时替换指定输出，已对外交付版本应递增版本并保留原包。可用 `-OutputDirectory`、`-ZipPath` 指定 artifacts 内的新位置。已还原同一依赖组合的 win-x64 资源后可用 `-SkipRestore`；开发机可离线重建的范围取决于依赖缓存。

解压后保留整个文件夹，在可恢复 Windows 测试机以管理员身份运行 `VeyonCampus.exe`。复制对应校区配置包，在 App 中选择目录。教师生成包前需已安装固定版本 Veyon；同一校区换用新的输出目录会复用完整密钥对，不应通过删除密钥解决输出路径冲突。

Apple Silicon 开发预览：

```sh
dotnet publish src/VeyonCampus.App -c Release -r osx-arm64 --self-contained true -o artifacts/macos-arm64/VeyonCampus-0.4.4.app/Contents/MacOS
cp packaging/macos/Info.plist artifacts/macos-arm64/VeyonCampus-0.4.4.app/Contents/Info.plist
```

该 Mac 产物仅用于本地预览，未进行发行签名或公证。Windows 发布包的已知摘要和历史打包证据见验证记录；本轮文档修订没有重新打包。

## 手工验收入口

先在可恢复测试环境验收，再扩大设备范围。完整编号与记录模板见 [测试清单](测试验收与发布清单.md)。

1. 不选操作应拒绝预览；单选改名无需包，核对 3/99/100/150、0/151 及过长前缀；账户预览不输入密码。
2. 有效包、无效包、取消选择、拖放与导航往返分别核对；清除包保留其他输入，重置表单才恢复全表默认。
3. 资料或表单变更后预览、预检和执行资格失效；缺包、未提升权限、报告过期及未实现组合均不得开始修改。
4. 两条学生执行路径、教师安装及包生成分别测试正常、重复点击、等待期间修改输入、失败与重启；不能用模拟协调器结果替代入口测试。
5. 两次同校区输出的公钥应一致；检查无私钥混入、残缺密钥停止、生成中断后保留已创建事实；已有安装不可被教师入口覆盖。
6. 一教师一学生完成离线安装、公钥核对、服务、拔除介质、双方重启和日常教师身份连接，并保留失败恢复证据。

## 代码布局与下一步

`src/VeyonCampus.App` 当前同时包含界面状态和执行编排；`src/VeyonCampus.Core` 同时包含规则、资料、协调器及 Windows 适配与安装资源，尚未实现平台隔离。`tests/VeyonCampus.Checks` 是可执行检查程序，直接编译链接 MainViewModel；`tools/VerifyEmbeddedResource` 用于发布资源复核。

任务租约（AR-01）、进程接口（AR-05）、资源快照（AR-03）与运行记录（P3-09）已在 2026-09-26 完成本地实现与检查；下一步按任务主清单推进受限 Worker、跨进程互斥（P3-05）、备份与恢复，再完成 Windows 实机改名／账户验收。局域网分发仍后置。
