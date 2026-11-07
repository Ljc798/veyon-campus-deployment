---
id: "duplicate-location"
slug: "duplicate-location"
title: "Veyon 里出现两个同名地点怎么办？"
summary: "同名地点不一定是同一份设备目录；先查看每个地点的电脑和绑定关系。"
category: "troubleshooting"
audience: ["teacher", "school-it"]
keywords: ["重复地点", "同名地点", "Veyon Location", "地点重复", "目录"]
aliases: ["Veyon 出现两个同名机房", "Veyon 里地点重复了", "两个同名地点是不是同一个机房"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/机房座位图使用指南.md", "src/VeyonCampus.App/Presentation/Views/TeacherRoomView.axaml"]
related: ["classroom-roster-mismatch", "classroom-location", "seat-chart", "add-student-computer"]
status: "published"
---

两个地点名称相同，不代表它们包含相同设备或具有相同身份。不要急着删除其中一个。

## 先核对，再决定

1. 分别打开两个地点，记录电脑数量和设备名。
2. 在座位图中核对 UUID、Host 和座位关联，确认哪一份是当前实际使用的目录。
3. 如果设备已经关联到另一份地点档案，先查看教师端提示的冲突位置。
4. 由学校管理员确定正式地点后，再安排合并或清理；操作前保留可恢复的数据副本。

## 合并前的注意事项

不要只按地点显示名称自动合并，也不要在身份未确认时重复导入全部电脑。合并后从 Veyon 和教师端重新核对名单。
