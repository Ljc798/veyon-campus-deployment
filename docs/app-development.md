# App 开发与运行说明（0.4.4）

更新日期：2026-09-26。当前为 **Windows 本地离线部署实验版**。教师安装、教师密钥生成、学生 Veyon 安装／配置与电脑改名会产生真实系统修改；学生账户创建和管理员改密已恢复为仅预览，在密码输入与 SID 确认完成前不开放执行。Windows 实机验收与机房交付验收尚未完成。

[文档导航](README.md) · [任务主清单](开发路线与任务清单.md) · [架构复审](reviews/2026-09-25-0.4.4架构复审.md) · [验证记录](reviews/2026-09-25-验证与变更记录.md)

## 当前能力

| 入口 | 当前行为 | 限制 |
| --- | --- | --- |
| 学生计划 | 四项独立选择、1–150 命名、目标与步骤预览 | 仅开放 Veyon / 改名组合；包含账户操作的计划在任何修改前拒绝 |
| 学生“仅安装” | 预览、预检后运行内嵌 Veyon 安装器，再读回安装、版本和服务；已接入任务租约与资源快照 | 当前也要求校区配置包；不导入公钥；Windows 实机验证待补 |
| 学生“执行所选操作” | 消费冻结计划；未安装时先安装，同版跳过安装，再设认证、从快照导公钥、处理服务；支持与改名组合 | 最终读回参与结果汇总；预期公钥指纹尚未独立确认时保持需核对 |
| 改名 | 使用 `Rename-Computer`；明确读取域状态、活动名称及待生效名称；不自动重启 | 域成员或未知状态阻断；已有其他待生效名称不覆盖；真实改名与重启验收待补 |
| 账户 | 使用 `Get-LocalUser` 读取明确账户的名称和 SID；创建／改密仅预览 | 执行入口、预检及适配器均阻止账户修改，不创建空密码账户 |
| 教师安装 | 提取校验内嵌安装器，安装 Master，核对版本与 Master 文件 | 已有或未知安装状态会停止；不完成教师认证、私钥权限或电脑目录；不等于服务运行验收 |
| 教师生成配置包 | 按校区 KeyId 查询密钥库，复用完整密钥对，缺失时创建，只导出公钥 | 残缺或异常清单停止；迁移、备份及 CLI 故障恢复未完成 |
| 教师机房预览 | 前缀、起始编号、数量，最多 150 条名称 | 不写入 Veyon 电脑目录，不发现设备或验证连接 |
| 页面与资料 | 文件夹选择、单个文件夹或清单拖入、指纹摘要、失效提示、说明按钮 | 原生拖放、键盘、高 DPI 与真实 UI 验收待补；不支持 ZIP 导入 |

Windows 修改操作要求手动以管理员身份启动整个 App，受限 UAC Worker 尚未实现。预检有效期五分钟，绑定当前表单、资料与事实；执行前重新核对。四个修改入口在首次 await 前取得进程内 `TaskLease` 和当前用户 ACL 的单实例命名管道租约，阻止其他 App 实例并发；双实例运行验收待完成。该管道只作互斥锁，不传送提权请求。公钥导入实际消费 `PackageResourceSnapshot` 副本，Windows 执行期间保持只读共享句柄以阻止副本改写／删除；安装器仍使用单独的缓存及信任校验，不属于该公钥快照。安装超时、崩溃恢复和 Worker 权限边界仍未完成，见复审 AR-02 及 P3-05。暂不用于生产机房部署。

本轮 Windows Release 构建与 29 组本地检查通过，覆盖进程失败／超时、账户阻断、改名模拟、SID 解析、快照实际导入路径和最终状态记录。详见 [b66751e 审阅修复记录](reviews/2026-09-26-b66751e修复记录.md)。不代表真实改名、Windows 安装、教师学生连接或 150 台实机通过。macOS 仅用于界面／计划开发，真实部署入口被平台检查阻断。

## 学生端软件的生命周期与角色边界

学生端所选部署操作完成并经系统读回确认后，可以关闭并移除便携式 App 文件夹；Veyon 服务、已导入的公钥和 Windows 中已应用的配置不会因此自动卸载或还原。当前 App 没有自动清理入口，移除前应保留必要的运行结果和恢复资料。

当前交付仍是教师／学生共用的完整 App，默认学生流程不能阻止打开教师入口；学生专用交付物尚未完成。教师私钥不应出现在学生机。教师／学生交付物隔离列入 P7；完成隔离前，不能把“部署后删掉 App”作为唯一的角色访问控制措施。

