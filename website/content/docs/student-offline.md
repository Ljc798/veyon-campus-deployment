---
id: "student-offline"
slug: "student-offline"
title: "学生电脑显示离线，怎么办？"
summary: "先确认电脑和网络，再分别检查名单、Veyon 和 Agent 通道。"
category: "troubleshooting"
audience: ["teacher", "school-it"]
keywords: ["离线", "不在线", "连不上", "掉线", "学生电脑", "Agent 回执"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/设备健康中心现场验收指南.md", "docs/教师端课堂快捷管控使用指南.md"]
related: ["veyon-connection", "classroom-offline", "maintenance-service", "deployment-check"]
status: "published"
---

先别急着重装。名单里看得到电脑只说明目录有记录；离线状态可能来自关机、网络、主机名解析、Veyon 或教师端 Agent 通道。

## 检查步骤

1. 在学生电脑确认设备已开机并连接到学校网络。
2. 核对教师端当前地点、学生名单和电脑 Host 是否正确。
3. 分别测试 Veyon 连接与教师端设备状态读取。
4. 如果只有 Agent 回执缺失，记录对应状态和时间；不要据此断定电脑关机。
5. 仍未恢复时，请学校 IT 核对网络分区、防火墙、服务状态和日志。

连接恢复后重新读取状态。离线期间尚未确认的控制命令不应被当成成功；按照教师端显示的结果，重新核对目标后再由教师决定是否重试。
