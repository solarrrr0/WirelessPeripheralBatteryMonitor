# 第三方来源与依赖记录

2026-10-26：0.6.0数字托盘使用本项目自行编写的System.Drawing路径绘制，调用Windows已有Segoe UI字体，不随包再分发字体或新增图标素材、第三方库。

2026-10-23 Phase5：快照/连接状态分配优化和软件负载夹具为独立实现，无新增生产依赖。自带.NET10.0.12运行时仅在本地忽略目录进行同版本发布比较，不进入正式framework-dependent候选；微软运行时文件和本地实验ZIP不上传或作为已验收独立版本分发。

2026-10-23设备设置：稳定摘要身份、别名/隐藏/排序、可选每设备NotifyIcon及当前用户Run自启动均为本项目独立实现，复用.NET/WinForms、SHA256和Windows注册表，无新增生产依赖或第三方代码。官方接口依据、260字符启动命令上限与验证边界见docs/PHASE4_DEVICE_SETTINGS.md。启动注册检查使用假注册表，没有修改用户实际启动项。

2026-10-23低电量提醒：阈值/回差状态、schema3设置、生产托盘投影及通知封装为独立实现，复用既有.NET/WinForms与Windows Shell API，无新增生产依赖或第三方代码。SHQueryUserNotificationState/NotifyIcon官方依据及未验系统限制见docs/PHASE4_LOW_BATTERY.md；本次仅使用假通知器，不发送真实系统通知或进行设备查询。

2026-10-23软件接续：ATK生产批次、可选自动调度、设置及诊断为本项目独立实现，复用既有原生传输/编解码，没有新增生产依赖或复制官网代码。此前95%回复及用户官网核对只支持该次读数；本次自动刷新没有新实机查询或共存验证，下方协议研究限制保留历史语境。

## ATK官方网页协议研究（2026-10-22）

[ATK HUB](https://hub.atk.pro/)公开3.2.29脚本仅作静态协议事实研究，本轮未建立其实现代码的复制/再分发许可依据。原脚本保存在忽略的.phase0目录，不进入产品或ZIP，未运行网页/WebHID。出处、哈希及边界见[官方协议研究](docs/ATK_PROTOCOL_RESEARCH.md)；后续实现应独立编写，不复制官网的驱动、界面、资源或失败默认值逻辑。

ATK编解码、Win32传输及手动宿主为本项目独立编写，只使用已记录的协议事实和微软公开API；没有复制、打包或执行官网驱动实现，也没有移植所研究Go项目代码。响应成功状态/校验尚无目标实机证据，不随参考资料自动获得验证。

## Phase 2 候选应用依赖（2026-10-22）

应用源代码为本项目新建，没有复制 Phase 0 所研究项目的 Provider 或 UI 代码；图标由本项目绘制，没有第三方图片资源。

- .NET / WinForms 由已安装的 Microsoft .NET 10 Desktop Runtime 提供；framework-dependent 包不包含该运行时。[.NET Runtime 许可](https://github.com/dotnet/runtime/blob/v10.0.12/LICENSE.TXT)、[WinForms 许可](https://github.com/dotnet/winforms/blob/v10.0.12/LICENSE.TXT)。
- Windows 版本目标自动引用 `Microsoft.Windows.SDK.NET.Ref 10.0.19041.57`，发布包含 `Microsoft.Windows.SDK.NET.dll` 与 `WinRT.Runtime.dll`。Phase 2使用WinRT已配对设备发现；标准GATT适配器已有实现但候选自动Provider未启用。NuGet 包元数据指向微软 Windows SDK 许可，不能直接把整个包认定为 MIT。原始许可随包放在 [licenses/Windows-SDK-license.rtf](licenses/Windows-SDK-license.rtf)，来源为 [SDK 包声明的许可地址](https://aka.ms/WinSDKLicenseURL)。
- `WinRT.Runtime.dll` 的文件版本为 2.2.0.48161，来源为 Microsoft C#/WinRT 2.2.0 系列，采用 MIT；完整版权、许可与免责声明保存在 [licenses/CsWinRT-LICENSE.txt](licenses/CsWinRT-LICENSE.txt)，来源为 [Microsoft C#/WinRT 2.2.0.241111.1](https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/LICENSE)。
- 构建所用 .NET SDK 10.0.402 仅在忽略目录 `.tools/` 中本地使用，不包含在 Phase 1 可运行 ZIP 中。

开发诊断另使用Microsoft的`dotnet-counters 10.0.750501`（项目本地`.tools/diagnostics/`），仅采集本轮启动应用的运行时计数器；它及其依赖不进入候选包或产品后台进程。使用方式参考[Microsoft诊断工具文档](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)。外部驻留页分类工具为本项目新写的Win32 API调用代码。

## Phase 0 调研快照

Phase 0 当时只有诊断脚本和研究报告，没有把第三方 Provider 代码集成到正式程序。下载的源码快照与官方运行基线保存在 Git 忽略的 `.phase0/` 内，仅用于本地审查；本文件不授予额外再分发权利。

| 来源 | 本轮核查的许可 | 用途 |
| --- | --- | --- |
| [HeyOkay/HaloBattery](https://github.com/HeyOkay/HaloBattery/blob/v1.15.0/LICENSE) | MIT；Copyright (c) 2026 Halo Battery contributors | 官方 v1.15.0 运行基线、Provider 与连接监听审查 |
| [JKWTCN/BatteryMonitor](https://github.com/JKWTCN/BatteryMonitor/blob/50f628c1ae1ced004e9fa03737cee9d6a561d80e/LICENSE) | MIT；Copyright (c) 2026 JKWTCN | 代码阅读，未运行或集成 |
| [libratbag/libratbag](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/COPYING) | MIT 风格；具体 src/hidpp20.c 自带许可和版权 | HID++ 参考；本机无目标 Logitech 设备 |
| [noosxe/atk-tool](https://github.com/noosxe/atk-tool/blob/fb4e0c65b5fa060dd4a16fa0ecd1639b9e0903fa/LICENSE) | MIT；Copyright 2026 Levon Kirakosyan | ATK 协议参考上游 |
| [papelblume/atk-tool-plus](https://github.com/papelblume/atk-tool-plus/blob/a1a9e4663705bcc0d71ded33c7783b9b0371c1f2/LICENSE) | MIT；保留上游 Levon Kirakosyan 声明 | 精确 373B:11FD 条目和协议候选；未执行查询 |
| [cobanov/atkctl](https://github.com/cobanov/atkctl/blob/faf21521ebc058872ae9a44f4569354f30659f9c/LICENSE) | MIT；Copyright (c) 2026 Mert Cobanov | macOS/FF05 协议比较，未移植 |
| [o0Zz/PeripheralBatteryMonitor](https://github.com/o0Zz/PeripheralBatteryMonitor/tree/b0e33116ba29bbfe6ed1d47486e434603cb3406a) | 未发现许可证文件 | 仅研究接口思路；禁止据此认定可复制 |
| [ReformedDoge/0x44oge-ATK](https://github.com/ReformedDoge/0x44oge-ATK/blob/0114d22280b1ce6155a5d23505153f2ae063e0f9/README.md) | README License = TBD | 仅比较，未运行或复制 |

若后续复制或移植 MIT 代码，应随相关代码保留完整许可、版权和免责声明；还需逐文件检查协议实现所引用的上游，而不是只看仓库首页的 MIT 标签。HaloBattery 官方包的 Python、hidapi、pystray、Pillow、Tcl/Tk 等依赖也需在真正再分发时逐项核查。本阶段没有发布这些二进制，也未给尚无许可证的代码补写自定许可证。