课堂网站限制尚未实现。未来若采用学生机上的常驻策略服务，部署 App 文件夹可以移除，但该服务必须保留并有独立的更新、状态检查和卸载流程；若采用集中策略，按该策略的生命周期维护。详见 [P12 课堂网站限制](开发路线与任务清单.md)。

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

从 Git 克隆后，可在 Windows x64 开发机上自行生成离线 ZIP。需要安装 .NET 10 SDK；首次还原依赖时通常需要互联网访问 NuGet 源。固定版 Veyon 安装器已随仓库跟踪，无需另行下载。

```powershell
git clone https://github.com/Ljc798/veyon-campus-deployment.git
cd veyon-campus-deployment
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-windows-offline.ps1
```

脚本会在 `artifacts` 下生成 Windows x64 自包含目录和 ZIP。`/artifacts/` 被 `.gitignore` 排除，因此其他人克隆仓库时不会自动得到生成的 ZIP；他们需运行脚本自行构建。要让他人直接下载预制 ZIP，应在代码进入目标提交后将验收／发布包作为 GitHub Release 资产发布。

脚本按项目版本生成 `artifacts\windows-x64-v0.4.4` 与 `artifacts\VeyonCampus-0.4.4-win-x64-offline.zip`，发布 Windows x64 自包含 App，并从发布后的 Core 程序集重读安装器资源验证大小及 SHA-256。

已有同名输出时默认拒绝。`-Clean` 用于本地重建时替换指定输出，已对外交付版本应递增版本并保留原包。可用 `-OutputDirectory`、`-ZipPath` 指定 artifacts 内的新位置。已还原同一依赖组合的 win-x64 资源后可用 `-SkipRestore`；开发机可离线重建的范围取决于依赖缓存。

解压后保留整个文件夹，在可恢复 Windows 测试机以管理员身份运行 `VeyonCampus.exe`。复制对应校区配置包，在 App 中选择目录。教师生成包前需已安装固定版本 Veyon；同一校区换用新的输出目录会复用完整密钥对，不应通过删除密钥解决输出路径冲突。

Apple Silicon 开发预览：

```sh
dotnet publish src/VeyonCampus.App -c Release -r osx-arm64 --self-contained true -o artifacts/macos-arm64/VeyonCampus-0.4.4.app/Contents/MacOS
cp packaging/macos/Info.plist artifacts/macos-arm64/VeyonCampus-0.4.4.app/Contents/Info.plist
```

该 Mac 产物仅用于本地预览，未进行发行签名或公证。当前 Windows x64 验收包是从工作区重新生成的临时 0.4.4 产物；其摘要、文件位置及人工验收边界见 [工作进度报告](工作进度报告-2026-09-26.md)，不代表正式签名发布。

## 手工验收入口

先在可恢复测试环境验收，再扩大设备范围。完整编号与记录模板见 [测试清单](测试验收与发布清单.md)。

当前工作区验收 ZIP、摘要及本轮需要手工验证的项目见 [2026-09-26 工作进度报告](工作进度报告-2026-09-26.md)。

1. 不选操作应拒绝预览；单选改名无需包，核对 3/99/100/150、0/151 及过长前缀；账户预览不输入密码。
2. 有效包、无效包、取消选择、拖放与导航往返分别核对；清除包保留其他输入，重置表单才恢复全表默认。
3. 资料或表单变更后预览、预检和执行资格失效；缺包、未提升权限、报告过期及未实现组合均不得开始修改。
4. 两条学生执行路径、教师安装及包生成分别测试正常、重复点击、等待期间修改输入、失败与重启；不能用模拟协调器结果替代入口测试。
5. 两次同校区输出的公钥应一致；检查无私钥混入、残缺密钥停止、生成中断后保留已创建事实；已有安装不可被教师入口覆盖。
6. 一教师一学生完成离线安装、公钥核对、服务、拔除介质、双方重启和日常教师身份连接，并保留失败恢复证据。

## 代码布局与下一步

`src/VeyonCampus.App` 当前同时包含界面状态和执行编排；`src/VeyonCampus.Core` 同时包含规则、资料、协调器及 Windows 适配与安装资源，尚未实现平台隔离。`tests/VeyonCampus.Checks` 是可执行检查程序，直接编译链接 MainViewModel；`tools/VerifyEmbeddedResource` 用于发布资源复核。

任务租约、进程接口、公钥快照与运行记录已有本地实现与回归检查；跨进程命名管道锁已接入，但双实例验收待完成。下一步推进受限 Worker、备份与恢复，补齐账户密码及 SID 确认流程，再进行系统修改验收。独立公钥指纹读回与真实改名／重启验收仍待完成；局域网分发继续后置。
