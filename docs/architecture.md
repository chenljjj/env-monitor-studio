# 原生 WPF 架构

Studio 是一个独立的 .NET 10 WPF Windows 程序，主窗口不是网页，也不含导航侧栏。

```text
STM32 COM ─┐
ESP32 COM ─┼─> 行切分 / 脱敏 / 容错解析 ─> SQLite（最近 24h）
MQTT TLS ─┘                 │
                            └─> 固定单窗口：连接、环境值、错误计数、最新日志
```

核心实现集中在 `src/EnvMonitor.Studio/StudioRuntime.cs`：

- `SerialCapture`：两路 `System.IO.Ports` 串口分行读取。
- `MqttCapture`：MQTTnet 的 TLS 连接、遥测 Topic 和状态 Topic 订阅。
- `DebugLogParser`：STM32 文本、ESP32 文本、MQTT JSON 的宽容解析；未知格式仍作为原始脱敏日志保存。
- `SqliteLogStore`：本机 SQLite 的日志入库、最近 24 小时清理、清空和导出读取。
- `SettingsStore`：本地连接设置；密码经 Windows DPAPI 加密后保存。
- `StudioViewModel`：只向 WPF 单窗口提供最新 14 条日志、环境值、连接状态和错误累计。

日志表为：

| 字段 | 说明 |
| --- | --- |
| `timestamp_ms` | PC 接收时间，用作 24 小时保留依据 |
| `source` | STM32、ESP32 或 MQTT |
| `level` / `category` | 容错解析得到的级别和类别 |
| `sample` | 从 `sample` / `sample_sequence` 或 MQTT JSON 提取；缺失则为空 |
| `text` | 脱敏后的原始文本 |
| `errors` | 从调试字段归类的错误标签 |

索引 `ix_logs_time(timestamp_ms DESC)` 用于加载最近日志和导出。数据库位于 `%LOCALAPPDATA%\EnvMonitorStudio\studio-logs.db`，启动和写入期间都会清除超过 24 小时的数据。

错误看板并不伪造设备状态：只在实际日志出现 AHT20、BH1750、UART、ACK、链路、网络、队列或 MQTT 错误字段时累计；没有对应日志则保持为 0。
