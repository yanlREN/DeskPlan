# 架构与数据约定

## 分层和窗口生命周期

`DeskPlan.Core` 与 UI 无关，负责 0–100 进度规则、日期范围、重复规则、统计与提醒判定。

`PlannerStore` 持有一个由 WPF 主线程使用的 SQLite 连接。写入使用参数化 SQL；业务修改通过 `PlannerService` 在事务中保存实体与日志。数据库启用外键、WAL、FULL synchronous 和 busy timeout。

`AppController` 持有数据库、Service、共享 `MainViewModel`、托盘和调度器。切换模式时先提交待保存进度和窗口设置。0.1.2 起，悬浮与已唤出的桌面模式都是顶层窗口，切换时复用当前 Window / HWND，只更新宿主、置顶与任务栏样式并激活；不会在按钮 Click 中关闭当前窗口，也不会立即藏进壁纸层。从真正嵌入的桌面窗口切出、明确“放回桌面”或恢复失效宿主时才重建 HWND，绑定相同 ViewModel 并恢复滚动位置。提醒调度器和托盘不随组件重建，不产生多个提醒进程。同一数据目录只允许一个主实例，第二次启动通过本机事件唤出原实例。

`DesktopWindowHost` 请求 Explorer 建立壁纸宿主，定位 `WorkerW`，转换窗口样式为 `WS_CHILD` 并验证 `SetParent` 后的父窗口。`FloatingWindowHost` 使用 `WS_EX_TOOLWINDOW` 和实际 topmost 标志；安全回退使用独立普通非置顶 HWND。桌面宿主失效时定期检查，至多自动重试 3 次，然后保持可访问的回退窗口。用户主动点击“放回桌面”可以重新尝试嵌入。

0.1.1 增加 `RevealedWindowHost`：用户双击 EXE 或主动从托盘打开时，把桌面子窗口重建为可独立激活的普通窗口，临时提供任务栏入口，且不修改保存的模式；“放回桌面”重新建立桌面宿主。开机自启与后台恢复不会使用该主动唤出行为。第二进程通过仅在进程存活期间存在的共享内存取得主进程 PID，调用 `AllowSetForegroundWindow` 后发送单实例事件；启动交接使用有限重试，避免忽略快速连续双击。

窗口位置保存为虚拟屏幕物理像素，尺寸保存为 WPF DIP；附着时转换父窗口坐标，按目标工作区约束位置。收到屏幕 / DPI 消息后重新约束。标题拖动和缩放使用宿主坐标更新，不依赖 child HWND 上的系统 DragMove。

0.1.3 使用四边、四角共八个 Thumb 缩放区域。手势起点与最终位置取自 WPF 拖动事件，转换为屏幕物理像素；整个手势保持初始矩形不变，避免左 / 上边移动时出现相对坐标反馈。纯数学 `WindowResize` 负责对边固定、最小尺寸与工作区限制，窗口层转换回 DIP 后调用宿主放置逻辑；松手时补齐最终位移并保存。

## 数据模型

SQLite 数据库 schema version 为 1。实体主体存储为 JSON，同时把日期、ID、定义关联、进度和删除标志保存在有约束的列中，用于检索与索引。

| 表 | 约束与含义 |
| --- | --- |
| TaskDefinitions | 重复模板；开始日期、结束边界、时间、提醒、重复规则 |
| TaskOccurrences | 独立快照；`(definition_id, original_date)` 唯一；progress 有 0–100 CHECK |
| ProgressEvents | 已提交进度变更；指向实例的外键；保留完成、撤销等历史 |
| AuditEvents | 元信息、删除、排序、总结、恢复事件及前后值 |
| ReviewNotes | `(period, period_start)` 唯一，周一 / 月首归一化 |
| AppSettings | 单例设置；模式、背景透明度、主题、位置、尺寸、排序等 |

`OriginalDate` 是重复实例身份，`ScheduledDate` 是实际安排日期。改期后保留原身份，避免再次访问旧日期时生成同一实例。删除保留 tombstone，同样防止重复生成。

`IsCompleted` 始终从 `Progress == 100` 推导。完成时间只在首次到达 100% 时设定，维持 100% 不重置；重新打开任务清除当前完成时间，已有事件仍保留。界面进度立即反映，停止变化 400 ms 后提交；鼠标松开、离开控件、切换日期 / 模式、导出与退出时强制提交。

重复实例按查询范围惰性生成，启动和跨天补齐模板开始以来到今天的实例。所有重复日期保持独立。修改“本次及以后”会关闭旧模板区间，创建新模板；按日期保留已有实例 ID 和进度。先前单独编辑的实例保留快照；被新规则排除的未来日期记录软删除。过去发生的实例不会被系列修改覆盖。

所有事件时间使用 `DateTimeOffset`，写入 UTC，展示转为本机时区。计划日期用 `DateOnly`，计划钟点用 `TimeOnly`；提醒判定时用本机时区转换，处理夏令时不存在的钟点。

## 提醒策略

Windows Shell `NotifyIcon.ShowBalloonTip` 提供本地系统通知，无网络请求。每 15 秒最多派发一条，避免同时涌出。去重键为计划提醒时刻或具体 snooze 时刻；派发前持久化键，关闭重启或时钟回拨不重复派发。睡眠 / 关闭 / 时钟跳变后仅补发两小时窗口内仍未完成的任务。

使用托盘通知意味着点击处理的是最近的一条通知，不支持关闭进程后由通知中心按任意旧通知独立激活任务。该限制和 Windows 通知权限交由用户明确了解，详见 known-issues.md。

## 备份与迁移

程序打开高于当前 schema 的数据库时拒绝写入；执行迁移前使用 SQLite 在线备份 API。恢复 JSON 前验证显式字段、版本、ID 唯一性、引用、重复实例身份、日期、完成状态和设置。替换前创建 `.db` 安全副本，替换在单个事务中完成。

JSON 导出先写临时文件并 flush，再原子替换目标。CSV 为 UTF-8 BOM，正确转义逗号 / 换行 / 双引号，前导公式符号加文本前缀。

## 参考

- [.NET Windows 安装](https://learn.microsoft.com/dotnet/core/install/windows)
- [Microsoft.Data.Sqlite 事务](https://learn.microsoft.com/dotnet/standard/data/sqlite/transactions)
- [Microsoft.Data.Sqlite 在线备份](https://learn.microsoft.com/dotnet/standard/data/sqlite/backup)
- [NotifyIcon.ShowBalloonTip](https://learn.microsoft.com/dotnet/api/system.windows.forms.notifyicon.showballoontip)

`WorkerW` 嵌入不是有兼容承诺的公开桌面组件 API；宿主适配层可以后续替换。
