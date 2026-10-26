# Phase 4：低电量提醒与设置

日期：2026-10-23。用户要求继续计划下一环节，实机验证等本人回来后做。本轮集中实现低电量提醒、配置迁移与托盘接线，用假数据/假通知器验证；不读取真实设备，不向Windows发送测试通知，不进行Computer Use或资源长测。最终同构建结果及候选路径见[开发日志](../DEVELOPMENT_LOG.md)。

## 功能与触发规则

- 设置增加“低电量提醒”，默认关闭；全局整数阈值1–50%，默认20%。保存成功后整体应用，取消/关闭不改变状态，保存失败继续原设置。
- 仅有效且在当前身份/连接上下文中的设备直读百分比可参与：ATK生产成功值，或未来接入正常托盘的标准GATT当前值。Windows系统缓存、历史/过期、未知、离线、档位及Mock不触发；合法0%仍是百分比。
- 每个电池分量先建立基线：启动、首次读数、启用、修改阈值、身份代次改变及系统暂停/恢复后，不因已有低值立即弹窗。有效值从高于阈值下降到阈值及以下才提醒一次。
- 提醒后需新读数回升至“阈值+5”或以上才重新武装；20%阈值下20/21反复不重复提醒。连续低值、普通刷新、列表开关、相同/旧观测均不重复。
- 新查询的暂时无值、失败、离线或过期不产生通知，也不清除已提醒状态。下次新的有效读数仍可参与原阈值判断；没有把保留的比较状态当成当前电量显示。
- 同一设备多个分量同轮跨阈值合并为一个最低值条目，同一快照的多个设备合并为一条通知请求。追踪状态最多256分量，移除设备会清理，不保存设备身份或电量到配置。

只有状态变化及已有快照/ATK结果事件驱动判断，不添加电量读取、定时扫描、通知重试队列或新的后台线程。配置/读取摘要不触发设备通信。自动ATK仍默认关闭，自动GATT仍未启用；提醒开关不授予设备查询权限。

## Windows 通知与限制

复用已有NotifyIcon，先调用SHQueryUserNotificationState，仅HRESULT为S_OK且状态为QUNS_ACCEPTS_NOTIFICATIONS（5）时请求ShowBalloonTip。锁定、忙碌、全屏、演示、Quiet Time、未知状态或API错误时跳过。实际是否出现、声音和持续时间仍由Windows控制；没有修改系统通知、勿扰/专注或游戏设置，没有提权/注册额外服务。

被抑制或发送失败的阈值事件视为已消费，不在用户返回或退出全屏后补发旧提示；再次提醒仍要求回升后重新下降。诊断区分Crossings、ShellRequests、Suppressed、Failures；ShellRequests是请求数，不是用户看到的通知数。本轮全部注入假通知器，未检验真实Windows通知或所有专注助手/全屏组合。

API依据：[SHQueryUserNotificationState](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate)、[QUERY_USER_NOTIFICATION_STATE](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state)、[NotifyIcon.ShowBalloonTip](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon.showballoontip?view=windowsdesktop-10.0)。使用本项目独立代码和既有WinForms依赖。

## 配置和兼容

schemaVersion 3增加`notifications: { lowBatteryEnabled: false, thresholdPercent: 20 }`，保留Windows周期与ATK设置。schema1/2在内存中迁移为提醒关闭，不自动写文件；schema2保留用户既有ATK开关，schema1仍不授予ATK权限。旧schema中的同名未知notifications字段不会自行启用提醒。

schema3缺少notifications整体时按关闭/20处理；存在却类型错误、缺字段、已知结构重复键或越界时，整份配置保留并禁止保存，采用安全默认值。未知字段保留，写盘仍用同目录原子替换；旧版应用遇schema3会保护原文件，不悄悄覆盖。旧TrySave入口保留新的提醒设置。诊断schema升至3，添加不含设备标识的固定摘要，沿用256KiB上限。

## 验证及后续

| 软件组 | 范围 |
| --- | --- |
| LB01–LB03 | 首次/阈值/0%、新旧观测、一次提醒及5点回差、失效与代次、配置重建、分量合并、重复键及状态容量 |
| NA01 | 配置迁移/重载、保留ATK与未知字段、非法结构和失败保存不半应用、Mock控件禁用 |
| NA02 | 有效直读投影，缓存/档位/离线/失败/过期过滤，Windows状态纯规则、通知文本长度 |
| NA03 | 实际自有设置窗体及托盘接线、模拟ATK阈值变化、100次刷新无额外HID/提醒，抑制/异常不补发，诊断与退出 |

最终候选统一运行Core35项、平台62项、App21项、Mock23项/C30和发布5项。以上只证明软件接线；没有新实机结果或新资源指标。旧111804目录/ZIP保留。

| 待用户回来后验收 | 尚缺证据 |
| --- | --- |
| NL1 | 正常用户设置保存/关闭/重启，真实有效读数跨阈值时通知可见且不重复；首次低值仅建基线的实际体验 |
| NL2 | Windows通知开启/关闭、勿扰/专注、全屏和锁定场景，抑制后无旧提示补发；恢复/退出后的界面与资源 |

NL1/NL2保持NotRun，ATK的AL1–AL4和其他系统门槛不继承历史结果。设备别名/隐藏/排序、登录自启动和每设备独立图标仍是后续可选项，本轮未实现或启用。
