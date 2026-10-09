# Student Companion：实施任务

- [x] 1. 建立独立、普通权限的 Student Companion 项目和托盘窗口
  - `asInvoker`、单实例、窗口收起/托盘打开/退出；UI 使用单张状态卡。
  - _需求：1、2、4、5、8_
- [x] 2. 将 Companion 纳入学生角色安装包与登录启动
  - StudentSetup 发布独立自包含子目录；安装器创建开始菜单/All Users 启动快捷方式，卸载清理。
  - TeacherConsole 发布物必须排除 Companion。
  - _需求：1、3、7_
- [x] 3. 增加状态模型检查、项目隔离验证、文档与实现记录
  - 缺少状态来源时明确显示未连接；不访问网络/云或 SYSTEM 状态。
  - 依开发指南构建并记录 Windows 实机边界；单独提交此里程碑。
  - _需求：4–8_
- [ ] 4. 实现 S1-03 Teacher LAN 状态通道及认证客户端，使 Companion 能显示真实当前课堂状态。
  - _Requirements: 4, 6; separate follow-up feature_
- [ ] 5. Windows 实机验收登录启动、托盘、权限、升级覆盖和卸载清理。
