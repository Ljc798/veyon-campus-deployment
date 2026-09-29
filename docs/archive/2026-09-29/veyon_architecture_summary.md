> 归档于 2026-09-29：保留原始实现与讨论记录，不作为当前工作指引。当前入口见 [文档索引](../../README.md)。

# Veyon Campus Deployment：架构与发布方案总结

> 更新日期：2026-09-29。
>
> 目的：整理架构结论，并结合既有工作指引给出后续实施顺序、迁移要求和验收门槛。
>
> 核心原则：先把一次性部署体验做好，再补最小必要的云端能力；Student GUI 是部署工具，长期运行的是 Agent。
>
> 阅读方式：第 1–33 节描述目标方案，不代表功能已实现；第 34–40 节给出当前基线和执行建议。任务状态仍以[开发路线与任务清单](../../开发路线与任务清单.md)为唯一主表，沿用 P0–P12 编号，不在本文重复维护完成勾选。

---

## 1. 当前产品定位

项目不是替代 Veyon，而是把 Veyon 在学校机房中的首次部署、配置、网站策略管理和后续维护做得更简单。

当前主要角色：

- **Teacher App**
  - 初始化 Veyon
  - 生成/管理校区密钥
  - 管理 Veyon 地点和学生机
  - 下发网站黑白名单策略
  - 后续负责检查更新、下载 Student 更新、在 LAN 内分发更新
  - 后续负责向云端发送校区 heartbeat

- **Student Setup**
  - 只用于首次部署或后续管理员维护
  - 完成环境检查、Veyon 配置、账户/计算机名配置、Agent 安装、策略测试
  - 部署完成后不再作为普通学生日常使用的 GUI

- **Student Agent**
  - 长期后台静默运行
  - 接收 Teacher 的签名命令
  - 应用网站策略
  - 后续负责接收更新命令、下载安装包、调用 Updater
  - 向 Teacher 返回状态、版本、更新结果

---

## 2. 第一阶段最重要的任务

当前不建议继续扩大量云端功能。

发布前优先级：

1. **把 Student Setup 改成清晰的 Wizard**
2. 在干净的 Teacher / Student Windows VM 上完整验收
3. 改为正规 Windows Installer
4. 增加最小自动更新能力
5. 再发布首个真正给学校使用的版本

核心发布闭环：

```text
Teacher 安装
→ 初始化 Veyon
→ 生成校区配置
→ Student 获取配置
→ Student Wizard 部署
→ Veyon 连接成功
→ 网站黑白名单策略测试成功
→ Agent 留在后台
→ Student GUI 退出日常使用
```

---

## 3. Student Setup 应该改成固定页面 Wizard

Student Setup 不是“设置中心”，而是一次性的部署流程。

当前已确认：**不再使用原来的纵向长页面 + 上下滚动结构。**

新的交互采用固定页面 Wizard：

```text
[1 配置] → [2 检查] → [3 部署] → [4 完成]
```

顶部使用 stepper / 箭头显示当前步骤：

- 当前步骤高亮
- 已完成步骤显示 ✓
- 未开始步骤保持普通状态
- 页面主体只展示当前步骤内容
- 底部操作区固定，不跟内容滚动

例如：

```text
[✓ 配置] → [✓ 检查] → [3 部署中] → [4 完成]
```

这样用户始终知道：

- 当前在哪一步
- 上一步做了什么
- 下一步要做什么
- 当前是否可以继续

### Step 1：配置部署内容

主要内容：

- 选择 / 获取校区配置包
- 显示校区名称
- 显示 Campus ID
- 输入或选择电脑编号
- 根据编号预览目标电脑名
- 勾选需要执行的操作

建议提供以下独立执行项。下方勾选仅演示用户选择后的状态，不代表默认勾选；改名、创建账户和修改管理员密码继续默认不选。修改指定管理员密码保留独立选项和目标 SID 确认，不合并进“创建学生账户”：

```text
☑ 导入 / 配置 Veyon
☑ 修改电脑名称
☑ 创建 / 初始化学生普通用户
☑ 安装 Student Agent
```

页面示例：

```text
校区配置
[ 选择配置包 ]

校区：柴桑校区一教室
Campus ID：VC-000001

电脑编号
[ 03 ]

目标电脑名
PC-03

部署内容
☑ 配置 Veyon
☑ 修改电脑名称
☑ 创建学生账户
☑ 安装 Student Agent
```

底部：

```text
[取消]                         [下一步]
```

### Step 2：环境检查

这一页只做“是否可以安全开始部署”的确认。

检查项包括：

```text
✓ 当前 Windows 版本支持
✓ 当前具有管理员权限
✓ 已检测到 Veyon
✓ 校区配置有效
✓ 校区公钥有效
✓ 目标电脑名称合法
✓ 学生账户状态可处理
✓ Student Agent 安装资源可用
```

**检查结果应直接显示在页面内，而不是只依赖弹窗。**

例如：

```text
环境检查

✓ 管理员权限
✓ Veyon 已安装
✓ 配置包有效
✕ 目标电脑名称冲突
✓ Agent 安装资源有效
```

失败项应显示简短原因，并允许用户处理后：

```text
[重新检查]
```

只有与所选操作相关的关键检查全部通过后才允许进入下一步。尚未安装 Veyon、但已选择安装且可信离线资源齐备时，应显示“需要安装”，不能直接阻断；网络不可达不能阻断能够离线完成的本地操作。

