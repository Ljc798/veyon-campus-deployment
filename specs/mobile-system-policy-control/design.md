# 设计：手机端长期系统策略控制

## 模块边界

- `MobilePolicyProfile` 增加 `System` 类型并复用 `StudentSystemPolicySettings` 与 `StudentSids`。长期策略固定 `LifetimeMinutes=0`；现有网站/应用预设格式保持可读取。
- 桌面教师 ViewModel 从现有六项系统限制开关及学生 SID 输入保存系统预设。软件安装限制未勾选桌面影响确认时拒绝保存。
- 手机状态 API 将学生 Agent 已签名的 `SystemPolicy` 状态原样映射到移动响应；页面逐项呈现策略版本、未配置、待复核和未知。
- 手机 `/api/policy` 在 `System` 预设分支中使用 `StudentSystemPolicySigningKeyStore`、`StudentSystemPolicyRevisionStore` 和 `StudentSystemPolicyTransport`。签名私钥继续留在教师 Windows 用户证书库，手机只提交预设 ID 与目标。
- 移动页面对系统策略显示无自动到期的明确提示，确认弹窗列出具体设置；解除解释为恢复学生机原有值。现有身份固定、LAN、成对设备、nonce、防重放、目标目录和本机审计逻辑保持统一。

## 安全与错误处理

- 不增加新的学生机命令格式或信任密钥；复用学生端独立的长期系统策略签名协议、单调版本和签名回执。
- 状态为不支持、无策略、未连接或需复核时分别显示，不推断策略效果。
- 移动端不接受系统策略正文、SID 列表或公钥；所有操作通过已保存且校区匹配的预设进行。
- 启用/解除都需要手机端确认；系统策略没有到期时间，必须另行解除。

## 验证

- .NET 合同检查覆盖系统预设验证、保存/读取、有效与解除策略编译、六项状态校验和 API 校区隔离。
- 运行完整 Release 解决方案构建和可移植检查；构建移动 Web 静态资源。
- Windows 实机的注册表/LSA/SAM/权限读回、浏览器/系统实际效果、TLS/手机网络操作继续列为现场验收，不由本地合同检查代替。
