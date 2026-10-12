# 课堂即时测验：实施任务

- [x] 1a. 定义独立 question/answer DTO 与 purpose、Session/Quiz 绑定、教师/Agent 固定公钥 RSA-PSS 签名验证、大小/有效期上限；答案 payload 不含目标标识。
- [ ] 1b. 将消息契约接入 Teacher Console、Student Agent、Student Companion 与只读手机 API，并保持 Agent grant 中的目标绑定为唯一身份来源；教师服务端与只读手机 API 已有首段实现。
- [x] 2a. 实现教师进程内存状态机：问题/选项校验、目标授权集合、幂等提交、改选计数、关闭/揭晓、取消/过期及 Session 清理；不写持久化文件。
- [x] 2b. 将状态机接入教师服务 API、签名协议和活动 Session/目标集合变化清理；手机读取只暴露教师汇总 DTO。
- [ ] 3. 实现 Student Agent/Companion 的当前题目读取、签名提交、幂等重试和断线待确认状态。
- [ ] 4. 在 Teacher Console 活动课堂卡片加入题目编辑、发布、关闭、揭晓及汇总结果。
- [ ] 5. 在已配对手机页面只读显示活动状态和已揭晓汇总；禁止返回目标级答案。
- [ ] 6. 添加协议/状态检查，覆盖未知字段、越权目标、跨 Session、重复/改选、过期消息、并发上限、重启和隐私边界。
- [ ] 7. 在 Windows 教师/学生设备和真实手机上验收证书、LAN、防火墙、断网重连和 10/24 台并发；记录现场环境及结果。
