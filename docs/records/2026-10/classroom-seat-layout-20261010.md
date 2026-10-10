# 机房座位图实现记录

日期：2026-10-10（Asia/Hong_Kong）

## 实现范围

在现有“地点与学生名单”页面加入本机机房座位图。默认布局从电脑编号范围生成，教师可以设置每排数量，并通过依次点选两台电脑交换座位。布局按本机校区/机房稳定 ID 隔离，独立保存在 `%LOCALAPPDATA%\VeyonCampus\Teacher\classroom-seat-layouts.json`；不复制学生姓名、IP 或 CloudBase 数据。机房编号范围改变时保留仍匹配的电脑位置，并按编号顺序追加新电脑。

Teacher 桌面与配对手机的当前课堂求助卡片可显示行列位置。手机只读获取当前位置，不提供布局编辑。损坏布局会保留原文件并隐藏位置，同时不影响配对、策略或课堂会话 API。

## 主要实现

- `src/VeyonCampus.Core/ClassroomSeatLayouts.cs`：有界严格 JSON、重复字段拒绝、稳定 ID、原子保存、文件锁、列数校验和范围协调。
- `src/VeyonCampus.App/TeacherViewModel.cs` 与 `TeacherWindow.axaml`：机房页座位编辑、自动保存、桌面求助卡片行列提示。
- `src/VeyonCampus.App/TeacherMobileControlServer.cs` 与 `MobileWeb/app.js`：活动课堂只读位置 API 和手机求助卡片显示。
- `tests/VeyonCampus.Checks/ClassroomSeatLayoutChecks.cs` 与 `MobileControlApiChecks.cs`：默认顺序、交换、调整列数、范围变化、坏文件保护、API 位置与坏布局降级。
- [机房座位图使用指南](../../机房座位图使用指南.md) 与 [专项规格](../../../specs/classroom-seat-layout/requirements.md)。

## 自动验证

- `dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1 -p:VeyonCampusIncludeInstaller=true`：成功，0 警告、0 错误。
- `dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -m:1 -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusIncludeInstaller=true`：成功，0 警告、0 错误。
- `dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release -p:VeyonCampusIncludeInstaller=true`：**68/68 检查通过**。座位 API 检查包括活动课堂返回位置、无课堂返回空列表，以及损坏布局时仍返回正常会话但不泄漏部分位置。
- `node --check src/VeyonCampus.App/MobileWeb/app.js` 与 `git diff --check`：通过。

## 实机验收边界

尚未在 Windows 上验证卡片尺寸、键鼠/触屏交换操作及布局重启持久性，也尚未通过真实手机浏览器与校园 LAN 验证求助位置显示。上述自动检查和跨平台构建不代表 Windows AppLocker、HTTP.sys、防火墙或手机证书环境通过。后续在可恢复测试机上完成实机验收。
