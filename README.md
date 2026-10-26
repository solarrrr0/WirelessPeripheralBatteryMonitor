# Wireless Peripheral Battery Monitor

轻量 Windows 无线外设电量托盘工具。当前发布版本为 **v0.5.2**，数字托盘功能尚未加入此版本。

## 下载与运行

1. 在 [Releases](https://github.com/solarrrr0/WirelessPeripheralBatteryMonitor/releases/tag/v0.5.2) 下载 `PeripheralBatteryTray-v0.5.2-win-x64.zip`。
2. 安装 [Microsoft .NET 10 Desktop Runtime（Windows x64）](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。
3. 将压缩包完整解压到固定目录，双击 `Start.cmd`。程序驻留任务栏右下角，图标可能位于隐藏图标区。

可下载同名 `.sha256` 文件核对压缩包。此版本未进行代码签名。

## 功能与范围

- Windows 蓝牙设备系统电量报告；无有效读数时显示未知或离线。
- ATK Duckbill Ultra 鼠标：指定接收器和接口的手动读取，可自行开启 5–30 分钟自动读取。
- 设备别名、隐藏、排序、独立设备托盘图标、可选登录启动和低电量提醒。
- v0.5.2 简化耳机列表为名称和电量百分比，来源和冲突详情保留在悬停提示及诊断中。
- 所有设备信息与设置均在本机处理，无云端服务。

本项目围绕 ATK Duckbill Ultra 与 MOMENTUM TW 4 开发。系统电量报告的时效未知，多候选取低值仅供显示，不推测左右耳或充电盒；不保证其他设备可读取。设备直读、系统缓存、Mock 软件检查有明确区分。真实系统、通知与长期稳定性测试的未验边界见包内说明。

## 从源码构建

使用 Windows、PowerShell 7 和 .NET 10 SDK：

```powershell
dotnet build PeripheralBatteryTray.slnx -c Release
pwsh -File scripts/Publish-Phase2Candidate.ps1 -Zip
```

公开源码保留原发布清单的 113 项构建输入；`release/v0.5.2-manifest.json` 记录原始构建标识与 SHA-256。重新构建会产生独立构建标识。用户配置、原始硬件诊断、开发日志、本地 SDK 和测试输出不在本仓库中。

## 第三方说明

参见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 及 `licenses/`。应用使用 .NET / WinForms 和微软 Windows API，无需安装内核驱动。