底部：

```text
[上一步]            [重新检查]            [下一步]
```

### Step 3：执行部署

这一页执行真正的系统修改。

不要只显示一个 loading spinner，应逐项显示任务状态：

```text
正在部署...

[1/5] 配置 Veyon            ✓
[2/5] 修改电脑名称          ✓
[3/5] 创建学生账户          ✓
[4/5] 安装 Student Agent    ✓
[5/5] 测试网站策略通信      进行中...
```

每个任务至少应有：

- 等待中
- 进行中
- 成功
- 失败
- 已跳过

如果任务失败：

- 明确显示失败项
- 不把整个流程误报为成功
- 提供“查看详细信息”
- 如果安全可行，提供“重试失败项目”

高级信息默认折叠：

```text
查看详细信息 >
```

里面可以包含：

- Veyon CLI 输出
- Agent 安装日志
- 服务状态
- 文件路径
- 错误详情

底部主要按钮：

```text
[上一步]                     [开始部署]
```

开始部署后，应避免随意返回上一步导致状态混乱。

### Step 4：完成

最终页集中展示部署结果。

示例：

```text
部署完成

✓ Veyon 配置成功
✓ 校区公钥导入成功
✓ 学生账户处理成功
✓ Student Agent 安装成功
✓ 网站黑白名单策略通信测试成功

电脑名称：PC-03
Agent 状态：运行中
Veyon 状态：正常

注意：
电脑名称将在重启后完全生效。
```

底部：

```text
[稍后重启]              [立即重启]
```

也可以增加：

```text
[完成并退出]
```

### Wizard 的固定交互原则

1. **不再使用整个页面纵向长滚动。**
2. 顶部 stepper 固定表达流程进度。
3. 主区域一次只展示当前步骤。
4. 底部导航按钮固定。
5. 环境检查结果显示在页面内。
6. 部署过程逐项显示状态，不只显示等待圈。
7. 高级日志默认折叠。
8. 失败后允许重新检查或重试失败项目。
9. 完整首次部署只有 Veyon 部署验证和 Agent / 网站策略验证都成功后，才进入最终完成状态。仅改名或仅账户操作按所选范围验证；教师离线时保存“本地部署完成、连接待验收”，不能冒充完整首次部署成功。
10. 部署完成后 **GUI 不删除**，而是退出普通使用路径，保留为管理员维护工具。

不要默认把大量技术说明堆在主界面。用户最需要知道的是：

```text
我现在在哪一步
这一页要做什么
是否成功
如果失败该怎么办
下一步是什么
```

---

## 4. Student GUI 部署完成后怎么处理

不要把 GUI 真正删除。

对于完整首次部署，当以下两项都验证成功：

```text
Veyon 部署 / 连接验证成功
+
Student Agent / 网站黑白名单策略通信验证成功
```

Wizard 才进入最终完成状态。独立维护操作只验证所选范围，不据此将整机首次部署标记为完成。

完成后应把 Student Setup 从“日常入口”变成“管理员维护工具”。

正确方式：

```text
StudentSetup.exe
→ 保留在 Program Files
→ 不创建或移除桌面快捷方式
→ 不在普通学生的开始菜单突出显示
→ 不自动启动
→ 普通 User 没有管理员权限
```

部署完成后，本地记录：

```json
{
  "deploymentCompleted": true
}
```

以后管理员再次启动 StudentSetup.exe 时，可以显示：

```text
此设备已完成部署

[查看状态]
[修复部署]
[卸载 / 清理]
```

需要管理员权限才能执行修改。

### 为什么不要删除 GUI

未来可能需要：

- 修复 Agent
- 重新导入校区配置
- 迁移校区
- 清除网站策略
- 卸载
- 排查异常

所以 GUI 应该从“日常入口”变成“管理员维护工具”。

---

## 5. 卸载策略

正常 Windows 软件不应该要求用户重新找到原 ZIP 或安装包才能卸载。

Installer 应注册标准卸载入口：

```text
Windows 设置
→ 应用
→ 已安装的应用
→ Veyon Campus Student
→ 卸载
```

卸载程序负责：

```text
停止 Student Agent
→ 删除 Windows Service
→ 清理 Veyon Campus 自己创建的网站策略
→ 删除程序文件
→ 清理自身配置
```

默认不要自动卸载 Veyon。

可以让管理员选择：

```text
☑ 删除 Veyon Campus Student Agent
☑ 删除网站控制策略
☐ 同时卸载 Veyon
```

---

## 6. Campus ID 是什么

Campus ID 不是每台学生机一个。

更合适的定义：

> 一个校区 / 一个教室部署环境共享一个 Campus ID。

例如：

```text
VC-000001  柴桑校区一教室
VC-000002  柴桑校区二教室
VC-000003  浔阳校区一教室
```

同一个教室中的：

```text
Teacher
PC-01
PC-02
PC-03
...
```

都知道自己属于：

```text
campusId = VC-000001
```

如果以后需要区分单机，可以再单独使用：

```text
deviceId
computerName
```

Campus ID 的核心用途是：

- 判断消息是不是发给本校区
- 防止其他校区的 Teacher 命令被当前 Student 接受
- 云端按校区进行统计

---

## 7. Teacher → Student 的通信应该复用

网站策略和更新可以复用同一套 LAN 通信框架。

不要为每种功能开一个端口。

推荐：

