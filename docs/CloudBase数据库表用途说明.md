# CloudBase 数据库表用途说明

这是 CloudBase 控制台 PostgreSQL 表列表的快速说明。详细字段、索引、行级权限和迁移记录见[网站 PostgreSQL 数据库设计](../website/docs/PostgreSQL数据库设计.md)。业务表由云函数和网站管理页按授权访问；客户端不直接连接数据库。

## 校区、配置包与版本发布

| 表 | 用途 | 说明 |
|---|---|---|
| `admin_profiles` | 网站管理员身份与角色 | 保存 CloudBase Auth 用户 ID、显示名和 owner/admin/editor/viewer 角色；不保存密码。 |
| `campuses` | 网站登记的正式校区目录 | 保存校区名称、地区、城市和 active/paused 状态。教师发布的配置包不要求校区预先登记。 |
| `deployment_packages` | 学生端可搜索的已发布配置包目录 | 每个 packageId 一行，保存校区名称、电脑前缀、版本格式、文件大小、SHA-256、状态和下载计数等目录元数据。ZIP 文件内容不在这张表里。 |
| `deployment_package_artifacts` | 配置 ZIP 的私有存储定位 | 按 packageId 找到 CloudBase 私有对象存储中的文件；只供服务端使用，学生端不会读取对象键。 |
| `deployment_package_download_attempts` | 配置包下载校验的失败计数与限速状态 | 按 packageId 和客户端地址的 HMAC 指纹记录失败次数、时间窗与暂时封锁时间；不保存原始 IP。 |
| `application_releases` | Teacher/Student 应用更新清单 | 每个已发布角色、架构和版本一行，保存版本号、SHA-256、签名和私有安装包对象键。安装包本体放在独立的私有存储桶，私钥不存入数据库。 |
| `campus_daily_teacher_heartbeats` | 教师端校区每日心跳回执 | 每个匿名校区身份每天一行，保存关联的 packageId、Teacher/Student 版本、配置电脑数及更新时间。失败请求不会写入成功记录；原始教师标识不保存，只存服务端 HMAC。 |

## 每日匿名使用统计

| 表 | 用途 | 保留与可见范围 |
|---|---|---|
| `telemetry_daily_hkt_devices` | 新协议按 UTC+8 日期记录全站设备 HMAC 摘要 | 只用于当日去重，原始安装标识不入库；仅服务端可读，保留 90 天。 |
| `telemetry_daily_hkt_stats` | 新协议按 UTC+8 日期汇总全站独立设备数和心跳请求数 | 网站授权管理员可读取汇总；保留 400 天。 |
| `telemetry_hkt_retention_state` | 新 UTC+8 遥测清理任务的进度 | 通常只有一行，记录上次清理日期；这是维护状态表。 |
| `telemetry_daily_deployment_devices` | 按日期、正式校区、配置包、学生端版本去重的设备 HMAC 摘要 | 只有包关联到已登记校区时才会写入；摘要仅服务端可读，保留 90 天。 |
| `telemetry_daily_deployment_stats` | 上表对应的校区/配置包/学生端版本汇总 | 只有 package 的 `campus_id` 非空时才写入；授权管理员可读取汇总，保留 400 天。 |

当前保留 12 张应用表。旧 UTC 协议的三张遥测/维护表，以及旧版教师登录授权表 `deployment_package_publishers`，已由迁移 `20261001110000` 删除；对应的旧函数和 API 处理代码也已停用。学生端心跳使用 UTC+8 汇总；只有部署包能关联正式 `campuses` 行时，才会同时进入按校区拆分的两张 `telemetry_daily_deployment_*` 表。Teacher 校区心跳使用独立的 `campus_daily_teacher_heartbeats` 表，不等同于学生端匿名统计。首次发布配置包后的 Teacher 心跳会延迟 60 分钟；`configuredComputerCount` 是 Veyon 已配置电脑数，不含学生姓名或其他个人资料。

## 柴桑校区记录核查

`智学前程-柴桑校区` 的 packageId `3d69f9a1-bca5-43b5-bf3a-2c4197c3f90f` 在 `deployment_packages` 中状态为 `published`，且在 `campus_daily_teacher_heartbeats` 中有 2026-10-01（UTC+8）的成功回执，Teacher/Student 版本均为 `0.4.45`，配置电脑数为 `0`。因此柴桑教师端当天确实成功上报了心跳。

该配置包的 `campus_id` 是 `NULL`，因为校区名称尚未关联到 `campuses` 表里的正式校区记录。这不妨碍按 packageId 记录教师心跳；但它不会出现在按正式校区拆分的 `telemetry_daily_deployment_stats` 中。之前的查询把心跳表与 `campuses` 做了必须匹配的内连接，因 `campus_id` 为空而漏掉了这条包记录。这是查询条件的问题，不是部署包不存在。
