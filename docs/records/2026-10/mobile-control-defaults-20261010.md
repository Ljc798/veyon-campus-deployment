# 手机控制默认项精简记录

日期：2026-10-10

范围：减少已配对手机打开教师控制页后的初始选择与视觉干扰，同时保留必要的范围复核。

## 用户操作

- 活动课堂目标与 Veyon 清单完整、唯一匹配时，手机默认选中全班电脑并收起范围调整区。首屏 HTML 不再预先展开清单，避免脚本确认默认范围前短暂显示大量电脑选项。
- 没有活动课堂或目标无法完整匹配时，代码主动展开范围调整区并保持未选择，便于教师核对而不会扩大策略范围。
- 多个预设中默认选择最近更新的网站/应用预设；长期系统预设排在临时课堂策略之后。页面将默认项标记出来，教师仍可在下拉列表中改选。
- 默认目标与预设只减少选择步骤，不会自动读取以外地启用/解除策略，不跳过应用执行策略审核。

## 实现

- `MobileWeb/app.js` 按配置更新时间排序；无效或缺失的时间稳定地保留存储顺序。为了降低默认误选长期基线的可能，系统预设始终排在网站/应用预设之后。
- `MobileWeb/index.html` 移除范围清单的初始 `open` 属性。已有匹配逻辑仍会在无活动课堂或完整匹配失败时主动展开。
- 手机控制需求和设计记录明确默认策略及失败关闭行为；项目未增加新的用户配置项。

## 验证

- `node --check src/VeyonCampus.App/MobileWeb/app.js`：通过。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release --no-restore`：完整可移植检查 **65/65** 通过。
- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1`：Release 构建 0 警告、0 错误。
- `git diff --check`：通过。

本次没有运行真实手机浏览器回放；本机未检测到 Playwright/Puppeteer 或可用 Chromium 命令。行为变更仅调整前端默认排序和 `<details>` 初始状态，需在后续 Android/iOS 实机验收中确认显示效果。

Android/iOS 浏览器、证书信任和真实校园 LAN 验收仍按[手机控制任务](../../../specs/mobile-teacher-control/tasks.md)保持开放。