```text
Student Agent
↓
一个监听端口
↓
解析 message.type
↓
分发给不同 handler
```

例如：

```json
{
  "type": "website-policy"
}
```

或者：

```json
{
  "type": "update-command"
}
```

未来还可以有：

```json
{
  "type": "status-query"
}
```

```json
{
  "type": "ping"
}
```

建议结构：

```text
Student Agent
├── Command Listener
├── WebsitePolicyHandler
├── UpdateHandler
├── StatusHandler
└── UpdateDownloader
```

---

## 8. 网站策略与更新的区别

两者可以复用：

- 通信通道
- 校区身份
- Teacher 签名
- Campus ID
- 防重放机制
- 消息分发逻辑

但不要复用成“同一种消息”。

### 网站策略

```text
Teacher
→ signed website-policy
→ Student Agent
→ 验签
→ 写 Edge / Chrome policy
```

### 软件更新

Teacher 只通过命令通道发送一个小的更新指令：

```json
{
  "type": "update-command",
  "campusId": "VC-000001",
  "version": "0.5.0",
  "fileName": "VeyonCampus-Student-Setup-0.5.0.exe",
  "sha256": "...",
  "url": "http://teacher-ip:port/update/0.5.0",
  "issuedAt": "...",
  "expiresAt": "...",
  "nonce": "..."
}
```

Student Agent 收到后：

```text
验证 Teacher 签名
→ 校验 campusId
→ 校验 expiresAt
→ 校验 nonce / revision
→ 判断版本是否更新
→ 从 Teacher 的 LAN 文件服务下载 installer
→ 校验 SHA-256
→ 校验开发者发布签名
→ 调用 Updater
```

---

## 9. 更新包不要直接塞进策略消息

网站策略通常只有几 KB。

软件安装包可能几十 MB。

因此应拆成：

```text
控制通道
→ 只传更新命令和元数据

文件通道
→ 真正传安装包
```

Teacher 可以在 LAN 内临时提供：

```text
http://teacher-ip:port/update/student-0.5.0.exe
```

Student Agent 根据签名命令去下载。

---

## 10. 更新的安全模型

网站策略可以使用校区 Teacher 私钥签名。

但软件更新需要比网站策略更严格。

建议双重信任：

### 第一层：Teacher 校区签名

证明：

> “本校区 Teacher 要求这些 Student 安装这个版本。”

### 第二层：Developer Release Signature

证明：

> “这个安装包确实是开发者发布的合法版本。”

完整流程：

```text
Developer Release Key
        ↓
签名 Student Setup 0.5.0
        ↓
Cloud / Gitee
        ↓
Teacher 下载
        ↓
Teacher 使用校区私钥签 update-command
        ↓
Student Agent
        ↓
验证 Teacher 命令
        ↓
验证 Developer Release Signature
        ↓
验证 SHA-256
        ↓
安装
```

即使某个 Teacher 私钥泄露，也不能伪造一个恶意 Student 安装包。

---

## 11. Student Agent 怎么更新自己

不能简单直接覆盖正在运行的 Agent.exe。

建议：

```text
VeyonCampus.Agent.exe
VeyonCampus.Updater.exe
```

流程：

```text
Student Agent 收到更新
→ 下载新版 Installer
→ 验证
→ 启动 Updater
→ Agent 停止
→ Installer 静默升级
→ Agent Service 重新启动
→ 检查版本
→ 返回结果给 Teacher
```

更新 staging 可以使用：

```text
C:\ProgramData\VeyonCampus\Updates\
```

---

## 12. Teacher 如何更新自己

Teacher 自己不需要通过 LAN 更新。

流程：

```text
Teacher App
→ GET /version
→ 发现新版本
→ 下载 Teacher Setup
→ 校验
→ 启动 Installer / Updater
→ Teacher App 退出
→ 静默覆盖安装
→ 自动重新启动
```

用户体验上可以表现为：

```text
[立即更新]

正在更新...
程序自动重新启动
```

---

## 13. Teacher 如何更新所有 Student

推荐：

```text
Cloud
↓
Teacher 下载 Student Installer 一次
↓
Teacher 保存到本地更新缓存
↓
Teacher 向所有 Student Agent 发 update-command
↓
Student Agent 从 Teacher LAN 下载
↓
静默更新
```

优点：

- 一个机房几十台电脑，只消耗一次公网下载
- Student 不需要主动访问公网更新源
- Teacher 可以控制升级时间
- 可以先升级 1 台测试机
- 可以看到每台 Student 的升级状态

Teacher UI 以后可以做：

```text
Student Agent 最新版本：0.5.1

PC-01   0.5.1   ✓
PC-02   0.5.1   ✓
PC-03   0.5.0   待更新
PC-04   0.5.0   更新失败

[先更新测试机]
[更新全部]
```

---

## 14. Teacher 应该能够查看 Student 当前版本

这是需要做的。

Student GUI 不需要为了“看版本”重新打开。

因为 Agent 本身就知道：

- Agent Version
- Student Setup / Installed Version
- Campus ID
- Computer Name
- 当前策略 Revision
- 当前运行状态

Teacher 可以复用同一个 LAN Command Channel：

```json
{
  "type": "status-query"
}
```

Student 返回：

```json
{
  "type": "status-response",
  "campusId": "VC-000001",
  "computerName": "PC-03",
  "agentVersion": "0.5.0",
  "installedVersion": "0.5.0",
  "websitePolicyRevision": 27,
  "status": "ready"
}
```

