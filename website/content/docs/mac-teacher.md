---
id: "mac-teacher"
slug: "mac-teacher"
title: "教师端 macOS 版能做什么？"
summary: "教师端包含 Apple Silicon Mac 构建，但部分设备目录和 Windows 管理操作存在平台差异。"
category: "teacher"
audience: ["teacher", "school-it"]
keywords: ["Mac 教师端", "macOS", "Apple Silicon", "Veyon", "系统要求"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["src/VeyonCampus.App/VeyonCampus.App.csproj", "src/VeyonCampus.App/TeacherVeyonControlPlane.cs", "src/VeyonCampus.App/TeacherWindow.axaml.cs"]
related: ["system-requirements", "teacher-first-setup", "veyon-connection"]
status: "published"
---

教师端源码包含 Apple Silicon（ARM64）macOS 构建目标。**这不表示 Windows 与 Mac 上每个菜单、设备管理和课堂操作都完全相同。**

## 使用前先确认

1. 到官网[下载页面](/download)查看当前实际提供的安装包。若没有 macOS 安装包，不要把 Windows 安装包复制到 Mac 上运行。
2. 核对这台 Mac 已按学校要求安装并配置 Veyon。Veyon 地点读取、电脑名单和屏幕预览都有 macOS 专用处理流程，第一次使用应在可恢复的测试环境核对。
3. 需要修改学生 Windows 电脑的注册表、Windows 服务或其他 Windows 专用设置时，使用学校批准的 Windows 管理电脑完成。
4. 如果 Mac 上的菜单或结果与 Windows 教师端不同，记录当前版本、操作系统和完整提示，先不要据此推断学生电脑已经收到策略。

项目源码中存在相应平台实现不等于功能已经在学校现场验收。课堂连接和策略效果仍需在本校网络、目标学生电脑及当前发行版本上逐项确认。
