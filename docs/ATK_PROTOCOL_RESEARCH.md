# ATK Duckbill Ultra：官方网页协议线索

后续实机更新：2026-10-22受限批次收到在线/型号/电量三帧，电量声明长度2、百分比95、原始充电0；两次在线观测分别0/1。已修正最低电量长度为2，仅声明至少4才导出电压；现有三帧状态0且和0x55，但成功码完整语义/异常与共存未覆盖，不能推成后台兼容通过。详见[实机记录](ATK_LIVE_VALIDATION.md)。以下“未发送/无样本”描述原静态研究时点。

用户随后明确核对官网同次读数也为95%。正常托盘已加入显式手动读取：完整在线/型号/电量顺序、状态0及校验和0x55、范围与清理均通过才显示，5分钟后过期，失败/暂停使旧值失效；不做自动HID查询。独立研究宿主仍保存候选观测，不把单次对应提升为所有固件/共存/长期兼容已通过。本次新托盘接线未再次访问设备。

2026-10-22。用户明确确认鼠标型号为 **ATK Duckbill Ultra**，且[ATK HUB](https://hub.atk.pro/)能通过当前2.4GHz接收器读取电量并支持配对。这是用户报告；本项目本轮没有执行设备查询，不能写成应用实机兼容通过。此前“具体子版本未确认”的历史状态被此次用户确认更新，不能从接收器ID推断其他鼠标型号。

## 已核对的官方公开脚本

主页当次引用版本`hub-v3/production/3.2.29`。静态HTTP下载、文本分析；未运行网页脚本、浏览器或WebHID。

- [COMpx设备表与协议](https://bpcdn.atkgear.com/hub-v3/production/3.2.29/static/filter-BRWy5Cag.js)：SHA-256 `7F57DD4BBC2588F3CA7D5696E9537C2992F6665D686D4E7470C7923CF3005EC7`。
- [基础HID传输层](https://bpcdn.atkgear.com/hub-v3/production/3.2.29/static/base-hid-device-BZL9Z9sP.js)：SHA-256 `C11D860630CD426E9DF3B05EC7A078EEF80EFE7FB18B3D139AB7F6E1DC01990B`。

本地忽略目录`.phase0/atk-hub-20261022/`保留主页、相关脚本、snapshot.local.json、anchors.local.json与request-vector.local.json；前两项记录URL/哈希与原始脚本字符偏移。资源可能更新，后续按此快照核对，不默认把同一URL视为永久相同内容。仅作协议事实研究，未建立复制/再分发官方实现的许可依据；不把原脚本打入产品。

## 对应关系与电量读取

| 项目 | 官网静态证据 |
| --- | --- |
| 鼠标条目 | Duckbill Ultra，VID/PID `373B:11A6`，mouseCidMid `1,2` |
| 当前接收器条目 | `373B:11FD`，Wireless mouse 8k NANO dongle-L，receiver=true |
| 两类条目 | 同属COMpx控制器；网页过滤usagePage `FF04`、usage `0002` |
| 电量入口 | `getPowerInfo()`直接使用GetBatteryLevel；`getMouseInfo()`另含连接类型查询，后读取相同电量字段 |
| 电量命令 / 报告 | GetBatteryLevel `0x04`，Output report ID `0x08`，16字节payload |
| 数据布局 | payload[0]命令、[1]状态、[2..3]地址、[4]有效长度、[5]百分比、[6]充电原始字段、[7..8]电压原始字段、[15]校验 |
| 请求校验 | `(0x55 - ((0x08 + sum(payload[0..14])) & 0xFF)) & 0xFF` |
| 区分其他操作 | 在线查询`0x03`、CID/MID查询`0x10`；配对`0x05`、EEPROM写`0x07`、恢复`0x09`、升级`0x0D`属于排除范围 |

离线推导的Windows请求缓冲区（包含报告ID）为17字节：

```text
08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49
```

这只是依据字段定义计算的测试向量，总和模256为0x55；**未发送，也没有实机响应样本**。WebHID输入payload不含报告ID，Windows读缓冲区含报告ID槽，解析偏移必须区分。充电值含义、电压单位/端序及响应状态成功取值/响应校验尚未形成完整可实施约束，不将原始字段直接当作已确认状态。

## 接入时必须保留的边界

官网过滤FF04不等于Windows应打开FF04 collection。旧描述符快照中，MI_01/Col05的FF02:0002有17/17字节Input/Output；FF04:0002仅有8字节Feature。WebHID设备与Windows collection的对应不能仅凭网页筛选字段类推。本轮未重新枚举当前接口，也未获取逐报告ID能力；真正接入前需复核精确路径、报告ID8和Input/Output能力，不遍历试写。

官网通用包装层存在读取失败后使用默认电量、或重试耗尽返回合成缓冲区的路径；本应用不能照搬为真实电量。实现应严格检查长度、报告ID、命令关联、状态、百分比范围、超时与收尾；响应校验未核实前不编造规则。没有事务序号时，同一命令的迟到响应不能仅靠命令字区分新旧请求，必须设计单一在途与迟到帧隔离。初始化不整体照搬官网流程，尤其不能自动配对、下载配置或修改输入参数。

## 已独立实现的离线成果

AtkProtocol、AtkHidPolicy、Win32AtkHidTransport、AtkManualSession及`--verify-atk-host`独立宿主已实现；只允许在线/型号/电量三类查询，默认运行不创建ATK Provider。检查接口与查询为两个显式动作，唯一匹配且无检查错误才允许读取；离线/不同型号提前停止。报告ID8通过Input/Output的按钮/值能力表检查，打开实际句柄后重新验证；不把网页FF04过滤直接套用到Windows。

整批查询/手动间隔10秒，无原生重试；超时/异常保留原始占用直至收尾，并在本会话阻止后续查询。只保存观测字段，不导出完整帧中的唯一ID/RF ID。响应长度字段采用保守检查，候选百分比必须0–100；原始状态码与“请求式校验是否匹配”仍为观测，AcceptedPercent固定为空。合成响应不能证明真实响应也采用同一校验或长度规则。此阶段不接入当前电量聚合，真实接口/响应/共存仍需单列实机验证。

原生实现依据微软[HidD_GetAttributes](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getattributes)、[HidP_GetButtonCaps](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/nf-hidpi-hidp_getbuttoncaps)及[HIDP_BUTTON_CAPS报告ID字段](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/ns-hidpi-_hidp_button_caps)。未来显式打开时使用[HidD_FlushQueue](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_flushqueue)清理该collection现有输入队列，不将其解释为能取消设备无线工作或保证消除其他应用的迟到响应。

本轮开发没有运行原生入口或任何设备查询；正式后台HID Provider仍未启用。验证范围以候选VALIDATION与开发日志为准，不将代码完成写成Duckbill Ultra实机兼容。
