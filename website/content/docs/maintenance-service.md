---
id: "maintenance-service"
slug: "maintenance-service"
title: "学生电脑后台维护服务是什么？"
summary: "后台维护组件负责部分受授权的系统级工作；普通使用者通常不需要手动运行它。"
category: "faq"
audience: ["teacher", "school-it"]
keywords: ["Maintenance", "维护服务", "后台 Agent", "学生 Agent", "更新", "服务未运行"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/学生端首次部署与恢复指南.md", "src/VeyonCampus.Core/WebsitePolicyRuntime.cs"]
related: ["student-update", "student-update-pending", "student-offline"]
status: "published"
---

学生电脑可能包含后台组件，用于接收经验证的策略或维护请求。它与前台的学生部署工具不是同一个窗口，也不能把后台进程存在简单等同为教师端已经连通。

## 遇到问题时

- 在教师端查看当前设备状态、最近回执和错误说明。
- 如果页面提示组件未安装、未运行或版本不符，按页面允许的固定维护动作处理。
- 若显示身份变化、事务恢复或安装文件缺失，保留提示并联系管理员；不要手动删除计划任务、目录或密钥。
- 维修操作前确认学生电脑不在考试或正在上课的关键时段，并按学校流程准备恢复方案。

后台组件和服务状态需要在目标 Windows 设备上核实。网络超时并不一定代表服务已停止；应以新鲜、可信的读回状态为准。
