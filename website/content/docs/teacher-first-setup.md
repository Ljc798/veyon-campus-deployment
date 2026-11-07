---
id: "teacher-first-setup"
slug: "teacher-first-setup"
title: "第一次设置教师端要做什么？"
summary: "先检查教师端环境，再准备机房资料和学生端配置；不要跳过身份核对。"
category: "teacher"
audience: ["teacher"]
keywords: ["首次设置", "教师端", "向导", "机房配置", "学生配置包"]
aliases: ["教师端第一次打开怎么配置", "教师端首次打开设置", "第一次打开教师端怎么设置"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["src/VeyonCampus.App/Presentation/Views/TeacherSetupView.axaml", "docs/学生端首次部署与恢复指南.md"]
related: ["classroom-location", "student-config", "download-tools", "prepare-before-install"]
status: "published"
---

第一次设置时，先确认教师电脑上的 Veyon 环境，再准备机房与学生电脑资料。只有完成核对后，才把对应校区配置交给学生电脑使用。

## 建议顺序

1. 从官网下载安装[教师端](./download-tools.md)，按学校流程完成安装。
2. 启动“首次设置向导”，核对教师电脑及校区信息。
3. 建立或读取机房地点与电脑名单，确认没有重复地点或错绑设备。
4. 按页面提示生成与本校区对应的学生配置。
5. 先在一台可恢复的学生电脑上部署，并从教师端检查连接。

配置身份、公钥或证书发生变化时，不要靠重新生成配置包反复尝试。先核对校区身份与已有学生设备所信任的身份，再由负责管理员安排安全迁移。

教师端界面、生成按钮和实际部署结果仍应在目标 Windows 版本上核验。本文没有替代学校的密钥保管和设备恢复流程。
