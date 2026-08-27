# Env Monitor Studio

`Env Monitor Studio` 是与 `env-monitor-client` 使用相同 .NET 10 + WPF 框架的 Windows 原生开发调试工具。它不是网页，也不启动浏览器。

发布目录会以 `开发调试端-日期-时间` 命名，并包含 `环境检测器.png` 与带同一图标的原生 EXE。

主窗口固定为一页：STM32、ESP32、MQTT 连接状态，最新环境数据，按日志字段累计的错误计数，以及最新日志看板。连接参数收在“连接设置”弹窗；顶部提供清空日志和一键 CSV 导出。

## 启动

需要 .NET SDK 10（本机已安装）。在本目录执行：

```powershell
dotnet run --project src\EnvMonitor.Studio\EnvMonitor.Studio.csproj
```

也可继续使用之前的命令；它现在会启动原生 WPF 程序：

```powershell
npm.cmd run dev
```

构建与发布：

```powershell
dotnet build src\EnvMonitor.Studio\EnvMonitor.Studio.csproj
dotnet publish src\EnvMonitor.Studio\EnvMonitor.Studio.csproj -c Release -r win-x64 --self-contained false -o release
```

## 使用

1. 点击“连接设置”，刷新并选择 STM32 USART2、ESP32 的 COM 口，通常均为 `115200-8-N-1`。
2. 填写 MQTT Broker、端口、Topic、用户名和密码，点击“保存并连接”。公有 CA 的 MQTTS 一般使用端口 8883，并勾选“校验 Broker 证书”。
3. 日志到达后，窗口自动更新环境卡片、错误计数和固定显示的最新 14 条日志；输入右上角搜索框可即时过滤当前看板。
4. “导出日志”会导出 SQLite 中最近 24 小时的脱敏日志 CSV，带 UTF-8 BOM，可直接由 Excel 正确打开。

## 本地数据与隐私

- SQLite 日志库和设置保存于 `%LOCALAPPDATA%\EnvMonitorStudio`，日志自动保留最近 24 小时。
- MQTT 密码使用 Windows DPAPI 加密后才会写入本机设置；密码不会提交至 Git、不会写入日志，也不会出现在导出文件。
- 项目不包含任何真实 Broker 地址、Wi-Fi 密码、API Key、私钥或证书正文。证书、私钥和本地数据库已由 `.gitignore` 排除。

## 当前支持的调试字段

- STM32：`sample`、温湿度照度、AHT20/BH1750 通信与数据错误、UART TX/RX、ACK、重试、超时、丢帧、队列丢弃、RTOS heap 等文本字段。
- ESP32：`sample`、环境数据、网络状态、队列溢出、发布错误、abandoned、duplicate 等 ESP-IDF Monitor 字段。
- MQTT：遥测/状态 Topic 的 JSON；解析 `sample`、温度、湿度、照度和 `errors` 对象。未知格式保留为原始脱敏日志，不会中断采集。
