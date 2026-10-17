# Edge 实时网站限制 P0 原型

本目录是独立的 Manifest V3 验证原型，不接入 Student Agent、Teacher、安装器或生产更新包。它只维护自己的动态规则 ID `1`：点击按钮后，对 `bilibili.com` 及其子域名的后续顶层导航执行拦截；再次点击移除后解除该规则。原型不读取标签页 URL，不需要 `host_permissions`，也不重启 Edge。

## 在隔离测试机验证

1. 使用没有现存 Edge URLBlocklist/URLAllowlist 企业策略的可还原测试环境。按设计文档，正式 P0 环境应为非 AD Windows 11、普通学生账号。
2. 在 Edge 打开 `edge://extensions`，启用“开发人员模式”，选择“加载解压缩的扩展”，指向本目录。
3. 打开 `edge://version` 并记下浏览器进程 ID。保持此 Edge 窗口运行。
4. 在该窗口中导航到 `https://www.bilibili.com/`，确认基线可达。
5. 打开本扩展，点击“拦截 bilibili.com 的新导航”，确认状态显示规则已写入并读回；在同一 Edge 窗口再次导航到 `https://www.bilibili.com/`，确认新导航被拦截，并确认 `edge://version` 的浏览器进程 ID 未变化。
6. 点击“移除原型规则”，再次导航到该网站，确认网站恢复可达且浏览器进程 ID 未变化。
7. 另测根域、子域和相似域（例如 `notbilibili.com`），记录 Edge/Windows 版本、学生账号类型、请求结果与进程 ID。

如果测试网络本身无法访问目标网站，结果应记为“网络条件不足”，不能记作规则成功或失败。原型目前只覆盖 `main_frame` 新导航，不处理已加载页面、子资源、多个 Profile、InPrivate 或失联恢复。

## P0 状态

此目录仍是独立的 P0 DNR 原型，不连接 Student Agent、Teacher 或安装器。真实机房验收按当前工作安排暂缓；因此这里没有 P0 通过结论，也没有证明正式强制安装渠道或浏览器内实际拦截行为。

仓库中的 P1 集成代码包括 MV3 扩展、Native Messaging Host、Agent 签名快照与回执、教师端逐台状态及启用/恢复入口。源码完成不代表 P0 或 Edge 实机验收通过。默认离线包不配置 Edge 商店扩展 ID，迁移功能编译门控关闭，学生 Agent 继续使用旧注册表模式；独立 Beta 试用包需要手动加载解压扩展，也不得描述为正式静默分发已启用。

恢复 P0 后仍需按上文验证非 AD Windows 11 的商店强制安装、普通学生账号保护、扩展 ID 更新稳定性、Host 握手、多个 Profile/InPrivate 覆盖及无重启的拦截/恢复。启用与恢复流程都不自动重启 Edge；浏览器何时读取恢复后的机器策略仍需实机核对。
