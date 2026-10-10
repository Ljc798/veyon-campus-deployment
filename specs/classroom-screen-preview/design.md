# 课堂屏幕预览 PoC：设计

## 方案

- 复用 Veyon 4.11.2 的 `veyon-cli feature start <host> Screenshot`。上游 `ScreenshotFeaturePlugin` 会通过已连接的 `ComputerControlInterface` 读取 framebuffer 并写入教师当前用户的 Veyon 截图目录；不依赖 `remoteaccess view` 的图像输出，也不启动 WebAPI 监听。
- Teacher mobile service 只在认证后接受当前 session 的目标地址；目标必须同时出现在服务端活动课堂集合中。浏览器不能提交新的设备清单或 session 身份。
- 服务端最多从活动目标中选择 5 台，并以受限并发运行 CLI。输出只在本机短暂落盘，按 Veyon 文件名中的本次目标与时间定位，读取后缩放到最多 320px 宽，并删除精确匹配的原文件。
- 手机调用独立图片路由，带现有配对 Bearer token 和 60 秒活动租约。服务端不缓存到磁盘；每设备限频、全局限并发、session 结束取消；图片响应禁止缓存。停止按钮撤销租约。
- MobileWeb 提供一个显式“屏幕巡视 / 停止巡视”入口。概览用项目现有墨蓝、青绿和浅灰品牌色，按现有中文字体排版；缩略图使用座位号作为标题，缺少座位映射时显示目标地址。只对当前视口内缩略图轮询，并在页面隐藏或关闭巡视时 abort fetch、撤销 object URL。

## 数据流

```text
已配对手机 ── HTTPS + Bearer ──> Teacher Mobile API
    │                               │ 校验活动 Session 和目标
    │                               └──> veyon-cli feature start ... Screenshot
    │                                         │ Veyon 已配置的认证通道
    │                                         v
    │                                      Student Veyon
    │                                         │ 单帧 framebuffer
    │                                         v
    └── no-store PNG <── 缩放后内存 <── 本机临时截图文件（立即删除）
```

## 数据边界

- 图片本身不进 JSON 审计文件，不同步到云端，不写入应用持久缓存。
- 审计仅记配对设备、操作名、目标和结果；不记画面内容。
- 内存结果按 session/目标隔离；session 更换时清空。
- 取消过程必须终止对应 CLI 子进程；等待并发槽位的请求收到取消后不得再启动进程。

## 失败处理

- Veyon 未安装、目标离线、权限拒绝、命令失败或截图文件无法唯一定位：返回简短错误，手机保留状态文本。
- 不接受非本堂课目标、超出 5 台上限或越界主机名。
- 若发现匹配截图文件在本次请求开始前已经存在，或采集后出现多个可能文件，失败关闭且不删已有文件。
- `Bitmap.DecodeToWidth` 按比例缩放，验证图片头与最大输入大小，再生成 PNG；最终响应有大小上限。

## 实机待核验

- Windows Veyon 4.11.2 的 CLI 实际命令、截图目录及文件命名是否与上游源码一致。
- 单台/5 台采集的首帧等待时间、Teacher 内存/CPU 峰值与 LAN 流量。
- 安卓默认浏览器、iOS Safari 在锁屏、切后台、恢复前台时对轮询和 object URL 的实际处理。
