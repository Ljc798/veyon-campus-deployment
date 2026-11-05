# Veyon Campus 网站视觉重设计 · 实验报告

> 分支：`experiment/skills-evaluation`（独立 worktree `F:/github/veyon-campus-skills-evaluation`）
> 基于：`origin/develop` 的 `0f67214`；本轮沿用上一轮实验分支，**未合并、未推送、未触碰 develop/main、未修改生产环境**
> 日期：2026-11-05
> 使用 Skill：**impeccable**（前端设计工具集），按其 `teach-impeccable` 流程先收集设计上下文再动手

---

## 1. 你确认的四项前提

| 项 | 选择 |
|---|---|
| 改造范围 | **公开官网（6 个路由）+ 管理员后台（`/admin`，9 个页面）**，纯前端 |
| 视觉方向 | **编辑部 / 技术白皮书** |
| 落地位置 | **继续实验 worktree**，分阶段提交，不碰 develop/main |
| 受众气质 | **老师与教务：清晰、亲和** |

后两项之间存在张力：白皮书容易偏冷峻，而老师场景要求低压力、易读。最终处理是**保留白皮书的骨架（排版、细线、栏目编号、克制留白），用暖纸色、墨蓝、放宽行高与字号做亲和化**——像一本排版精良的校本教材，而不是法律文件或监控台。完整设计上下文已写入仓库根目录 `.impeccable.md`，后续会话会持续生效。

---

## 2. 范围界定

仓库里其实有三套 Web 页面，本轮只动第一套：

| 套 | 页面 | 本轮是否改动 |
|---|---|---|
| **A** | `website/` 公开官网 + `/admin` 管理员后台（Vite → CloudBase 静态托管） | ✅ **本轮改造对象** |
| B | `src/VeyonCampus.App/MobileWeb/` 手机教师控制台 PWA（编译进 Teacher.dll） | ❌ 未动 |
| C | C# 内联 HTML（连接引导页、可打印座位表） | ❌ 未动 |

A 套独立于 Windows 桌面程序发布（`website/docs/开发规范.md:3`），因此改它的样式**不需要重编 .NET、不影响 C# 数据契约、不影响安装包**。

---

## 3. 变更清单

| 文件 | 类型 | 内容 |
|---|---|---|
| `.impeccable.md` | 新增 57 行 | 设计上下文：用户、品牌气质、美学方向、6 条设计原则、来自 `website/docs/开发规范.md` 与 `docs/ui_rule.md` 的硬约束 |
| `website/editorial.css` | 新增 855 行 | **独立设计层**：token、排版、官网、后台全部组件的视觉覆盖；9 个分区 + 动效 + 窄屏修正 |
| `website/index.html` | +3 / −1 | 在 `styles.css` 与内联 `<style>` **之后**引入 `editorial.css`；`theme-color` 由冷灰白改为暖纸色 `#f8f6f0` |
| `docs/experiments/web-redesign/build-preview.py` | 新增 | 可复现的预览生成脚本：内联三份真实样式（`styles.css` + `index.html` 内联 style + `editorial.css`） |
| `docs/experiments/web-redesign/preview.html` | 新增 | 自包含视觉预览稿，可直接双击打开；`#stage-01`～`#stage-04` 分屏 |
| `docs/experiments/web-redesign/shot-*.png` | 新增 7 张 | 桌面/窄屏、预览稿/真实页面的验收截图 |
| `docs/experiments/web-redesign/REPORT.md` | 新增 | 本报告 |

### 明确未改动

`app.js`、`src/cloud-admin.js`、`admin-data.js`、`admin-navigation.js`、`cloudbase.js`、`public-download-data.js`、任何 C# 文件、`cloudfunctions/veyon-api/`、OpenAPI 契约、`MobileWeb/`、`TeacherMobileControlServer.cs` 的 CSP 白名单与资源映射、PWA 缓存版本号、路由表、CloudBase Auth / grants / RLS、数据库字段与 API 调用。**没有新增或删除任何 DOM 节点、类名、元素 ID 或事件绑定。**