Teacher 可以在设备列表中直接展示。

这样更新系统就能判断：

```text
PC-01  0.5.1  最新
PC-02  0.5.0  可更新
PC-03  0.4.23 离线 / 未响应
```

不需要学生端 GUI。

---

## 15. Installer：从 ZIP 改为正式安装程序

推荐 Inno Setup。

正式发布物可以是：

```text
VeyonCampus-Teacher-Setup.exe
VeyonCampus-Student-Setup.exe
```

第一版更推荐两个安装包，而不是一个安装器再让用户选 Teacher / Student。

原因：

- 不容易装错角色
- 给学校部署时更直观
- Teacher 和 Student 的内容差异比较大
- 后续仍可以合并成统一安装器

---

## 16. Installer 能做什么

Student Installer：

```text
安装到 Program Files
安装 Student Setup
安装 Agent
安装 Updater
注册 Windows Service
注册卸载入口
配置权限
首次安装完成后启动 Student Wizard
```

Teacher Installer：

```text
安装 Teacher App
安装 Updater
注册卸载入口
创建桌面 / 开始菜单快捷方式
```

Student 不一定需要允许自定义安装目录。

建议固定：

```text
C:\Program Files\VeyonCampus\Student\
```

因为这样：

- Service 路径稳定
- ACL 简单
- 静默更新简单
- 卸载简单
- 排查问题简单

---

## 17. Installer 会不会比 ZIP 更小

不一定。

Installer 的主要价值不是体积，而是：

- 标准 Windows 安装体验
- Program Files
- 开始菜单
- 桌面快捷方式
- Windows Service
- 管理员权限
- 卸载
- 覆盖升级
- 静默更新

Inno Setup 本身也会压缩，所以大小可能接近 ZIP，具体需要实测。

---

## 18. Inno Setup 不等于热更新

Inno Setup 不是“程序运行时无感替换所有文件”的框架。

更新一般还是：

```text
下载新版 Setup.exe
→ 静默执行
→ 旧 App 退出
→ Installer 替换文件
→ 新 App 启动
```

Inno Setup 支持类似：

```text
/VERYSILENT /NORESTART
```

所以可以做到用户几乎看不到安装界面。

但 Teacher.exe 或 Agent.exe 通常仍然需要短暂退出，因为 Windows 不能随意覆盖正在运行的 EXE。

---

## 19. 云端 heartbeat 不应该按每台 Student 上报

原先设计：

```text
3000 Student
→ 每天 3000 heartbeat
```

不适合。

更适合：

```text
每个校区 Teacher
→ 聚合本校区信息
→ 每天一次 heartbeat
```

如果有 54 个校区，就是大约：

```text
54 heartbeat / day
```

而不是 3000。

---

## 20. CloudBase 数据模型应该按校区

核心表可以简单到：

```text
campuses

campus_id
campus_name
configured_computers
teacher_version
student_agent_version
last_seen
```

以后可以扩展版本分布：

```json
{
  "studentVersions": {
    "0.5.1": 42,
    "0.5.0": 8
  }
}
```

---

## 21. 并发 heartbeat 怎么处理

不要做：

```text
读取 total
→ +1
→ 写回
```

并发时会丢数据。

应该让每个 Teacher 直接覆盖自己的校区记录：

```sql
UPDATE campuses
SET configured_computers = 47,
    teacher_version = '0.5.0',
    last_seen = NOW()
WHERE campus_id = 'VC-000023';
```

54 个校区即使同时发送，也只是更新各自行。

管理员后台总数：

```sql
SELECT SUM(configured_computers)
FROM campuses;
```

54 行数据，实时算一次非常便宜。

没必要长期维护一个全局 total。

---

## 22. Admin Dashboard 不需要实时轮询

管理员后台数据不需要每秒更新。

推荐：

```text
打开后台
→ 请求一次 API

之后不自动轮询

用户点击：
[刷新]
→ 再请求一次
```

显示：

```text
最后更新：2026-09-29 18:32

[刷新]
```

Teacher heartbeat 更新数据库。

Admin 页面只在：

- 首次打开
- 手动刷新

时重新请求。

不需要 WebSocket、SSE 或持续 polling。

---

## 23. Admin Dashboard 可以部署在 CloudBase 静态托管

架构：

```text
CloudBase Static Hosting
├── 官网
└── Admin Dashboard

CloudBase Functions
├── /api/version
├── /api/campus-heartbeat
└── /api/admin/*

CloudBase Database
└── 校区记录
```

Admin 前端只是静态 HTML / JS。

敏感操作放云函数。

不要把 Admin API Key 写在前端 JS 里。

---

## 24. 云函数的定位

Cloud Function 可以理解成：

```text
App 发 HTTP 请求
→ CloudBase 启动函数
→ 执行逻辑
→ 查询 / 写数据库
→ 返回结果
→ 函数结束
```

适合：

- `/version`
- `/campus-heartbeat`
- `/admin/*`

不适合为了少量低频请求一直开云托管。

---

## 25. Cloud Hosting vs Static + Functions

### Cloud Hosting

类似常驻服务器 / 容器：

```text
Node Server
24h 在线
```

适合：

- 长连接
- WebSocket
- 持续后台任务
- 实时服务

### Static Hosting + Functions

```text
静态前端
+
按需执行 API
+
数据库
```

适合当前项目，因为：

