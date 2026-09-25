# App 预览版（0.2.0）

现有 C# + Avalonia App 支持独立操作计划与部分只读检查。**当前不执行安装、改名、账户修改，也没有局域网分发功能。** 原有脚本和视频的使用方式保持不变。

后续工作按 [完整开发任务清单](开发路线与任务清单.md)、[架构约束](架构与实现约束.md) 和 [测试发布清单](测试验收与发布清单.md) 推进。2026-09-25 已接入 1–150 命名和操作独立选择的预览；下文区分预览功能与真实 Windows 部署。

## 已有功能

- 学生端、教师端页面入口；教师端可生成最多 150 条机房名称预览，尚不能写入 Veyon。
- 学生端独立选择 Veyon、电脑改名、创建普通学生账户、修改指定管理员密码；未选项不会进入计划。账户预览仅收集目标名称，不输入或保存新密码。
- 四项操作右侧有 `i` 说明按钮：悬停可快速查看，点击可展开完整说明；普通账户本身不保证阻止下载或运行软件。
- 学生端改名支持编号 1–150，01–99 保留两位，100–150 使用三位；批量清单与单机共用规则。
- 教师端生成清单后可点击“关闭预览”，保留前缀、起始编号和数量，便于修改后重新生成。
- 支持将单个部署包文件夹或其中的 `campus.json`／`manifest.json` 拖入资料区域，拖入时高亮；不接受多个包或 ZIP 压缩包。
- 文件夹选择与拖入清单使用同一包根目录解析；悬停不会读取大型资源，导入失败会清空旧包并在资料区旁显示错误。
- 侧栏与操作按钮统一悬停、按下配色，保持文字对比度。
- 读取原教师脚本生成的部署文件夹（`campus.json` 和 `*-public.pem`），兼容 UTF-8 BOM；不读取 `admin.txt`。
- 可只读解析早期 `manifest.json`，校验公钥和安装资源的大小、SHA-256 及包内路径。此格式尚缺正式协议中的版本兼容与来源信任字段，**不能用于执行安装**。
- 校验编号、命名长度、配置字段和公钥文件路径；解析 RSA 公钥、记录指纹，拒绝私钥、假 PEM 和重复字段。
- 修改资料或表单后旧计划与只读报告自动失效；Veyon 计划会再次核对包内配置及公钥文件的摘要。
- 只读环境检查附带时间、计划摘要和所选包摘要；非 Windows 系统阻断真实执行，并将 Windows 专项检查标为“不适用”。Windows 上当前只读检查系统构建、架构和部分所选资源，其余权限、账户、Veyon 服务等仍标为未知。
- 已加入 NuGet 锁文件和 Windows GitHub Actions 构建检查配置；实际 Actions 运行结果尚未取得。

## 开发运行

安装 .NET 10 SDK。在仓库根目录执行：

```sh
dotnet run --project src/VeyonCampus.App
```

开发机首次还原 NuGet 依赖需要网络。发布后的自包含 App 可离线运行；目标电脑无需安装 SDK。macOS 可用于界面和计划预览，真正的部署功能将只支持 Windows。`packages.lock.json` 用于锁定依赖；CI 以 `dotnet restore VeyonCampus.slnx --locked-mode` 检查。

运行配置及状态检查：

```sh
dotnet run --project tests/VeyonCampus.Checks
```

这些检查覆盖 1–150 边界、独立操作、部署包入口路径、旧部署包兼容、新版清单资源摘要、RSA 公钥、路径越界、资料被替换与状态清理，不会修改系统配置。

## 发布给 Windows 试用

```sh
dotnet publish src/VeyonCampus.App -c Release -r win-x64 --self-contained true -o artifacts/windows-x64
```

把整个 `artifacts/windows-x64` 文件夹复制到 Windows 测试机，双击 `VeyonCampus.exe`。不要只复制 EXE；其余文件是离线运行所需依赖。此版本不包含 Veyon 安装程序。

Apple Silicon Mac 可打包为本地预览 App：

