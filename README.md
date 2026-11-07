# WakeGlass 1.1

Windows 11 原生智能睡眠与唤醒管理器。C# / .NET 10 / WPF，使用真实的 Windows DWM Acrylic、Mica，直接访问 Windows Task Scheduler COM API 和原生电源 API。没有浏览器组件、遥测或后台常驻服务。

## 直接运行

解压 `WakeGlass-1.1.0-win-x64.zip`，双击 `WakeGlass.exe`。这是包含 .NET Desktop Runtime 的 x64 独立发布版，无需 Visual Studio、SDK、管理员权限或额外运行时。首次启动需要解压内置原生运行库，可能稍慢。系统要求 Windows 11 x64，Windows 11 22H2（22621）及以上支持所用的原生窗口材质接口。透明效果关闭、远程桌面、节电模式或高对比度下，Windows 可能显示纯色背景。

双击 `Install.cmd` 可安装到当前用户的 `%LOCALAPPDATA%\Programs\WakeGlass` 并创建开始菜单快捷方式。升级前请暂停多阶段计划，再从托盘退出正在运行的版本。安装后请从开始菜单启动，并重新保存曾在便携目录创建的计划，以更新任务中的程序路径。发布版未做代码签名。

## 使用

1. **立即睡眠**：选择未来的日期和时间，或使用 30 分钟／1 小时／2 小时快捷按钮，然后点击“睡眠并定时唤醒”。程序检查能力、注册 WakeToRun 任务并回读验证，再显示默认 15 秒的可取消倒计时。注册或验证失败时不会请求睡眠。
2. **每日计划**：输入 24 小时制 `HH:mm`，选择每天、工作日或自定义星期，勾选启用后保存。时间框支持方向键增减分钟。“工作日”指周一至周五，不包含节假日调休规则。重复星期按**睡眠日**计算，例如周五 23:30 的唤醒是周六 07:50。
3. **计划与记录**：选择任务后暂停、恢复或删除。每日睡眠与唤醒作为一个整体管理；重复保存覆盖同名任务，不创建副本。状态页显示下次时间、最后运行时间、系统返回码和执行记录。
4. **系统诊断**：查看 S3、S0、休眠、供电、计时器策略和任务注册权限。诊断使用 `powercfg /a`、`/waketimers`、`/lastwake`；某些命令要求管理员权限时会显示系统原始限制，不自动提权。可导出去除用户名、用户目录、设备名称和 SID 的诊断文本。
5. **托盘**：默认最小化或关闭窗口会隐藏至托盘。双击托盘图标恢复，可使用界面中选好的时间立即睡眠、查看下次唤醒、暂停／恢复每日计划或退出。退出 GUI 不删除系统计划。
6. **外观**：设置内提供跟随系统、深色、浅色，以及 Acrylic、Mica 和纯色模式。倒计时可设为 5–60 秒。

## 多阶段唤醒

在「多阶段唤醒」中添加阶段。每个阶段可分别选择周一至周日、唤醒时间、模式与启用状态。短时阶段支持 1–720 分钟，计划最多保存 48 个阶段。编辑、禁用和删除在保存后生效，列表按唤醒时间排列。初次保存后，点击「启用已保存计划」开始调度。

示例使用两个每天重复的阶段。第一个设为 **07:25 短时唤醒，保持运行 7 分钟**。07:32 到达结束点时，工作进程释放保持运行请求，在核验下次唤醒和安全条件后请求睡眠。第二个设为 **08:25 持续唤醒**，唤醒后保持系统运行，屏幕继续遵循 Windows 的显示超时。单次或每日计划若另有更早的唤醒任务，也可以提前唤醒电脑。

