# 基础 App（0.1.0）

这一阶段先建立 C# + Avalonia 桌面 App。**当前只做配置输入、资料读取和部署预览，不执行安装、改名、账户修改，也没有局域网分发功能。** 原有脚本和视频的使用方式保持不变。

## 已有功能

- 学生端、教师端页面入口；教师端明确显示待实现的功能。
- 学生端手动填写校区、电脑名前缀和 1–99 编号，实时生成规划名称。
- 支持将单个部署包文件夹或其中的 `campus.json` 拖入资料区域，拖入时高亮；不接受多个包或 ZIP 压缩包。
- 侧栏与操作按钮统一悬停、按下配色，保持文字对比度。
- 读取原教师脚本生成的部署文件夹（`campus.json` 和 `*-public.pem`），兼容 UTF-8 BOM；不读取 `admin.txt`。
- 校验编号、命名长度、配置字段和公钥文件路径，拒绝私钥内容。
- 可选改名，默认保留本机名称；显示操作计划，修改输入后旧计划自动失效。
- 当前公钥仅做文件和 PEM 标记检查，不代表密钥密码学有效、配置可部署或设备可连接。

## 开发运行

安装 .NET 10 SDK。在仓库根目录执行：

```sh
dotnet run --project src/VeyonCampus.App
```

开发机首次还原 NuGet 依赖需要网络。发布后的自包含 App 可离线运行；目标电脑无需安装 SDK。macOS 可用于界面和计划预览，真正的部署功能将只支持 Windows。

运行配置及状态检查：

```sh
dotnet run --project tests/VeyonCampus.Checks
```

这些检查覆盖错误输入、旧部署包兼容、路径越界、私钥误选、表单变化和失败时的状态清理，不会修改系统配置。

## 发布给 Windows 试用

```sh
dotnet publish src/VeyonCampus.App -c Release -r win-x64 --self-contained true -o artifacts/windows-x64
```

把整个 `artifacts/windows-x64` 文件夹复制到 Windows 测试机，双击 `VeyonCampus.exe`。不要只复制 EXE；其余文件是离线运行所需依赖。此版本不包含 Veyon 安装程序。

Apple Silicon Mac 可打包为本地预览 App：

```sh
dotnet publish src/VeyonCampus.App -c Release -r osx-arm64 --self-contained true -o artifacts/macos-arm64/VeyonCampus.app/Contents/MacOS
cp packaging/macos/Info.plist artifacts/macos-arm64/VeyonCampus.app/Contents/Info.plist
```

然后打开 `artifacts/macos-arm64/VeyonCampus.app`。这是本地开发预览产物，尚未进行发行签名或公证。

## 手动验收

1. 打开 App，默认显示学生端，开始部署按钮不可用。
2. 填入“演示校区”、`PC-`、`3`，应显示 `PC-03`；生成预览后确认默认不改名。
3. 勾选改名后，旧预览消失；重新生成，计划增加改名步骤。
4. 输入 `0`、`100` 或过长前缀，应显示中文错误，不显示成功计划。
5. 选择现有部署包，检查校区和前缀；取消选择不改变表单；无效包显示错误并清空旧资料。
6. 拖入部署文件夹或其中的 `campus.json`，应读取同样资料；拖入无效文件夹应显示错误，移出拖拽区域后高亮消失。
7. 悬停侧栏与操作按钮，文字保持清晰，选中页面仍有区分。
8. 切换教师端后再返回，学生端输入仍保留。窗口缩小时可滚动查看全部内容。

## 本次验证记录

- .NET 10.0.401 + Avalonia 12.1.3 编译通过，修正弃用接口后为零警告、零错误。
- 六组可执行检查通过，覆盖输入边界、改名计划、预览失效、BOM 部署包、路径与私钥拦截、错误资料状态清理。
- macOS Apple Silicon 实际打开自包含 App，检查中文显示、编号预览、空校区错误、计划生成及教师端页面切换。
- Windows x64 自包含发布成功；尚未在 Windows 实机运行或验证任何部署操作。

## 后续顺序

1. 接入学生端只读环境检查：Windows 版本、运行权限、已安装 Veyon、部署资源。
2. 接入学生端安装和公钥配置，逐步骤执行、验证和记录；在 Windows 测试机验收。
3. 接入可选改名、账户配置及失败处理。
4. 完成教师端认证、密钥复用、电脑列表和学生部署包生成。
5. 最后增加局域网分发。

## 代码布局

- `src/VeyonCampus.App`：Avalonia 界面和界面状态。
- `src/VeyonCampus.Core`：独立于界面的资料校验和部署计划。
- `tests/VeyonCampus.Checks`：不依赖测试框架的可执行检查。

后续 Windows 执行层单独实现，不把系统修改写进按钮事件。当前没有网络服务、遥测、后台常驻服务或自动提权。

技术参考：[Avalonia 文件选择接口](https://docs.avaloniaui.net/docs/services/storage/storage-provider)、[.NET 发布说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。