---

## 4. 关键设计决策

1. **独立覆盖层，而不是重写 `styles.css`（74 KB / 935 行）**
   新文件在 HTML 中最后加载，同名选择器以后加载者胜出。这样 diff 清晰、**删除一行 `<link>` 即可完整回退**，也避免碰后台 JS 里 76 KB 的 HTML 字符串模板。已验证 dev 与生产构建中顺序都是 `styles.css → 内联 style → editorial.css`。

2. **暖纸 + 墨蓝 + 朱赭批注，保留品牌色**
   纸面 `#F8F6F0`、墨蓝 `#132E46`、品牌青绿 `#187B70` 作辅助强调、朱赭 `#B04A30` 只作小面积批注与栏目标记。符合规范「品牌色优先深蓝 + 青绿」，同时把冷灰白换成暖底。未引入任何外部字体、图标库或 CDN。

3. **侧栏从深蓝底改成纸色目录页（最大胆的一步）**
   原后台是深色侧栏 + 渐变高亮 + 白字，偏监控台。改为暖纸侧栏、右侧细竖线、朱赭左侧 active 标记、衬线品牌名。因为原 CSS 里有大量 `rgba(255,255,255,.x)` 白字，设计层逐条覆写了侧栏内 **12 类**文字与边框颜色，避免浅底下白字隐形——截图已确认九个导航项、账号区、帮助区全部清晰可见。

4. **可读性硬指标**
   原后台大量 9–10px 文字（表头 9px、状态 9px、操作提示 8px）。设计层统一提升到 **11–13px**，关键状态、失败原因、可点击文案不低于 12px；状态一律保留文字标签 + 图标/形状 + 颜色三重表达，不以颜色单独承载。

5. **去掉模板感**
   圆角收敛到 3px、移除通用阴影与圆形光晕、渐变改实色；改用细线分栏、出版式表格（大写表头 + 细横线）、章节编号（`counter-reset` + `::before`）、左侧竖线边注、衬线大号数字（tabular-nums）、hero 极淡竖线纸格。

6. **动效只作用于状态**
   仅过渡 `color / background-color / border-color / opacity`，使用 ease-out-quart；移除按钮弹跳与卡片位移；完整保留 `prefers-reduced-motion`。响应式沿用原断点，只在末尾补字号修正，不改任何布局结构。

---

## 5. 验证结果

| 项目 | 结果 |
|---|---|
| `npm ci --prefix website` | ✅ 依赖安装成功（Node 22.22.2 / npm 10.9.7） |
| `npm run build --prefix website` | ✅ 0 错误；设计层打包为 `dist/assets/index-BXAZxb8T.css`（27.44 kB / gzip 5.34 kB） |
| `npm run build:static-only --prefix website` | ✅ 通过（该模式按既有设计不产出 `cloud-admin` chunk） |
| 生产 `dist/index.html` CSS 顺序 | ✅ `styles.css` 第 14 行 → 内联 `<style>` 15–140 行 → 设计层 143 行 |
| `node --check app.js` / `cloud-admin.js` | ✅ 语法通过 |
| dev server `GET /`、`/editorial.css`、`/admin` | ✅ 均 200 |
| **真实官网首页**（1440px 无头浏览器截图） | ✅ 暖纸底、衬线大标题、朱红强调、细线检查清单、直角按钮全部生效，无错位 |
| **真实 `/admin`**（本地无 CloudBase 凭据） | ✅ 显示既有的「配置缺失」提示页，新样式生效；非空白、非样式丢失 |
| **后台主界面**（静态预览，真实类名 + 真实 CSS） | ✅ 九个导航项、命令面板按钮、连接横幅、4 个指标卡、折线图、地区分布、校区表格、分页、状态徽章全部完整可读 |
| **窄屏 500px 真实官网** | ✅ 单列布局、汉堡菜单、文字完整、无横向裁切 |
| 敏感信息扫描 | ✅ 当前 `dist` 与源码中无 service API key / `Telemetry__DailyHashKey` 字面值；本轮未引入任何密钥 |
| 并发 develop 冲突 | ✅ 实验期间 `origin/develop` 又前进 4 个提交（v1.1.60、课堂结束原因、注册表快照、文档），改动全在 `src/` 与 `docs/maintenance`，**与 `website/`、`.impeccable.md` 零重叠** |