- API 很低频
- heartbeat 一天一次
- Admin 很少打开
- 版本检查低频
- 没有必须常驻的后端

---

## 26. Cloud API 防滥用原则

如果请求已经进入云函数，即使返回：

```text
401
403
410
```

这次调用仍然已经发生。

因此保护应尽量前置：

```text
网关限流
→ 请求大小限制
→ IP / QPS 限制
→ 函数内部身份验证
→ campusId 校验
→ 签名 / token
→ timestamp / expiry
→ 防重放
```

但任何公网服务都无法保证遭遇恶意分布式攻击时完全零成本。

所以核心原则是：

> 尽量减少公网暴露接口。

---

## 27. Student 部署包不应该经云函数传大文件

如果以后仍保留“Teacher 上传部署包、Student 首次获取”的云端流程：

不要：

```text
Teacher
→ Cloud Function
→ 大文件上传
```

应该：

```text
Teacher
→ 对象存储

Cloud Function
→ 只负责授权 / 生成临时链接

Student
→ 直接对象存储下载
```

但长期方向仍然可以优先考虑 LAN bootstrap，避免学生长期依赖公网。

---

## 28. ViewModel 太大的含义

Avalonia MVVM 中：

```text
View
→ UI

ViewModel
→ UI 状态 + 调用服务

Service / Core
→ 真正执行业务
```

当前 `MainViewModel`、`TeacherViewModel` 已经比较大。

不是说文件大就一定错误。

真正风险是 ViewModel 同时做：

- UI 状态
- Veyon
- 文件
- Windows
- HTTP
- CloudBase
- 更新
- 部署
- 错误处理

后续应该逐步拆成：

```text
TeacherViewModel
├── VeyonDeploymentService
├── WebsitePolicyService
├── CampusTelemetryService
├── UpdateService
└── DeploymentPackageService
```

ViewModel 只负责：

```text
用户点击按钮
→ 调 Service
→ 获取结果
→ 更新 UI
```

但这不是当前发布前最优先的任务。

---

## 29. 首个正式发布版本应该包含什么

最小可发布范围：

### Student

- 清晰的 Setup Wizard
- 环境预检
- Veyon 配置
- Agent 安装
- 网站策略测试
- 标准 Installer
- 标准卸载
- 后台静默 Agent

### Teacher

- Veyon 配置
- 学生列表
- 网站黑白名单
- 查看 Student Agent 当前版本
- 检查 Teacher 更新
- 下载 Student 更新
- LAN 推送 Student 更新
- 查看更新结果

### Cloud

第一版可以只做：

```text
GET /version
POST /campus-heartbeat
```

Admin Dashboard 可以晚一点做。

---

## 30. 推荐发布顺序

```text
1. Student Wizard UX
2. 干净 VM 全流程测试
3. Inno Setup Installer
4. Teacher / Student 版本上报
5. Teacher 自身自动更新
6. Student Agent LAN 静默更新
7. 发布
8. 后续功能全部通过更新系统继续迭代
```

---

## 31. 最终推荐架构

```text
                     CloudBase
                        │
              ┌─────────┴─────────┐
              │                   │
          /version          /campus-heartbeat
              │                   │
              ▼                   ▼
         Teacher App        Campus Database
              │
              │ 下载一次 Student 更新
              ▼
       Local Update Cache
              │
              │ LAN
              ▼
        Student Agent × N
        ├── WebsitePolicyHandler
        ├── UpdateHandler
        ├── StatusHandler
        └── Updater
```

长期运行：

```text
Teacher App
+
Student Agent
```

一次性使用：

```text
Student Setup Wizard
```

---

## 32. 最重要的几个设计原则

1. **不要让 Student GUI 承担长期管理职责。**
2. **不要删除 Student GUI，改为管理员维护工具。**
3. **一个 Agent 监听端口，多种消息类型。**
4. **网站策略和更新复用通信框架，不混成同一种消息。**
5. **大文件走 LAN 文件通道，小命令走控制通道。**
6. **更新必须校验 Teacher 命令签名 + 开发者发布签名 + SHA-256。**
7. **Teacher 应直接看到每台 Student Agent 版本，不需要学生打开 GUI。**
8. **公网 heartbeat 按校区聚合，不按设备逐台上报。**
9. **Admin Dashboard 不需要实时刷新，打开一次 + 手动刷新即可。**
10. **第一版重点是部署体验和更新闭环，不要继续扩大 scope。**

---

## 33. 给开发 Agent 的审阅任务

请重点审阅以下问题：

1. 当前 Student Setup 如何重构成固定页面 4 步 Wizard：
   - 顶部 stepper / 箭头
   - 当前步骤高亮
   - 已完成步骤打勾
   - 主体区域只展示当前步骤
   - 底部固定导航按钮
   - 不再依赖长页面滚动
2. 环境检查如何改成页面内逐项展示，而不是只弹窗提示。
3. 部署执行页如何逐项显示等待中 / 进行中 / 成功 / 失败 / 已跳过。
4. 部署失败后如何安全支持“重新检查”或“重试失败项目”。
5. 完成页如何同时验证 Veyon 状态与 Agent / 网站策略状态，再结束 Wizard。
6. 当前 Student Agent 的 LAN listener 是否适合抽象为通用 Command Dispatcher。
7. 网站策略协议如何最小改造为支持：
   - `website-policy`
   - `update-command`
   - `status-query`
   - `status-response`