- 每个启用的阶段对应独立的 `Stage-Wake-<ID>` 周计划。保存时按稳定 ID 更新并核验 `WakeToRun`、触发时间、用户和程序路径。停用或删除阶段会删除对应系统任务，暂停整套计划也会清理后台恢复任务。普通注册或写入失败会尝试回滚本次改动。
- `Stage-Worker` 在登录时启动，每分钟检查恢复机会。阶段任务也会请求启动后台。任务使用已登录用户的交互令牌，工作进程可在界面退出后继续运行。恢复任务本身不会唤醒电脑。
- 工作进程在同一线程调用 `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`，在暂停、阶段结束或收到系统睡眠事件时释放。用户可主动睡眠，应用也会尊重原有的明确睡眠操作。
- 自动睡眠要求同一短时阶段保持至结束点，并在结束后 15 秒内完成检查。当前控制台会话最近 2 分钟有键鼠输入、输入状态未知、电源策略阻止唤醒或下一次阶段任务核验失败时，本次睡眠会跳过并记录原因。跳过的自动睡眠不会补执行。
- 重启、重新登录和后台重新启动时，会按实际时间恢复有效阶段。睡眠处理记录保存在 `stage-run.json`，用于防止重复执行。夏令时跳过或重复的本地时间不启动相应阶段。已知的重复时间、短时阶段重叠及与每日计划的冲突会在保存时提示。
- 目标设备的固件、电源策略和 Modern Standby 行为决定真实唤醒与保持运行的结果。S0 电池供电可能限制保持运行请求。远程会话无法确认本机交互空闲时会跳过自动睡眠。

## 可靠性与权限边界

- 任务按当前 Windows 用户注册到独立的 `\WakeGlass-<当前用户 SID>` 文件夹，运行级别为普通用户，登录类型为 InteractiveToken；**关闭 GUI、最小化和锁屏不影响系统调度，但必须保持用户登录。注销后不保证执行**。不保存密码，不创建 SYSTEM 权限服务。
- 每日睡眠任务本身不启用 WakeToRun，也不补执行错过的睡眠。任务执行时启动独立进程，检查配置仍启用、当前处于计划时间窗口、成对任务仍匹配，且系统报告的下次唤醒与预期一致；倒计时后再检查一次。所有检查通过后才请求睡眠。
- 唤醒任务启用 `WakeToRun=true`，允许电池执行。程序不更改电源计划；如果当前供电方式禁用了普通唤醒计时器、仅允许重要计时器、权限受限或低电量，会阻止即刻睡眠并解释原因。每日计划可以预先保存，但每次自动睡眠前仍检查当前条件。
- 睡眠调用 `SetSuspendState(false, false, false)`，保留唤醒事件，不强制关闭应用，不请求关机；只在调用期间启用当前用户已有的 SeShutdownPrivilege，然后恢复原有令牌状态。
- 成对更新有回滚；先禁用旧睡眠任务，再写唤醒任务、睡眠任务、验证、保存配置。删除先暂停睡眠，避免留下启用的孤立睡眠任务。操作使用用户会话内命名互斥锁，配置原子替换。
- 一次性取消或睡眠 API 失败会清理本次唤醒任务。一次只保留一个即刻唤醒任务；安排新的一次性睡眠会替换旧的一次性任务。正常唤醒动作执行后会清理到期的一次性任务。
- 程序移动位置后需重新保存计划。计划定义会验证执行路径；路径失配会阻止自动睡眠。系统重启后计划仍存在；重新登录后才能继续执行交互式任务。时区更改后建议重新保存计划；夏令时无效或歧义唤醒时间会被拒绝，系统下次时间不一致时跳过睡眠。
- 日志只保存应用操作、时间及电源诊断，位于 `%LOCALAPPDATA%\WakeGlass`；单文件上限约 2 MB，保留一份轮转文件。无网络上传。

## 硬件兼容性：请先阅读

**注册成功、动作返回 0、收到恢复事件，都不能单独证明电脑被某个计时器成功唤醒。** WakeGlass 如实展示这些不同证据，不把创建任务描述为硬件测试成功。

