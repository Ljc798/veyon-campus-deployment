---
id: "campus-identity"
slug: "campus-identity"
title: "为什么校区身份不匹配会导致操作失败？"
summary: "学生电脑会验证教师端发来的身份；先核对校区和信任状态，不要自行替换公钥。"
category: "faq"
audience: ["teacher", "school-it"]
keywords: ["校区身份", "密钥", "公钥", "信任", "配置包", "换教师电脑", "身份不匹配"]
aliases: ["教师端和学生端身份不一致", "学生电脑不信任教师端", "公钥信任不匹配", "换了教师电脑后操作失败", "校区身份标识", "校区身份标识在哪里确认", "学生端提示校区身份不匹配"]
errorCodes: ["409"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/学生端首次部署与恢复指南.md", "docs/教师端密钥迁移架构复核.md"]
related: ["student-config", "student-update-pending", "error-409", "maintenance-service"]
status: "published"
---

学生电脑会核验教师端或校区配置的身份。名称相同不一定代表仍在使用同一套受信密钥；身份不匹配时，学生端可能拒绝策略或更新请求。

## 看到身份或签名错误时

1. 核对教师端当前选中的校区以及学生电脑最初部署时使用的校区。
2. 保留错误原文、设备版本和出现时间。
3. 请有权限的管理员读取并比较当前身份状态，确认是否发生过教师电脑更换、密钥轮换或配置误选。
4. 只有经过核对和批准的迁移流程才能调整信任关系。

不要反复生成新的校区配置、替换学生电脑上的公钥、删除密钥或关闭签名验证。这些做法可能破坏原有设备的信任链。普通教师遇到该提示应停止操作并联系管理员。
