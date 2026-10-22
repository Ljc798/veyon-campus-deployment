# 座位电脑编号续排规则说明

日期：2026-10-22  
版本：TeacherConsole / StudentSetup 1.1.18  
状态：编号分配和 Veyon 读取代码已接通；姓名显示更新包已重建，Windows Veyon 现场验收待统一进行。

## 规则与实现

- 从 Veyon 读取已有地点时，教师端读取地点内的电脑名称和连接地址，并在名称、已有学生姓名及座位编号可唯一对应时保存本机关联；无法唯一匹配的条目保留待处理。
- 新建空地点或只添加空桌位时不自动分配电脑编号。填写学生姓名且没有手动编号时，才为该学生座位分配新编号。
- 新号从教师本机已登记地点及座位图中的最大编号之后续排；切换地点、增删其他座位不会重排已有编号。已有绑定 Veyon 电脑的座位编号仍锁定。
- 未登记到教师本机地点档案的 Veyon 设备不会被无提示扫描来猜测编号；先读取/同步对应 Veyon 地点，再从已登记最大编号续排。
- 座位配置页的说明现在写明续排范围；教师日常设备列表仍使用学生姓名展示，电脑编号只用于地点/座位配置和设备身份关联。

主要实现位于 `TeacherViewModel.SaveSeatCell`、`GetFirstComputerNumberAfterCampusMaximum`、`ReadSeatVeyonTargetsAsync` 和 `ClassroomSeatGridStore`。本次只澄清界面说明并把验收场景补入[统一现场验收清单](../../下一阶段统一现场验收清单.md)，没有改变编号生成算法或 Veyon 数据。

## 统一测试包

- 教师端：`artifacts/test-build-phase7i-student-name-20261022/VeyonCampus-TeacherConsole-1.1.18-Student-Name-Test.exe`，69,335,551 bytes，SHA-256 `906B5F6E6E1C81392783D54A940B6E2D8B3CD01ACBA411E49036359D494F4220`。
- 学生端：`artifacts/test-build-phase7i-student-name-20261022/VeyonCampus-StudentSetup-1.1.18-Student-Name-Test.exe`，66,948,852 bytes，SHA-256 `E512413760FC70EDE5E09F9DE2D19975E8157829C7DF31778BBD592E1CAE6047`。
- TeacherConsole Release 编译 0 错误，2 条既有 `TextBox.Watermark` 过时警告；StudentSetup Release 编译 0 错误，1 条 Companion XAML runtime-loader 警告。内嵌 Veyon 和发行公钥安全检查通过。
- 缺少 Developer Release PEM 公钥，应用内自更新安全停用。未运行自动测试套件、未启动/安装应用、未操作 Veyon 真实地点、也未进行 Windows/手机现场验收。

## 后续现场核对

1. 在已有 Veyon 地点读取电脑名称与地址，并核对现有姓名/编号座位能唯一匹配；歧义项不应被自动绑定。
2. 新建地点后先添加空桌位，确认没有编号；填姓名后确认新号从已登记地点的最大编号之后开始。
3. 再添加多名学生、切换地点、移动或删除其他桌位，确认既有编号稳定不变；已有 Veyon 绑定编号不能被直接改写。
4. 在教师日常健康、策略、更新、课堂和手机列表中检查显示学生姓名；编号只出现在地点/座位配置界面。
