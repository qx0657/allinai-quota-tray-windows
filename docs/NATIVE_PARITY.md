# 原生版本同步维护

2026-10-20：决定继续分别维护 macOS / Windows 原生仓库。Windows 0.2.0 的本轮行为基线是 macOS `79e417d`，不引入跨平台 UI 或共享运行库。

macOS 仓库：[allinai-quota-menu](https://github.com/qx0657/allinai-quota-menu)，[对应维护说明](https://github.com/qx0657/allinai-quota-menu/blob/main/docs/NATIVE_PARITY.md)。Windows 侧功能同步完成于 `8c759ba`；上述提交标识为行为基线，不是后续发布提交。

## 功能对应

| 行为 | Windows 实现 | macOS 对应 |
| --- | --- | --- |
| 有效订阅、最受限周期、余额、未知状态 | `Core/Models.cs` 的 `QuotaPolicy` | `QuotaPolicy` |
| 独立资源快照、最后成功时间 | `AccountSnapshot` | `AccountSnapshot` |
| 验证后保存、认证恢复、并发续期 | `Core/Client.cs` 的 `AllinClient` | `AllinClient` actor |
| 调度、限流、退避、睡眠和退出隔离 | `Core/QuotaStore.cs` | `QuotaStore` |
| 初次观察静默、阈值与通知去重 | `ReminderManager` | `ReminderManager` |
| 手动 / 可选每日更新、限流与版本比较 | `Core/Updates.cs` | `ReleaseClient` / `UpdateChecker` |
| 只读金额区和错误说明 | `SummaryContent` / `SummaryPanel` | `SummaryContent` / `SummaryView` |
| 验证完成后保存的异步登录表单 | `LoginForm` | `LoginPanelController` |

## 保留的平台差异

- Windows 使用 WinForms / Credential Manager / 当前用户 HKCU Run；macOS 使用 AppKit / Keychain / LaunchAgent。
- Windows 托盘只显示图标，数值放在 tooltip 与概览；不实现 macOS 菜单栏图标旁的数值开关。
- Windows 使用托盘通知并排队投递；实际展示由系统决定。macOS 使用其原生通知权限与投递能力。
- Windows 图标保留黑白轮廓，macOS 使用系统模板图标。
- 两端分别读取本仓库的 GitHub Releases，版本与产物独立发布。

## 后续同步检查

调整业务规则时，先写清相同输入的预期结果，再对照另一个仓库是否需要相同修改。至少比较：

- 每日剩余 80%、每周剩余 10% 时选每周；未知用量不能补为零。
- 过期订阅不能遮住余额；有效订阅耗尽不能自动改为余额消费。
- 一项接口失败时另一项的成功数据和时间保留；非认证错误不反复登录。
- 401 仅重试拒绝的资源；并发续期只有一个请求；退出后不能回写会话。
- 429 的冷却约束覆盖手动、定时、网络恢复和唤醒刷新。
- 提醒默认关闭，初次观察静默，按账号、订阅和周期去重。
- 更新默认手动，自动检查每日一次，失败计入频率，冷却跨重启保留，更新不携带账户凭据。

Windows 业务用例放在 `Tests/CoreTests.cs`；macOS 用例在其 `Tests/AllinAIQuotaTests.swift`。测试用同一类行为场景，各自直接调用生产逻辑，不另造两套“测试专用实现”。

提交说明中记录另一平台是否需要同样修改；已同步时引用对方提交，平台差异说明原因。两端分别执行原生验证并按各自[发布指南](RELEASING.md)发布，不要求构建号或发布日期强制绑定。

服务对缺失限额、周期窗口、订阅耗尽后扣费的正式约定仍有待确认。客户端继续明确表示未知，不以两端一致为理由自行决定后端语义。
