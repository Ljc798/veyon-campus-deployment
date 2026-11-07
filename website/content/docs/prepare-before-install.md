---
id: "prepare-before-install"
slug: "prepare-before-install"
title: "安装前需要准备什么？"
summary: "先确认授权、安装包、校区资料、管理员权限和电脑恢复方式。"
category: "getting-started"
audience: ["teacher", "school-it"]
keywords: ["准备", "首次安装", "测试电脑", "管理员", "恢复", "配置包"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/学生端首次部署与恢复指南.md", "website/app.js"]
related: ["download-tools", "teacher-first-setup", "student-config", "deployment-check"]
status: "published"
---

开始部署前，先确认你有权管理这些电脑，并准备安装程序、对应校区的配置资料、管理员账户和恢复办法。

## 准备清单

1. 通过官网[下载对应角色的安装包](./download-tools.md)，核对版本和文件名。
2. 向本校授权教师取得校区配置。不要自行修改配置文件中的身份、公钥或清单。
3. 在学生电脑上确认有经学校批准、能够执行所选系统操作的账户。
4. 记录电脑的系统版本、当前 Veyon 状态和恢复方式；确认能还原或重装。
5. 先用一台可恢复的测试电脑试装，确认教师端与学生端的连接后再扩大范围。

如果电脑受域、组策略或 MDM 管理，先请学校 IT 确认部署方式。遇到配置身份不匹配、签名检查失败或来源不明时，应停止并找配置提供者核对，不要删除验证文件或替换公钥。
