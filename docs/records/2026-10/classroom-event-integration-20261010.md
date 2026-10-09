# 双向课堂事件通道：完整接线记录

日期：2026-10-10（Asia/Hong_Kong）

## 本阶段交付

- Teacher 在活动课堂期间自动启动课堂事件服务，按每台学生机的已固定 Agent 身份推送短期签名授权；课堂刷新时续签，下课后清除当前授权和队列。
- Student Agent 独立接收课堂事件授权，并仅向 loopback 暴露求助与事件读取接口。事件经固定 HTTPS 证书指纹连接教师机；该路径不调用网站、应用或系统策略执行器。
- Student Companion 在普通用户会话中自动读取本机 Agent 签名状态。学生界面只提供“需要老师帮助”一个动作，不选择网络、目标电脑或求助原因；断线后自动重连，教师回复与当前求助关联。
- Teacher 桌面和已配对手机网页读取同一有界内存队列。桌面显示待回复数量并可直接回复；手机网页课堂期间自动长轮询并可回复。二维码、证书下载地址及每台学生机的教师 HTTPS 地址均自动选择，不再要求选择网络适配器。
- 过期事件不会阻塞分页游标；下课取消长轮询。学生登录会话切换时清理上一课堂的求助状态。

## 验证

- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1`：0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -p:VeyonCampusRole=TeacherConsole -m:1`：0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release --no-build --no-restore`：完整检查 **65/65** 通过；包含教师桌面求助数量、事件关联、学生求助/回复、重复消息及断线状态检查。
- `node --check src/VeyonCampus.App/MobileWeb/app.js` 和 `node --check src/VeyonCampus.App/MobileWeb/service-worker.js`：通过。
- `git diff --check`：通过。

## 尚待实机验收

本阶段证明构建、协议和 API 合同通过，不等于 Windows Agent 与校园网络已经现场可用。应在 Windows 教师机和至少一台学生机完成以下验证，再用手机浏览器配对：

1. 固定学生 Agent 身份并开始课堂，确认 Teacher 自动启动服务、签发授权，学生 Companion 显示课堂状态和单个求助按钮。
2. 点击求助，确认教师桌面待回复数量与手机网页同时收到正确电脑的消息；从桌面回复一次、从手机回复一次，确认学生收到对应内容且重复回复不会生成第二条。
3. 短暂断开校园 Wi‑Fi/LAN 后恢复，确认状态和事件自动恢复；下课后旧授权和消息不可继续使用。
4. 验证 Windows HTTP.sys/SYSTEM 权限、防火墙/VLAN、手机系统证书信任、默认浏览器和多网卡下自动选址。
5. 至少验证 24 台并发；实测前不承诺 60–70 台容量。

以上实机步骤未在本开发环境完成；当前源码版本也没有因此创建或发布新的 Release。
