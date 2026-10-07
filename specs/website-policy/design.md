# 学生机网站限制设计

## 组件边界与数据流

TeacherConsole 从教师选择的目标电脑、校区、模式、域名和时长生成递增 revision 的网站策略，使用独立网站策略签名密钥签署后，通过校园 LAN 直接发送给各 Student Agent。Student Agent 使用学生配置包内的校区网站公钥验证请求，通过 SYSTEM 权限把三个浏览器的机器策略写入 Windows 注册表，并以机器身份密钥签名回复。策略推送不经 CloudBase；CloudBase 只承载带网站公钥的校区配置包。

Student Agent 与学生维护 GUI 是独立进程。Agent 当前由受保护 SYSTEM 开机计划任务运行；Teacher 关闭或断网不影响已装 Agent 的本地策略、到期检查和恢复。手机 PWA 只连接 TeacherConsole，并调用桌面端保存的预设，不保存网站私钥。

## 策略文档与规则编译

- 策略包含 schema version、campus ID、递增 revision、签发时间、模式、规范化域名和可选 `expiresUtc`。JSON 拒绝重复字段和未知字段；正文字节上限为 128 KiB，名单上限为 1000 项。
- 输入允许裸域名，或无凭据、默认端口、无路径/查询/片段的 HTTP/HTTPS URL。规范化为小写 IDNA ASCII 主机，丢弃尾随点并排序去重；不接受 IP、端口、路径和通配符。
- Edge 使用裸主机名 URL-blocklist/allowlist 条目；Chrome 使用 `[*.]domain` 覆盖根域和子域；Firefox 使用 WebsiteFilter `*://*.domain/*`，白名单通过 `*://*/*` 的 Block 与具体 Exceptions 组合表达。
- Blocklist 将所选主机写入拒绝规则；Allowlist 先拒绝一般网页，再放行指定主机；Disabled 生成空名单。
- schema v1 表示无自动到期，schema v2 必须带到期时间。可到期策略最长 24 小时；桌面建议时长 45/60/90/120 分钟。用户需手动重启浏览器使策略效果刷新。

规则格式依据 Microsoft Edge、Google Chrome 和 Mozilla Firefox 官方策略文档；这些文档确认策略字符串语法，不证明某个目标机房的浏览器版本已加载规则。[Edge URL filter](https://learn.microsoft.com/en-us/deployedge/edge-learnmmore-url-list-filter-format)、[Chrome URL pattern](https://chromeenterprise.google/intl/en_ca/policies/url-patterns/)、[Firefox WebsiteFilter](https://firefox-admin-docs.mozilla.org/reference/policies/websitefilter/)。

## 签名、重放与设备身份

教师网站策略私钥与应用、系统和 Developer Release 密钥隔离；网站策略使用 RSA-PSS/SHA-256 签名。Agent 先验证策略信封、校区公钥、严格 JSON、时间窗和版本，再接受新 revision，拒绝错误签名、其他校区、过期、未来签发或重复/倒退 revision。策略公钥随校区配置包分发；旧 schema 若未带该公钥，不支持网站 Agent 安装。

每台回执使用该 Student Agent 的持久身份密钥签名，并绑定当前 nonce、命令/策略摘要、校区和目标身份。Teacher 对首次身份或换钥要求显式审批，网络超时标待核对，不自动重试；旧式无签名响应不计成功。

## 浏览器注册表资源与事务

Windows 后端管理 Edge、Chrome 的 `URLBlocklist`/`URLAllowlist` machine policy，以及 Firefox 的 `WebsiteFilter\Block`/`Exceptions` 企业策略值，并维护本工具所有权影子状态。开始写入前读取现值，检查其是否为空/由本工具拥有；不能安全取得所有权时不覆盖学校组策略或其他管理员配置。

应用、停用、到期和撤销均经过持久 Pending 事务，先记录原值/目标值和阶段，再逐项写入并读回。Agent 启动时恢复未完成事务。恢复前要确认当前值仍与工具写入值一致；若外部软件或管理员改动，保留当前状态并报告冲突。到期扫描在 Agent 启动时执行，并每 30 秒再次检查，只清除本工具仍拥有的值。

Agent 的 ProgramData 文件、启动任务和防火墙规则使用 SYSTEM/Administrators 保护；学生普通账户不能停用任务或修改策略状态。管理员仍可维护或移除工具，界面隐藏不是安全边界。

## Teacher 与手机操作

Teacher UI 支持按机房/电脑选择目标，输入域名并选择黑名单/白名单、签发时长，推送启用或停用。结果包含每台确认、离线、失败、冲突和待核对状态；最近 50 次逐台历史存于当前教师用户本机，网络失败设备可被填回目标列表供教师核对后重新发起新策略。

手机控制仅限本地 Teacher HTTPS 服务和配对设备，使用桌面已保存网站预设；同一套策略签名和身份验签仍由 Teacher 执行。CloudBase 不接收网站策略正文、域名或逐台状态。

## 卸载与恢复

管理员在 StudentSetup 明确确认卸载 Agent 后，Worker 检查安装位置、任务身份、配置摘要、防火墙规则和策略所有权。只有能够确认归属的任务/规则/服务值才删除；浏览器值逐项与工具所有权记录比较后恢复原值。未知文件、任务、外部策略或所有权冲突会中止自动清理并要求复核。卸载后 Veyon 和 StudentSetup 保持不变；浏览器须手动重启。

## 验证矩阵

| 层次 | 证据 | 能证明什么 |
| --- | --- | --- |
| 可移植 .NET 检查 | 当前 53/53 检查集中的域名、三浏览器编译、签名/重放、目标去重和事务回归 | 输入规范、协议与恢复逻辑 |
| Windows 构建/fixture | Agent、Teacher/Student 角色构建及 ACL/防火墙 fixture | 编译、包边界和预期 ACL 判断 |
| Windows 实机 | P12-01–08、P7-13 相关验收 | 真实任务 ACL、普通用户权限、注册表读回、浏览器策略页、网站拦截、重启、到期、冲突、卸载恢复 |
| 手机/校园网 | 手机控制实机验收 | 证书信任、配对、同/跨网、预设触发和逐台回执 |

截至 2026-10-08，策略编译、协议、事务代码和可移植检查有记录证据；Windows 浏览器效果、Agent ACL 与真实手机/校园网验收仍未完成。项目状态见[任务主表](../../docs/开发路线与任务清单.md)，当日检查见[续查记录](../../docs/records/2026-10/续作核查记录-20261008.md)。