8. 是否需要单独 `Updater.exe`，还是使用 Installer 直接完成自更新。
9. Inno Setup 如何分别打包 Teacher / Student。
10. Student Agent Windows Service 的安装、权限、升级和卸载流程。
11. Developer Release Signature 应如何实现。
12. Teacher 如何维护 Student 版本列表和更新状态。
13. 现有 per-device heartbeat 如何迁移为 per-campus heartbeat。
14. 哪些云端功能应暂时删除或延后，避免首版 scope 膨胀。

---

## 34. 当前阶段的产品判断

本地基础功能已有实现和部分用户验收，但正规安装器、服务迁移、更新链路及完整故障恢复仍有实质工作。不能将“接近收尾”视为已达到正式发布条件。

后续按以下依赖推进：

```text
统一源码与文档基线
→ Student 四步 Wizard 与部署可靠性
→ 干净 VM 首轮部署验收
→ 正规安装器、Windows Service 与迁移验收
→ 通用命令与版本查询
→ Teacher 更新
→ Student LAN 更新
→ 小规模试点与正式发布
```

后续所有新增能力都可以依赖自动更新继续迭代，而不需要第一版一次做完。

---

## 35. 当前基线与既有工作指引的取舍

### 35.1 当前事实与证据范围

2026-09-29 只读核对发现：

- `src/VeyonCampus.App/VeyonCampus.App.csproj` 的版本为 **0.4.38**，而 App 文档主入口与进度表仍主要记录 **0.4.33**；开始下一轮实现前须核对中间变更，不能直接沿用旧版本的完成结论。
- StudentSetup 当前仍使用主体纵向 `ScrollViewer`，固定四页 Wizard 是待实施方案。
- 当前 Agent 采用 SYSTEM 计划任务，运行代码仍启动 Student 匿名心跳；Windows Service 和按校区聚合心跳属于迁移目标。
- 历史记录中，一教师一学生基础部署、地点创建、黑白名单及 0.4.31 本机卸载曾由用户确认；这些证据不自动覆盖 0.4.38、新 Wizard、新服务或未来安装器。
- 工作区已有教师端、API、网站和数据库相关未提交修改。下一轮先识别其内容和依赖，保留已有工作，不把它们直接当成经过验收的发布基线。
- 云端部署文档记录 HTTP 云函数与路由尚待部署；这是文档状态，本轮没有查询线上环境。恢复云端工作时应先只读核实现状。

本次文档修订未运行构建、自动测试、GUI 或 Windows 系统验收。

### 35.2 统一后的决策

| 事项 | 原指引或当前实现 | 后续方向与迁移要求 |
| --- | --- | --- |
| Student GUI | 部署验证后可删除便携 GUI | 保留为管理员维护工具；替换“完成并清理”入口及对应验收条目 |
| Agent 生命周期 | SYSTEM 计划任务 | 改为 Windows Service；保留受控迁移及恢复路径，避免新旧进程双重运行 |
| 文件位置 | 经历史 ACL 修复后使用 ProgramData 版本目录 | 程序安装到 Program Files，配置、日志、状态和更新暂存放受保护的 ProgramData；重新验收 ACL |
| 系统操作 | Veyon、改名、创建账户、管理员改密独立 | 继续独立选择；不默认增加改名、改密、自动登录或重启 |
| 完成判定 | 所选操作读回；连接另验 | 完整首次部署需 Veyon 与 Agent/策略验证；独立维护按操作范围验证 |
| 校区配置获取 | 本地文件与 Windows 只读共享 | 先保留并验收现有 SMB/UNC 流程；云配置包分发后置 |
| 心跳 | Student 按安装实例上报 | Teacher 按校区汇总；保持默认关闭、明确启用和最小数据范围 |
| 云端投入 | 已有网站、统计、配置包 API 工作 | 保留现有成果，暂停扩展；优先版本接口，随后校区心跳，后台扩展后置 |

本文更新产品方向；[架构与实现约束](架构与实现约束.md)中的安全执行、私钥隔离、离线部署和失败恢复约束继续有效。旧文档冲突条目应在阶段 0 明确标为已替代，并保留决策原因、影响任务和迁移办法。

---

## 36. 分阶段实施计划

以下阶段是执行顺序，不替代 P0–P12 任务编号。每阶段应在原主表中建立对应子任务，分别记录“代码已实现”“自动检查通过”“Windows 实机验收通过”。不在测试条件未知时承诺发布日期。

