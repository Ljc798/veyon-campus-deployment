---
id: "website-policy-modes"
slug: "website-policy-modes"
title: "网站黑名单和白名单有什么区别？"
summary: "黑名单阻止列出的网站；白名单只允许列出的网站；组合模式还可以放行例外。"
category: "classroom"
audience: ["teacher"]
keywords: ["黑名单", "白名单", "组合模式", "允许网站", "阻止网站", "策略", "www", "子域名", "www.bdfjiaoyu.com"]
aliases: ["白名单里的域名要不要带 www", "bdfjiaoyu.com", "bdfjiaoyu.com 白名单", "bdfjiaoyu.com 白名单会自动加 www 吗", "www.bdfjiaoyu.com 是否默认允许", "网站地址是否需要加 www", "允许名单的子域名怎么填写"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/教师端课堂快捷管控使用指南.md", "src/VeyonCampus.App/TeacherViewModel.cs", "src/VeyonCampus.Core/WebsitePolicyPresetStore.cs", "src/VeyonCampus.Core/WebsitePolicyListDraftStore.cs"]
related: ["import-study-sites", "apply-website-policy", "end-class", "classroom-offline"]
status: "published"
---

在“策略与预设”里，网站名单有不同工作方式，选择前先理解名单外的网站会怎样。

## 三种模式

- **阻止名单（黑名单）**：阻止列出的网站，其他网站通常仍可访问。
- **允许名单（白名单）**：只允许列出的网站及其子域名访问，名单外的网站会被阻止。
- **组合模式**：可以阻止一份名单，同时放行另一份例外；名单外的网站仍按界面说明处理。

## 带 www 的域名

如果实际访问的网址带有 `www`，请把完整域名也加入名单。例如填了 `www.example.edu`，不要因此假设不带 `www` 的 `example.edu` 一定也会匹配；推送后应在学生电脑上分别打开实际使用的网址确认。

## 旧名单会被迁移

内置常用允许名单包含 `www.bdfjiaoyu.com`。读取本机常用预设或按校区保存的旧名单时，如果其中有精确条目 `bdfjiaoyu.com`，教师端会将该条目替换为 `www.bdfjiaoyu.com` 并保存。这个本机迁移不会修改已经推送到学生电脑的策略；需要学生端使用新名单时，请重新推送并核对回执。

## 保存不等于已推送

修改名单只是保存教师电脑上的草稿或常用预设，不等于已推送到学生电脑。推送前再次核对目标设备、名单内容、模式和有效期，查看每台电脑的结果。

## 学校策略可能覆盖

如果浏览器由学校组策略或其他管理工具控制，页面显示收到回执也不单独证明浏览器已经拦截。请在测试电脑上检查目标网页和浏览器策略状态。
