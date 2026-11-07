---
id: "end-class"
slug: "end-class"
title: "下课后怎样恢复网站访问？"
summary: "先执行对应的恢复操作并核对逐台结果，再结束课堂；离线设备恢复后需重新检查。"
category: "classroom"
audience: ["teacher"]
keywords: ["结束课堂", "下课", "解除黑名单", "恢复网站", "限制到期", "自动解除"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/教师端课堂快捷管控使用指南.md", "src/VeyonCampus.App/Presentation/Views/TeacherClassroomView.axaml"]
related: ["apply-website-policy", "classroom-offline", "website-policy-modes"]
status: "published"
---

下课后，使用教师端明确提供的“恢复网站访问”或解除课堂管控操作，并逐台检查结果。策略到期时间到达后，学生端可能自动解除本工具设置的限制；仍要核对实际浏览器状态。

## 收尾步骤

1. 在课堂页面核对本堂课目标设备数量。
2. 执行恢复网站访问，等待各设备状态返回。
3. 处理“失败”“离线”或“待核对”设备。离线设备重新连接后，刷新状态并由教师发起新的检查或恢复。
4. 在测试电脑确认普通网页访问恢复。
5. 只有课堂任务与限制均已确认处理后，再结束课堂。

教师电脑上保存的常用名单不会因为恢复学生电脑而删除。其他管理员或学校组策略设置的限制，也不会由本工具的课堂恢复操作自动移除。