S3 唤醒受 BIOS/UEFI、RTC 和电源策略影响；S0 Modern Standby 由 Windows 和固件共同调度，普通定时任务在部分设备上可能无法按时唤醒。电池、节电、临界低电量自动休眠、混合睡眠、设备驱动或组织策略可能改变结果。休眠能力只做检测，本版按钮请求睡眠；系统之后自动转入休眠时的唤醒仍须实测。屏幕可能保持关闭，即使系统已恢复运行。**完全关机后的自动开机不属于本版功能。**

在本次开发电脑上：Windows 11 x64，版本 10.0.26300；S0 支持、S3 不支持、休眠支持；插电允许计时器，电池禁用。未修改这些设置，未进行真实系统睡眠。

实际验证时，请保存工作并接电，在“系统诊断”点击“真实唤醒测试…”。只有你在明确提示中选择“是”，才会安排约 3 分钟后的唤醒并开始可取消倒计时。若未自动恢复，请手动按电源键，再查看诊断。建议先完成此测试，再启用无人值守的每日计划。

## 卸载与清理

先从托盘退出。双击 `Uninstall.cmd`（也可从开始菜单进入）：选择“是”仅清理当前用户 WakeGlass 创建的任务，并移除固定安装目录和快捷方式；选择“否”保留任务及已安装程序，以免留下失效的任务路径；“取消”不执行。便携版清理任务后可删除解压目录。设置和诊断记录保留供排错，可自行删除 `%LOCALAPPDATA%\WakeGlass`。清理工具不会访问其他应用的任务或修改电源设置。

## 源代码与构建

`WakeGlass.sln` 包含：

- `src/WakeGlass.App`：窗口、主题、原生材质、托盘、后台任务入口、系统电源事件。
- `src/WakeGlass.Core`：PowerService、WakeScheduler、ScheduleManager、SystemCapabilityService、SettingsStore、Diagnostics、安全编排与时间计算。构造函数注入接口，异步执行系统检查与诊断，GUI 不负责计时调度。
- `tests/WakeGlass.Tests`：无需第三方测试框架的独立断言运行器，失败时返回非零退出码，输出 `TestResults/results.txt`。
- `tools`：当前用户安装和卸载脚本。

双击源目录的 `Build.cmd` 会自动下载项目专用 .NET SDK 10.0.403 到 `.tools`，构建、运行模拟单元测试、发布并压缩。不会安装系统 SDK，不要求 Visual Studio。复现系统集成测试的命令由维护人员运行：

```powershell
.\.tools\dotnet\dotnet.exe run --project tests/WakeGlass.Tests -c Release -- --integration "$PWD\artifacts\WakeGlass-1.1.0-win-x64\WakeGlass.exe"
```

集成测试使用临时系统任务并在结束时清理。多阶段集成用例要求当前没有已启用的阶段或既有阶段任务。它在内存中保存测试计划，以此验证真实注册和后台启动。电源时序用例使用模拟时钟和模拟电源。开发 UI 可使用 `WakeGlass.exe --simulate`，模拟模式只允许保存暂停的每日与多阶段计划。

选用 WPF 的原因：初始环境没有任何 .NET SDK、Windows SDK 或 Visual Studio / C++ 构建链。WPF 可使用项目本地 SDK 稳定构建和自包含发布，并通过官方 DWM API 使用 Windows 11 真正的 Acrylic / Mica。未采用 WinUI 3 / Windows App SDK，也不依赖其额外部署链。

## 原生 API 依据

- [SetSuspendState 与权限、唤醒标志](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-setsuspendstate)
- [SetThreadExecutionState 与系统空闲睡眠](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate)
- [GetLastInputInfo 与当前会话输入](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo)
- [Task Scheduler WakeToRun](https://learn.microsoft.com/en-us/windows/win32/taskschd/tasksettings-waketorun)
- [InteractiveToken 登录限制](https://learn.microsoft.com/en-us/windows/win32/taskschd/principal-logontype)
- [SYSTEM_POWER_CAPABILITIES](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_power_capabilities)
- [Windows 11 DWM 原生材质](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)

测试明细和未验证范围见 `TEST-REPORT.md`。