> 关于 390px 截图：Chrome headless 在此环境存在约 500px 的最小布局视口，`--window-size=390` 会把按 500px 排版的内容右侧裁掉，属**截图工具限制而非页面缺陷**。已用 500px 视口复验真实官网完整无裁切，误导性的两张 390px 截图已删除。

### 已知限制（需你或后续实机补验）

- 本地未配置 `website/.env.local`（CloudBase env ID / publishable key），因此 **`/admin` 真实登录态下的 9 个页面未逐一回归**。静态预览稿使用真实类名与真实样式表覆盖了数据总览主界面，但 Auth 会话、RLS 数据加载、CSV 导出、命令面板、弹窗等交互路径未做登录态验证。
- 站点选择器只在 `kidscode.fun` 主机或 `/sites` 出现，本地 dev 不触发；用预览稿 `stage-01` 验证。
- 预览稿中的校区名、数值、日期均为**版面占位示例**，页面顶部已醒目标注「不代表真实记录」，不写入数据库、不调用 API、不加载 CloudBase SDK。
- 未执行任何部署、推送或合并。

---

## 6. 预览方式

1. **最直接**：打开 `docs/experiments/web-redesign/preview.html`（自包含，无需构建/网络/服务）。
   - `preview.html#stage-01` 站点选择器 · `#stage-02` 公开官网 · `#stage-03` 管理员后台 · `#stage-04` 组件与色板
2. **真实页面**：本地 dev server `http://127.0.0.1:5173/`（`npm run dev --prefix website` 可重启）。`/admin` 需配置 CloudBase 环境变量才会进入登录页。
3. **重新生成预览稿**：`python docs/experiments/web-redesign/build-preview.py`。
4. **一键回退**：删除 `website/index.html` 中 `<link rel="stylesheet" href="/editorial.css" />` 这一行即可恢复原设计。

---

## 7. 合并建议（等待你确认）

**当前状态：本轮 2 个提交后，分支相对 `origin/develop` 为 ahead 7 / behind 4；未设置推送上游。**

建议按以下顺序再决定是否合入：

1. 在配置了 `website/.env.local` 的环境启动 dev server，用真实授权账号逐一核对 `/admin` 的 9 个页面（尤其 `/admin/database`、`/admin/api` 的长表格与代码摘要块、`/admin/downloads` 的双来源版本卡），确认字号提升后表格仍可横向滚动、无溢出。
2. 在 320 / 390 / 768 / 1440px 真机或真实浏览器（非 headless 截图）复核窄屏抽屉导航与表格。
3. `git fetch origin develop` 后重跑 `npm run build --prefix website`；由于零文件重叠，预期无代码冲突，但 behind 4 个提交里含 C# 改动，合并前仍建议先 `merge` 到临时分支做一次完整 `VeyonCampus.slnx -c Release` 构建（网站改动不影响 .NET，此步是防并发回归）。
4. 按 `website/docs/开发规范.md:59` 核对：构建中无 service API key、公开页无后台入口、静态托管回退与上传范围正确，再考虑发布 `website/dist`。

**未经你的明确指令，不会执行任何 merge / rebase / push / deploy。**

若你希望调整方向（例如后台侧栏改回深色、朱赭批注色更淡、或把手机教师控制台 PWA 也纳入），直接说即可——设计层是独立文件，替换成本很低。
