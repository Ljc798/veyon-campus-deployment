---
id: "add-student-computer"
slug: "add-student-computer"
title: "机房新增一台学生电脑怎么办？"
summary: "先确认设备身份和地点，再在目录中添加或绑定，并检查 Veyon 读回结果。"
category: "devices"
audience: ["teacher", "school-it"]
keywords: ["新增电脑", "新电脑", "添加学生机", "电脑名单", "Host", "UUID"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/机房座位图使用指南.md", "src/VeyonCampus.App/Presentation/Views/TeacherRoomView.axaml"]
related: ["classroom-location", "seat-chart", "computer-number", "deployment-check"]
status: "published"
---

新增电脑前先确认它属于哪个机房、由谁授权部署，以及系统中的计算机名和 Veyon 地址。不要仅凭相同的座位编号把新电脑当作旧设备。

## 操作步骤

1. 按学校流程完成学生端部署，并记录新设备的名称与 Veyon 连接信息。
2. 在教师端打开正确地点，确认没有已存在的同一设备记录。
3. 为新设备创建座位或绑定到对应的座位记录。
4. 保存后核对 UUID、Host、电脑编号和学生姓名，再刷新策略与课堂名单。
5. 从教师端实际连接这台新电脑。

## 遇到冲突怎么办

若发现设备已绑定到其他地点，先处理冲突记录，不要重复导入同一台电脑。地点与座位写入后的实际读回仍需在目标环境验收。