```sh
dotnet publish src/VeyonCampus.App -c Release -r osx-arm64 --self-contained true -o artifacts/macos-arm64/VeyonCampus-0.2.0.app/Contents/MacOS
cp packaging/macos/Info.plist artifacts/macos-arm64/VeyonCampus-0.2.0.app/Contents/Info.plist
```

然后打开 `artifacts/macos-arm64/VeyonCampus-0.2.0.app`。这是本地开发预览产物，尚未进行发行签名或公证。

## 手动验收

1. 打开 App，默认显示学生端，开始部署按钮不可用。
2. 不选择任何操作时生成预览，应提示至少选一项；单选改名、编号 `3`，应显示 `PC-03`，不要求部署包。
3. 编号改成 `100` 和 `150`，应分别显示 `PC-100` 与 `PC-150`；`0`、`151` 或过长前缀应报错。
4. 只选创建学生账户或修改管理员密码时，不应要求部署包，计划中不出现改名或 Veyon。
5. 选择现有部署包，检查校区和前缀；取消选择不改变表单；无效包显示错误并清空旧资料。
6. 拖入部署文件夹或其中的 `campus.json`／`manifest.json`，应读取同样资料；拖入无效文件夹应显示错误，移出拖拽区域后高亮消失。
7. 悬停侧栏与操作按钮，文字保持清晰，选中页面仍有区分。
8. 悬停学生端四个 `i` 查看简短说明，点击查看完整说明；再次点击可收起。教师端以默认 `PC-`、起始 1、数量 150 生成清单，检查首尾及 99/100；关闭预览后输入保持不变，数量 151 应报错。
9. 只读环境检查不执行修改；Mac 显示非 Windows，Windows 未核实的安装、账户或权限项目显示未知。
10. 切换教师端后再返回，学生端输入仍保留。窗口缩小时可滚动查看全部内容。

## 本次验证记录

- .NET 10.0.401 + Avalonia 12.1.3 编译通过、零错误。本机网络受限时 NuGet 漏洞公告读取产生 NU1900 警告；不能把此次构建当作已完成依赖漏洞审计。
- 十一组可执行检查通过，覆盖 150 台边界、独立操作、预览失效、只读报告、部署包入口、旧部署包、早期 manifest、RSA 公钥和资料替换。
- macOS Apple Silicon 已打开 0.2.0 自包含 App，确认中文文字、四个选项、资料区域及只读检查入口显示；本版尚未完成完整的原生交互验收。
- Windows x64 预览版已交叉发布，尚未在 Windows 实机运行；任何系统部署操作均未接入。
- 拖拽入口已实现并编译；操作系统文件管理器拖入的端到端成功／失败验收记录仍待补齐。
- Mac 原生界面已验证操作说明点击展开、教师端生成 150 台清单及关闭预览；Windows 界面仍待验收。

## 后续顺序

1. 建立 Windows 测试基线，补齐真实拖拽与界面验收，确认旧包与目标 Veyon 的实际兼容性。
2. 扩展只读预检到 Windows 身份、服务和环境，再建立执行引擎、按需权限、日志与失败恢复基础。
3. 接入学生端 Veyon 离线安装、公钥配置，并分别实现改名、创建学生账户及指定管理员改密。
4. 完成教师端认证、密钥复用、150 台电脑列表和学生部署包生成。
5. 完成真实试点与本地离线核心版交付，最后增加局域网分发。

详细任务 ID、依赖、交付条件和待确认事项以完整任务清单为准，本节不单独维护另一份任务状态。

## 代码布局

- `src/VeyonCampus.App`：Avalonia 界面和界面状态。
- `src/VeyonCampus.Core`：独立于界面的资料校验、命名、独立部署计划和早期只读预检。
- `tests/VeyonCampus.Checks`：不依赖测试框架的可执行检查。

后续 Windows 执行层单独实现，不把系统修改写进按钮事件。当前没有网络服务、遥测、后台常驻服务或自动提权。

技术参考：[Avalonia 文件选择接口](https://docs.avaloniaui.net/docs/services/storage/storage-provider)、[.NET 发布说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。
