# 手机画面页预览

截图使用项目当前的 PWA HTML、JavaScript 和 CSS，通过隔离 Chrome 手机视口生成：390×844 和 320×740 CSS px。会话、课堂名单和画面读取器由本地脚本注入；屏幕图像由 Canvas 合成，不含真实学生信息，也不代表真实教师 API、手机或 Veyon 采集结果。策略预设截图使用本地 mock API。

2026-10-28 手机主界面壳层预览：使用当前 PWA HTML/CSS、显式 Chrome 设备视口模拟和静态课堂内容，检查连接状态常驻、四个导航入口和窄屏布局。390×844 与 320×740 CSS px 下，`documentElement.scrollWidth` 均等于视口宽度。截图不连接教师 API，连接状态和课堂内容为示意。

![390×844 手机主界面与连接状态预览](mobile-control-shell-v54-390.png)

![320×740 窄屏手机主界面预览](mobile-control-shell-v54-320.png)

2026-10-27 增量：当前截图展示离屏卡片保留并变暗的最近画面，旁边卡片显示新帧；空卡片显示“暂无画面”占位。隔离 Chrome 在 390×844、320×740 CSS px 注入合成课堂状态和 Canvas 画面，检查了离屏保留、回到卡片后再次读取、长姓名换行和文档宽度等于视口宽度。模拟读取过程中没有浏览器错误；另以九个 1 MiB 合成缓存项验证回收后用量回到 8 MiB，并优先释放最早的离屏帧。预览不验证真实配对认证、教师 API、学生 Agent、校园网或真实屏幕采集。

![390px 手机视口预览](mobile-monitor-390.png)

![320px 窄屏及长姓名预览](mobile-monitor-320.png)

两种视口下文档宽度均与视口宽度一致；预览包含 4 张双列 16:9 画面卡片。窄屏姓名最多显示两行，更新时间另起一行；完整姓名仍保留在元素文本和 `title` 中。真实 Android/iOS 浏览器、证书信任、教师 API 和学生屏幕仍需现场验收。

2026-10-27 新增首次读取状态预览：隔离 Chrome 使用 320×740 CSS px 视口、合成课堂会话和 mock 画面 API。截屏时两张当前可见卡片显示“正在读取画面…”及骨架光带；视口宽度与文档宽度均为 320，`prefers-reduced-motion: reduce` 下光带动画关闭。随后 mock API 返回合成 JPEG，两张卡片均显示已加载状态，加载光带已清除。截图不含真实学生画面，也不验证教师 API 或 Veyon 采集。

![320px 手机首次读取画面状态](mobile-monitor-loading-320.png)

2026-10-27 新增的策略预设卡片另用 iPhone 15（iOS 17.5）Simulator Safari 检查，CSS 视口为 393×852；教师会话、策略预设和签名状态均由本地 mock API 提供。预览确认预设摘要、可发送状态和两个操作按钮在该视口内可读且没有明显横向溢出；它不证明真实教师 API、学生 Agent 策略交付或实体手机证书信任。

![iPhone 15 Simulator Safari 中的手机策略预设卡片](mobile-policy-presets-iphone15-393.png)

2026-10-27 继续在同一 iPhone 15 Simulator Safari 会话中检查 320px 窄屏：外层 Safari 视口为 393×852 CSS px，预览 wrapper 把实际 PWA 放入 320×852 CSS px 的 iframe，接口仍由本地 mock API 提供。预设选择器在窄屏下占满一行，刷新按钮移到下一行；截图中预设名称和按钮均完整可见，卡片没有明显横向裁切。该模拟不代表实体手机、真实教师 API 或策略下发结果。

![320px 宽度下的手机策略预设卡片](mobile-policy-presets-320.png)
