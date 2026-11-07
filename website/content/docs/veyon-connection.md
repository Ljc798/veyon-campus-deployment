---
id: "veyon-connection"
slug: "veyon-connection"
title: "教师端连接不上学生电脑，怎么排查？"
summary: "Veyon 连接和 Campus 教师长连接是两条通道；按学生端维护页的签名诊断逐项排查。"
category: "troubleshooting"
audience: ["teacher", "school-it"]
keywords: ["连不上", "长连接", "找不到学生电脑", "Veyon", "教师端", "学生端通信", "AgentHealth", "双向消息"]
aliases: ["教师端连接不上学生端", "教师端无法给学生端发消息", "教师端发不了消息", "学生端无法回消息", "等待教师端连接", "学生端显示等待教师端连接", "Veyon 能连接但教师端长连接失败", "AgentHealth 是 200 ready", "AgentHealth=200 ready", "AgentHealth 200 ready 但教师端连接不上", "AgentHealth 正常但学生消息发不出去", "教师连接配置缺失"]
errorCodes: ["MissingEndpoint", "MissingRootCertificate", "InvalidConfiguration", "DnsFailure", "TcpConnectFailure", "TlsValidationFailure", "AuthenticationRejected", "ProtocolMismatch", "TeacherNotListening", "NetworkFailure", "ConfigurationChanged"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/学生端首次部署与恢复指南.md", "docs/教师端课堂快捷管控使用指南.md", "src/VeyonCampus.Core/StudentAgentTeacherChannelClient.cs", "src/VeyonCampus.Core/StudentAgentChannelProtocol.cs", "src/VeyonCampus.App/TeacherMobileControlServer.cs", "src/VeyonCampus.App/TeacherViewModel.cs", "src/VeyonCampus.App/Presentation/Views/TeacherUpdatesView.axaml"]
related: ["student-offline", "classroom-offline", "deployment-check", "student-config"]
status: "published"
---

先分清三个状态：电脑是否在名单里、Veyon 是否能连接，以及 Campus 教师端的 Agent 长连接是否在线。Veyon 连接和 Agent 长连接彼此独立；Veyon 正常，或单独看到 `AgentHealth=200 ready`，都不能证明教师与学生之间的双向消息通道已经通过 TLS 和身份认证。

## 查看教师端连接诊断

1. 在教师端打开“设备与更新”→“学生端维护”，找到目标学生电脑。
2. 查看“检查时课堂连接”：显示“已认证长连接在线”才代表本次检查时 Agent 通道在线；显示“没有已认证长连接”表示状态接口可读，但课堂长连接没有通过认证。
3. 查看“教师连接诊断”中的最近签名上报、失败代码、详情、时间和“下一步”。记录诊断时间，避免把旧上报当作当前结果。较旧的学生 Agent 协议可能不提供 DNS/TCP/TLS 等分阶段诊断。
4. 若显示 `MissingEndpoint`、`MissingRootCertificate` 或 `InvalidConfiguration`，选择对应学生电脑，点击“同步教师连接配置”。该操作会用当前教师端的安全连接地址与证书同步到所选学生机，并逐台核验 Agent 身份及读回结果。

## 按失败代码处理

- `DnsFailure`：核对教师地址以及学生网络的名称解析。
- `TcpConnectFailure` 或 `TeacherNotListening`：请学校 IT 检查教师连接服务是否监听，以及学生到教师的网络路由和防火墙规则。
- `TlsValidationFailure`：核对教师证书有效期、证书链，以及学生机配置的 TLS 根证书是否匹配。
- `AuthenticationRejected`：由管理员在教师端核对该 Agent 的受信身份；不要删除或自动替换不受信任的密钥。
- `ProtocolMismatch`：核对教师端与学生 Agent 是否为兼容版本。
- `NetworkFailure`：检查学生机网络和教师连接服务日志。`ConfigurationChanged` 可先等待 Agent 重试；仍失败时重新同步教师连接配置。

若 Veyon 可用但 Campus 消息仍无法双向收发，记录教师端版本、学生端版本、上述连接状态和诊断代码、发生时间及网络环境，交给管理员排查。不要关闭防火墙、删除证书或替换密钥来“试通”。