| 阶段 | 关联既有任务 | 主要交付物 | 进入下一阶段的门槛 |
| --- | --- | --- | --- |
| 0：统一基线 | P0、P9 | 核对 0.4.38 变更、未提交工作和文档差异；更新架构决策、主任务表及验收条目 | 版本、源码提交或工作区快照、产物及已知问题能对应；不冒用历史测试结果 |
| 1：四步 Wizard | P1、P2、P3 | 固定 stepper、当前页、底部导航；页面内预检；逐项部署状态；完成与维护入口 | 输入变化使旧预检失效；失败、部分完成、待重启和需核对状态明确；不误报成功 |
| 2：部署可靠性与 VM 验收 | P0、P3–P8、P12 | 安全重试、持久化运行记录、必要备份与恢复；干净机和旧故障机验收报告 | 离线部署、公钥读回、Agent 修复、教师连接及浏览器策略通过；中断后能读回并安全继续 |
| 3：安装器与服务迁移 | P3、P9、P12 | Teacher/Student 两个安装包；Agent Windows Service；标准卸载；旧版迁移；管理员维护页 | 新装、升级、修复、重启、卸载及旧任务迁移通过；系统中仅一个有效 Agent |
| 4：命令分发与版本查询 | P7、P11、P12 | 通用命令封装、协议版本、状态查询；教师设备版本与最后响应时间 | 旧策略流程兼容；可区分离线、超时、旧协议、不支持与正常；跨校区及重放被拒绝 |
| 5：Teacher 自更新 | P9、P11 | 版本接口、签名发布清单、受控下载、Updater、安装和重启流程 | 旧版到新版升级通过；篡改、错误角色或架构被拒绝；失败后有可执行恢复路径 |
| 6：Student LAN 更新 | P10、P11、P12 | Teacher 本地缓存与文件服务、签名更新命令、Agent 下载、静默升级与结果查询 | 单台试更新后再分批；断网、重复命令、文件锁定、安装失败不误报成功 |
| 7：最小云端与试点发布 | P0、P8–P11 | 校区心跳迁移、下载页、签名产物、支持矩阵、试点及发布报告 | 核心功能不依赖公网；严重缺陷关闭；新装和跨版本升级均有当前版本证据 |

阶段 5 前完成版本 API 和发布信任链。校区 heartbeat 不阻塞阶段 1–6，可在更新闭环稳定后实施。正式安装器改变了运行路径和生命周期，阶段 3 后必须重新跑部署验收，不能只引用阶段 2 的结果。

### 36.1 下一轮开发切片

第一轮只交付“可验收的 Student Wizard 与维护入口”，建议拆成以下连续变更：

1. 核对版本和未提交修改，统一任务主表、架构决策及旧清理验收条目。
2. 在现有预检、冻结计划和执行协调器之上增加 Wizard 页面状态；ViewModel 负责展示和调用服务。
3. 展示逐项预检及部署结果；输入变化重新检查；执行期间禁止随意返回修改计划，取消只在安全点生效。
4. 将“完成并清理 GUI”改为“完成并退出”；再次打开显示查看状态、修复部署、卸载/清理入口。实际系统修改仍须管理员权限。
5. 完成故障旧机与干净 VM 验收，记录缺陷后再进入 Installer 和 Service 改造。

可先查看的实现入口：`StudentSetupWindow.axaml`、`MainViewModel.cs`、`ReadOnlyPreflight.cs`、`ExecutionCoordinator.cs`、`StudentDeploymentVerification.cs` 和 `WebsitePolicyRuntime.cs`。按真实职责提取服务，避免在本轮整体重写大型 ViewModel。

---

## 37. 实施前必须明确的行为契约

### 37.1 Wizard、独立操作与完成状态

- 四页只展示当前步骤；长日志或检查列表可在局部区域滚动，顶部流程和底部按钮保持固定。
- 预检根据实际选择判断依赖。仅改名、仅账户操作不得强制要求校区包、Veyon 或 Agent。
- 冻结计划应记录所选操作、目标身份和资源摘要；修改输入或资源后重新检查，执行器在第一次修改前再次验证。
- “已跳过”区分用户未选择、后置条件已满足、前置步骤失败。只有已满足且有读回证据时，才可作为依赖成功。
- 安装待重启、部分完成、超时后状态未知单独处理；不强行汇总为成功或普通失败。
- 完整首次部署同时要求本机读回与教师连接/策略验证。Agent 健康响应不等于浏览器限制生效；教师暂不可达时保存待验收状态，下次继续检查。
- `deploymentCompleted` 只是状态摘要，建议同时保存版本、校区身份、所选范围、验证时间和结果依据；管理员重新打开后仍读回系统事实。
- 只允许对已证明可重复的失败步骤重试。管理员改密、未知安装状态及中断后的操作先复核，不能一键盲目重跑。
- 原有受限 Worker、跨进程互斥、必要备份和崩溃恢复要求继续列入 P3；未满足的权限与恢复门槛不能因 UI 完成而关闭。

### 37.2 Installer、Windows Service 与卸载

- Installer 负责程序文件、服务注册、权限和标准卸载；Wizard 负责校区配置与首次部署业务，避免两者重复安装 Agent。
- 服务在未完成可信校区配置时保持未配置状态，不接收有效管理命令；配置完成后才进入可用状态。
- 旧任务迁移要识别归属、保留配置和策略所有权、防重放记录，停止旧 Agent 后切换到新服务，验证失败时提供恢复路径。
- 重新验证程序目录、配置目录、服务控制权限及普通学生账户边界。历史 ACL 故障必须加入回归范围。
- 卸载只删除本工具拥有且未被外部修改的策略和文件；默认保留 Veyon，不隐式删除学生账户或恢复电脑名称。
- 升级与卸载采用不同清理语义：升级保留校区配置、策略归属及必要运行状态，不执行卸载式清空。

### 37.3 命令协议与更新

