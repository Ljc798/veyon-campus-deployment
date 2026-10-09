# 课堂状态同步：设计

## 组件边界

- TeacherConsole 维护已有 `ClassroomSessionStore`，使用当前本机已选校区/机房；默认选择最近使用的档案，没有历史选择时取列表第一项。一个明确的开始/下课切换入口不增加地址、期限或目标选择。
- TeacherConsole 使用 `WebsitePolicySigningKeyStore` 为课堂状态构造独立用途的签名消息，通过现有 LAN 端口 39174 发送到机房 `HostOverrides`；未填写覆盖项时使用电脑编号。开始课堂时把签名校区 ID 保存在与 session ID 关联的本机小型上下文文件中，页面切换到其他校区也不会改变当前课堂的信任身份；旧版 Teacher 可忽略该文件并继续读取原课堂记录。
- Student Agent 在 `/v1/classroom/status` 验证教师签名、校区、时间、消息 ID 和严格 schema，仅更新内存中的只读课堂状态；不复用策略应用代码，不写注册表或长期文件。
- Student Agent 在 `/v1/classroom/status/local` 提供只允许 loopback 的本机查询。状态回执沿用 Agent RSA 身份签名；Teacher 仍按现有身份固定记录核对设备回执。
- Student Companion 固定每 5 秒查询 loopback，Agent 返回“未收到/已过期”时自动回到断开状态。窗口继续使用单张状态卡，不显示连接配置或手动重试。

## 协议

教师消息包含 `schemaVersion`、固定 `purpose`、`campusId`、随机 `messageId`、`issuedUtc`、最多两分钟后的 `expiresUtc`、可空 `sessionId`、`active`、可空 `roomName` 和 `targetCount`。结束快照使用 `active: false`、空 `sessionId`/`roomName` 和 `targetCount: 0`。

教师端活动课堂每 30 秒生成新 `messageId` 并刷新；Agent 只接受签发时间不比当前状态旧的唯一消息。未知字段、重复 JSON 字段、非规范编码、错误用途和其他校区消息均拒绝。

Agent 回执签署校区、`messageId`、请求正文 SHA-256、UTC 接收时间与 Agent 版本。Teacher 只在回执签名与已固定 Agent 公钥匹配时报告“已确认”；有效但尚未固定的 Agent 身份显示为“需核对”。

## 安全与故障处理

- 同一端口上的课堂端点有独立路径与 purpose 字符串，不接受策略 JSON；策略签名也不能被解析成课堂消息。
- 本机查询只接受 `127.0.0.1` / `::1` 对端，远程请求拒绝；返回的活动状态再次检查有效期。
- Agent 重启后状态从空开始；Teacher 的定时刷新会重新建立当前状态，不读取或回放本机持久化课堂记录。
- Teacher 网络错误时保留本机 session 生命周期结果，但对设备报告失败/需核对；活动快照会继续刷新。下课操作不因某台离线而继续显示其旧状态，旧状态最长两分钟自然过期。
- 不新增服务发现、WSS 监听、TLS 根证书、UDP 广播、防火墙规则、CloudBase API 或学生端连接设置。双向事件通道另立规格。

## 验证

当前跨平台检查覆盖签名往返、校区/用途隔离、时间窗、状态组合、过期、单调消息、Agent 回执、loopback 判定和 Companion 状态映射。Teacher 目标默认值、逐台推送结果、UI 交互和真实定时轮询未由本机单元检查覆盖；教师与 Agent 间的 Windows HTTP.sys/SYSTEM 通道和 Companion loopback 路由需要现场验收。

跨设备状态链、Windows loopback 路由、登录启动以及断网两分钟过期仍需 Windows 实机验收。