- 优先复用现有 TCP 39174 控制入口；网站策略、状态查询和更新使用不同消息类型，统一身份、签名、时效、大小限制和分发入口。
- 增加协议版本及能力声明；旧 Agent 不支持更新时明确显示需管理员迁移，不尝试向旧入口发送可执行脚本。
- 状态响应关联请求和设备，设计响应来源认证，不能凭任意 HTTP 成功响应认定设备已升级。
- 策略 revision 与更新命令去重状态按消息用途定义，避免互相消耗序号；重启后继续拒绝重放。
- 文件服务只提供当前授权的版本文件，限制来源、有效期和并发，采用流式传输；控制通道不传安装包。
- 开发者签名清单至少绑定角色、版本、架构、包大小和 SHA-256。算法、信任公钥分发与轮换方式在阶段 5 前固定；同一下载站提供的哈希不能独立证明发布者身份。
- Updater 只接受固定更新操作和经验证的包；暂存目录受 ACL 保护，安装前再次校验，禁止任意路径或自由命令执行。
- 建议采用独立 Updater，负责停止进程、启动 Installer、等待结果和恢复检查；是否提取共享 Worker 根据实际权限边界决定，不预建空项目。
- 默认拒绝降级；失败后保留已验证旧包及修复入口。不能在数据格式不兼容时承诺自动回滚。
- 更新成功以服务重启后的实际版本与健康读回为准；Teacher 本轮离线时本机保存结果，下次查询返回。

---

## 38. 最小云端范围与现有数据迁移

首版云端优先提供版本元数据，随后提供 Teacher 校区 heartbeat。本文 `/version`、`/campus-heartbeat` 为目标逻辑接口名；实施前统一是否采用 `/api` 或版本前缀，并记录现有 `/v1/heartbeat` 的退役策略。

实施要求：

1. Student 新版本停止逐台公网心跳；盘点旧包与旧 Agent 的启用情况，再确定旧端点兼容期和关闭方式，避免遗留客户端持续重试。
2. Teacher 统计保持默认关闭，管理员明确启用。定义一天的时区口径、成功持久化、失败退避和多 Teacher 去重规则。
3. 校区身份使用稳定 ID 和受控凭据绑定。App Campus ID、数据库 campus_id 与 packageId 分别建模，不能只凭校区名称匹配或客户端自报身份授权。
4. 已配置设备数、最近响应设备数、版本分布分别记录；“配置了 150 台”不表示 150 台在线。同校区多 Teacher 上报需要指定权威来源或冲突规则。
5. Teacher 关闭时不承诺仍会每天上报；后台显示最后上报时间及过期状态，不额外引入常驻教师服务来满足统计。
6. 采用按校区受控 upsert；明确请求认证、限流和字段大小，不使用全局总数读后加一。
7. 保留已应用数据库迁移，通过新增迁移演进；历史设备日活与新校区快照分别呈现，不拼接成同一统计口径。
8. 延续现有 CloudBase Auth/RLS 权限边界；管理页面首次加载和手动刷新，不增加持续轮询。服务端密钥不进入前端或安装包。

暂缓：Admin Dashboard 扩展、公开校区包检索/分发、学生直接公网更新、自动发现及 Agent 策略轮询。已有实现先保留和记录，不为收缩首版范围直接删除数据库或线上资源。

---

## 39. 验收与发布门槛

详细场景继续使用[测试验收与发布清单](../../测试验收与发布清单.md)。下一轮先修订其中“删除 GUI”“计划任务”“便携 ZIP”等已被新方案替代的条目，再追加 Service、Updater 和签名更新场景。

建议验收顺序：

1. 纯逻辑与受控失败检查：预检失效、步骤依赖、失败重试、签名、消息类型、版本比较和中断恢复。
2. 干净 Windows 10/11 VM：无 SDK/.NET 环境、离线安装、普通账户、重启及卸载。
3. 一教师一学生：SMB 只读配置获取、Veyon 连接、Edge/Chrome 策略及到期解除。
4. 旧版本迁移：相同/冲突公钥、残留任务、历史 ACL、服务切换、策略外部改写、重复安装及跨版本更新。
5. 小批量试点后扩大到真实机房；150 条配置容量、分批分发能力和实际远控性能分别记录。

每次验收记录源码提交或工作区快照、包版本与 SHA-256、Windows/Veyon/浏览器版本、VM 快照名、步骤、实际结果和未解决问题。当前版本代码、当前产物与当前结果必须对应。

以下情况阻止正式发布：误报成功、错误账户修改、未知状态下重复改密、外部策略误删、权限导致 Agent 不可用、签名校验绕过、升级后无法恢复，以及已声明支持环境中的部署主流程失败。

发布前完成两个角色包的新装与升级、标准卸载、管理员维护、离线可用性、私钥隔离、发布签名、恢复说明和下载页核对。核对第三方再分发材料、版本说明及校验清单。未实际验证的规模和环境写入限制，不扩大宣传范围。

---

## 40. 后续开发的文档维护规则

- [开发路线与任务清单](../../开发路线与任务清单.md)：唯一任务状态主表；保留原编号，新增子任务关联本文阶段。
- [当前进度与下一步](当前进度与下一步.md)：只记录当前版本、最近证据、下一项门槛和阻塞。
- [架构与实现约束](架构与实现约束.md)：记录新旧方案取舍、权限边界、协议与迁移，不把目标模块写成现有实现。
- [开发与部署指南](../../开发与部署指南.md)：随 Installer、Service、维护入口和 Updater 实际交付更新操作步骤。
- 网站和云端文档随各自实现更新，不用桌面测试证明云端已上线，也不用 API 健康响应证明数据库和身份权限已验收。
- 每次变更聚焦一个可验证行为，记录实现、验证和剩余限制；同步相关文档与文件校验值。本文的规划不自动勾选原任务，也不表示已授权执行生产发布或数据库变更。
