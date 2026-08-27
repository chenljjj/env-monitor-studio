using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MQTTnet;
using MQTTnet.Protocol;

namespace EnvMonitor.Studio;

public sealed class StudioSettings
{
    public string Stm32Port { get; set; } = string.Empty;
    public int Stm32BaudRate { get; set; } = 115200;
    public string Esp32Port { get; set; } = string.Empty;
    public int Esp32BaudRate { get; set; } = 115200;
    public string MqttHost { get; set; } = string.Empty;
    public int MqttPort { get; set; } = 8883;
    public string MqttClientId { get; set; } = $"env-monitor-studio-{Guid.NewGuid():N}"[..28];
    public string TelemetryTopic { get; set; } = "stm32-env-monitor/telemetry";
    public string StatusTopic { get; set; } = "stm32-env-monitor/status";
    public string MqttUsername { get; set; } = string.Empty;
    public bool ValidateBrokerCertificate { get; set; } = true;
    public string CaCertificatePath { get; set; } = string.Empty;
    public string ClientCertificatePath { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string? ProtectedPassword { get; set; }
}

public sealed class LogItem
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Source { get; init; }
    public required string Level { get; init; }
    public required string Category { get; init; }
    public string Sample { get; init; } = "—";
    public required string Text { get; init; }
    public string Errors { get; init; } = string.Empty;
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

public sealed class ErrorCounter : INotifyPropertyChanged
{
    private int _count;
    public ErrorCounter(string name) => Name = name;
    public string Name { get; }
    public int Count { get => _count; set { if (_count == value) return; _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed record ParsedLog(string Category, string? Sample, double? Temperature, double? Humidity, double? Illuminance, string[] Errors);

internal static partial class DebugLogParser
{
    [GeneratedRegex(@"\b(?:sample|sample_sequence)\s*[=:]\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex SampleRegex();
    [GeneratedRegex(@"\b(?:t|temperature(?:_c)?)\s*[=:]\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex TemperatureRegex();
    [GeneratedRegex(@"\b(?:h|humidity(?:_rh)?)\s*[=:]\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex HumidityRegex();
    [GeneratedRegex(@"\b(?:l|lux|illuminance(?:_lux)?)\s*[=:]\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex IlluminanceRegex();
    [GeneratedRegex(@"\b(?<name>aht20_(?:comm|data)|bh1750_(?:comm|data)|txerr|rxerr|ackerr|retry|timeout|drop|qdrop|overflow|puberr|abandoned|duplicate)\s*[=:]\s*(?<value>\d+(?:/\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex CounterRegex();

    public static ParsedLog Parse(string source, string text)
    {
        if (source == "MQTT") return ParseMqtt(text);
        var errors = new List<string>();
        foreach (Match match in CounterRegex().Matches(text))
        {
            if (match.Groups["value"].Value.Split('/').Any(value => int.TryParse(value, out var number) && number > 0)) errors.Add(NormalizeError(match.Groups["name"].Value));
        }
        if (source == "STM32" && Regex.IsMatch(text, @"\b(?:aht|bh)=?(?:ERR|FAIL)|uart\d?=(?:ERR|FAIL)", RegexOptions.IgnoreCase)) errors.Add("传感器/UART");
        if (source == "ESP32" && Regex.IsMatch(text, @"\b(?:wifi|mqtt).*(?:disconnect|fail|error)|\bnet=(?!WM|IP|MQ)", RegexOptions.IgnoreCase)) errors.Add("网络");
        var category = source == "STM32" && text.Contains("heap=", StringComparison.OrdinalIgnoreCase) ? "RTOS资源" :
            source == "ESP32" && text.Contains("net=", StringComparison.OrdinalIgnoreCase) ? "网络统计" : "环境采样";
        return new ParsedLog(category, Value(SampleRegex(), text), Number(TemperatureRegex(), text), Number(HumidityRegex(), text), Number(IlluminanceRegex(), text), errors.Distinct().ToArray());
    }

    private static ParsedLog ParseMqtt(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            var errors = new List<string>();
            if (root.TryGetProperty("errors", out var errorObject) && errorObject.ValueKind == JsonValueKind.Object)
                foreach (var property in errorObject.EnumerateObject()) if (property.Value.TryGetInt32(out var value) && value > 0) errors.Add(NormalizeError(property.Name));
            return new ParsedLog("遥测JSON", JsonString(root, "sample"), JsonNumber(root, "temperature_c"), JsonNumber(root, "humidity_rh"), JsonNumber(root, "illuminance_lux"), errors.ToArray());
        }
        catch (JsonException) { return new ParsedLog("JSON解析", null, null, null, null, ["MQTT JSON"]); }
    }

    private static string? Value(Regex regex, string text) => regex.Match(text) is { Success: true } match ? match.Groups[1].Value : null;
    private static double? Number(Regex regex, string text) => double.TryParse(Value(regex, text), CultureInfo.InvariantCulture, out var value) ? value : null;
    private static double? JsonNumber(JsonElement root, string name) => root.TryGetProperty(name, out var property) && property.TryGetDouble(out var value) ? value : null;
    private static string? JsonString(JsonElement root, string name) => root.TryGetProperty(name, out var property) ? property.ToString() : null;
    private static string NormalizeError(string name) => name.ToLowerInvariant() switch { "aht20_comm" or "aht20_data" => "AHT20", "bh1750_comm" or "bh1750_data" => "BH1750", "txerr" or "rxerr" => "UART", "ackerr" => "ACK", "retry" or "timeout" or "drop" or "qdrop" => "链路", "overflow" => "队列", "puberr" or "abandoned" or "duplicate" => "MQTT", _ => name };
}

internal sealed class SqliteLogStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    public SqliteLogStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnvMonitorStudio");
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "studio-logs.db") }.ToString();
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS logs (id INTEGER PRIMARY KEY, timestamp_ms INTEGER NOT NULL, source TEXT NOT NULL, level TEXT NOT NULL, category TEXT NOT NULL, sample TEXT, text TEXT NOT NULL, errors TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_logs_time ON logs(timestamp_ms DESC);";
            command.ExecuteNonQuery();
            PurgeExpired(connection);
        }
    }
    public void Add(LogItem item)
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO logs(timestamp_ms,source,level,category,sample,text,errors) VALUES($time,$source,$level,$category,$sample,$text,$errors);";
            command.Parameters.AddWithValue("$time", item.Timestamp.ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$source", item.Source); command.Parameters.AddWithValue("$level", item.Level); command.Parameters.AddWithValue("$category", item.Category); command.Parameters.AddWithValue("$sample", item.Sample == "—" ? DBNull.Value : item.Sample); command.Parameters.AddWithValue("$text", item.Text); command.Parameters.AddWithValue("$errors", item.Errors); command.ExecuteNonQuery();
            if (DateTimeOffset.Now.Second % 30 == 0) PurgeExpired(connection);
        }
    }
    public List<LogItem> Latest(int limit = 120)
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT timestamp_ms,source,level,category,COALESCE(sample,''),text,errors FROM logs ORDER BY timestamp_ms DESC LIMIT $limit"; command.Parameters.AddWithValue("$limit", limit); using var reader = command.ExecuteReader(); var result = new List<LogItem>();
            while (reader.Read()) result.Add(new LogItem { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), Source = reader.GetString(1), Level = reader.GetString(2), Category = reader.GetString(3), Sample = reader.GetString(4) is { Length: > 0 } sample ? sample : "—", Text = reader.GetString(5), Errors = reader.GetString(6) }); return result;
        }
    }
    public List<LogItem> All()
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT timestamp_ms,source,level,category,COALESCE(sample,''),text,errors FROM logs WHERE timestamp_ms >= $start ORDER BY timestamp_ms DESC"; command.Parameters.AddWithValue("$start", DateTimeOffset.Now.AddHours(-24).ToUnixTimeMilliseconds()); using var reader = command.ExecuteReader(); var result = new List<LogItem>();
            while (reader.Read()) result.Add(new LogItem { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), Source = reader.GetString(1), Level = reader.GetString(2), Category = reader.GetString(3), Sample = reader.GetString(4) is { Length: > 0 } sample ? sample : "—", Text = reader.GetString(5), Errors = reader.GetString(6) }); return result;
        }
    }
    public void Clear() { lock (_gate) { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM logs"; command.ExecuteNonQuery(); } }
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
    private static void PurgeExpired(SqliteConnection connection) { using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM logs WHERE timestamp_ms < $cutoff"; command.Parameters.AddWithValue("$cutoff", DateTimeOffset.Now.AddHours(-24).ToUnixTimeMilliseconds()); command.ExecuteNonQuery(); }
}

internal sealed class SettingsStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnvMonitorStudio", "settings.json");
    public StudioSettings Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(_path)) ?? new StudioSettings() : new StudioSettings(); }
        catch (JsonException) { return new StudioSettings(); }
        catch (IOException) { return new StudioSettings(); }
    }
    public void Save(StudioSettings settings, string? plaintextPassword)
    {
        if (!string.IsNullOrEmpty(plaintextPassword)) settings.ProtectedPassword = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintextPassword), null, DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
    public string Password(StudioSettings settings)
    {
        try { return string.IsNullOrWhiteSpace(settings.ProtectedPassword) ? string.Empty : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedPassword), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return string.Empty; }
        catch (FormatException) { return string.Empty; }
    }
}

internal sealed class SerialCapture : IDisposable
{
    private SerialPort? _port;
    private string _pending = string.Empty;
    public event Action<string>? LineReceived;
    public bool Connected => _port?.IsOpen == true;
    public void Connect(string port, int baudRate)
    {
        Disconnect(); _port = new SerialPort(port, baudRate, Parity.None, 8, StopBits.One) { NewLine = "\n", ReadTimeout = 500 }; _port.DataReceived += OnData; _port.Open();
    }
    public void Disconnect() { if (_port is null) return; _port.DataReceived -= OnData; if (_port.IsOpen) _port.Close(); _port.Dispose(); _port = null; _pending = string.Empty; }
    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        try { _pending += _port?.ReadExisting() ?? string.Empty; var lines = _pending.Split('\n'); _pending = lines[^1]; foreach (var line in lines[..^1]) if (!string.IsNullOrWhiteSpace(line)) LineReceived?.Invoke(line.TrimEnd('\r')); } catch (InvalidOperationException) { }
    }
    public void Dispose() => Disconnect();
}

internal sealed class MqttCapture : IAsyncDisposable
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    public event Action<string, string>? MessageReceived;
    public event Action<string>? StatusChanged;
    public MqttCapture()
    {
        _client.ApplicationMessageReceivedAsync += e => { MessageReceived?.Invoke(e.ApplicationMessage.Topic, Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray())); return Task.CompletedTask; };
        _client.ConnectedAsync += _ => { StatusChanged?.Invoke("已连接"); return Task.CompletedTask; };
        _client.DisconnectedAsync += _ => { StatusChanged?.Invoke("未连接"); return Task.CompletedTask; };
    }
    public async Task ConnectAsync(StudioSettings settings, string password)
    {
        if (string.IsNullOrWhiteSpace(settings.MqttHost)) throw new InvalidOperationException("请填写 MQTT Broker 主机。");
        if (_client.IsConnected) await _client.DisconnectAsync();
        var builder = new MqttClientOptionsBuilder().WithClientId(settings.MqttClientId).WithTcpServer(settings.MqttHost, settings.MqttPort).WithKeepAlivePeriod(TimeSpan.FromSeconds(45)).WithCleanSession();
        if (!string.IsNullOrWhiteSpace(settings.MqttUsername)) builder.WithCredentials(settings.MqttUsername, password);
        builder.WithTlsOptions(tls =>
        {
            tls.WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
            tls.WithAllowUntrustedCertificates(!settings.ValidateBrokerCertificate);
            if (!string.IsNullOrWhiteSpace(settings.ClientCertificatePath))
            {
                var clientCertificate = string.IsNullOrWhiteSpace(settings.PrivateKeyPath)
                    ? X509CertificateLoader.LoadCertificateFromFile(settings.ClientCertificatePath)
                    : X509Certificate2.CreateFromPemFile(settings.ClientCertificatePath, settings.PrivateKeyPath);
                tls.WithClientCertificates(new X509Certificate2Collection(clientCertificate));
            }
            if (!string.IsNullOrWhiteSpace(settings.CaCertificatePath))
            {
                var root = X509CertificateLoader.LoadCertificateFromFile(settings.CaCertificatePath);
                tls.WithCertificateValidationHandler(context => ValidateAgainstCustomRoot(context, root, settings.ValidateBrokerCertificate));
            }
        });
        await _client.ConnectAsync(builder.Build());
        var topics = new MqttClientSubscribeOptionsBuilder().WithTopicFilter(t => t.WithTopic(settings.TelemetryTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
        if (!string.IsNullOrWhiteSpace(settings.StatusTopic)) topics.WithTopicFilter(t => t.WithTopic(settings.StatusTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
        await _client.SubscribeAsync(topics.Build()); StatusChanged?.Invoke($"已订阅 {settings.TelemetryTopic}");
    }
    public async Task DisconnectAsync() { if (_client.IsConnected) await _client.DisconnectAsync(); }
    public async ValueTask DisposeAsync() { await DisconnectAsync(); _client.Dispose(); }
    private static bool ValidateAgainstCustomRoot(MqttClientCertificateValidationEventArgs context, X509Certificate2 root, bool validate)
    {
        if (!validate) return true;
        if (context.Certificate is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(context.Certificate));
    }
}

public sealed class StudioViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly SettingsStore _settingsStore = new();
    private readonly SqliteLogStore _store = new();
    private readonly SerialCapture _stm32 = new();
    private readonly SerialCapture _esp32 = new();
    private readonly MqttCapture _mqtt = new();
    private readonly List<LogItem> _recent = [];
    private readonly Dictionary<string, ErrorCounter> _errors;
    private string _stm32Status = "未连接", _esp32Status = "未连接", _mqttStatus = "未连接";
    private string _stm32Detail = "请选择 USART2 COM 口", _esp32Detail = "请选择 ESP-IDF COM 口", _mqttDetail = "请在连接设置中配置";
    private string _temperature = "—", _humidity = "—", _illuminance = "—", _lastUpdate = "等待真实日志";
    private string _searchText = string.Empty;

    public StudioViewModel()
    {
        Settings = _settingsStore.Load();
        ErrorCounters = new ObservableCollection<ErrorCounter>(new[] { "AHT20", "BH1750", "UART", "ACK", "链路", "网络", "队列", "MQTT" }.Select(name => new ErrorCounter(name)));
        _errors = ErrorCounters.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _store.Latest()) _recent.Add(item);
        _stm32.LineReceived += line => Receive("STM32", line);
        _esp32.LineReceived += line => Receive("ESP32", line);
        _mqtt.MessageReceived += (topic, text) => Receive("MQTT", $"[{topic}] {text}", text);
        _mqtt.StatusChanged += detail => RunOnUi(() => { MqttStatus = detail.StartsWith("已", StringComparison.Ordinal) ? "已连接" : "未连接"; MqttDetail = detail; });
        RefreshVisible();
    }

    public StudioSettings Settings { get; private set; }
    public ObservableCollection<LogItem> VisibleLogs { get; } = [];
    public ObservableCollection<ErrorCounter> ErrorCounters { get; }
    public string Stm32Status { get => _stm32Status; private set => Set(ref _stm32Status, value); }
    public string Esp32Status { get => _esp32Status; private set => Set(ref _esp32Status, value); }
    public string MqttStatus { get => _mqttStatus; private set => Set(ref _mqttStatus, value); }
    public string Stm32Detail { get => _stm32Detail; private set => Set(ref _stm32Detail, value); }
    public string Esp32Detail { get => _esp32Detail; private set => Set(ref _esp32Detail, value); }
    public string MqttDetail { get => _mqttDetail; private set => Set(ref _mqttDetail, value); }
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string Humidity { get => _humidity; private set => Set(ref _humidity, value); }
    public string Illuminance { get => _illuminance; private set => Set(ref _illuminance, value); }
    public string LastUpdate { get => _lastUpdate; private set => Set(ref _lastUpdate, value); }
    public string LogSummary => $"{_recent.Count} 条近期日志 · 固定显示最新 14 条 · 可搜索";
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) RefreshVisible(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SaveSettings(StudioSettings settings, string? password)
    {
        Settings = settings; _settingsStore.Save(settings, password); Raise(nameof(Settings));
    }
    public string SavedPassword() => _settingsStore.Password(Settings);
    public string[] Ports() => SerialPort.GetPortNames().OrderBy(port => port, StringComparer.OrdinalIgnoreCase).ToArray();
    public async Task ConnectStm32Async()
    {
        if (string.IsNullOrWhiteSpace(Settings.Stm32Port)) throw new InvalidOperationException("请选择 STM32 串口。");
        Stm32Status = "连接中"; Stm32Detail = Settings.Stm32Port;
        await Task.Run(() => _stm32.Connect(Settings.Stm32Port, Settings.Stm32BaudRate)); Stm32Status = "已连接"; Stm32Detail = $"{Settings.Stm32Port} · {Settings.Stm32BaudRate}-8-N-1";
    }
    public async Task ConnectEsp32Async()
    {
        if (string.IsNullOrWhiteSpace(Settings.Esp32Port)) throw new InvalidOperationException("请选择 ESP32 串口。");
        Esp32Status = "连接中"; Esp32Detail = Settings.Esp32Port;
        await Task.Run(() => _esp32.Connect(Settings.Esp32Port, Settings.Esp32BaudRate)); Esp32Status = "已连接"; Esp32Detail = $"{Settings.Esp32Port} · {Settings.Esp32BaudRate}-8-N-1";
    }
    public void DisconnectStm32() { _stm32.Disconnect(); Stm32Status = "未连接"; Stm32Detail = "已手动断开"; }
    public void DisconnectEsp32() { _esp32.Disconnect(); Esp32Status = "未连接"; Esp32Detail = "已手动断开"; }
    public async Task ConnectMqttAsync() { MqttStatus = "连接中"; MqttDetail = $"正在连接 {Settings.MqttHost}"; await _mqtt.ConnectAsync(Settings, SavedPassword()); MqttStatus = "已连接"; MqttDetail = $"已订阅 {Settings.TelemetryTopic}"; }
    public async Task DisconnectMqttAsync() { await _mqtt.DisconnectAsync(); MqttStatus = "未连接"; MqttDetail = "已手动断开"; }
    public void ClearLogs()
    {
        _store.Clear(); _recent.Clear(); VisibleLogs.Clear(); foreach (var counter in ErrorCounters) counter.Count = 0; LastUpdate = "日志已清空"; Raise(nameof(LogSummary));
    }
    public IReadOnlyList<LogItem> ExportableLogs() => _store.All();
    public async ValueTask DisposeAsync() { _stm32.Dispose(); _esp32.Dispose(); await _mqtt.DisposeAsync(); }

    private void Receive(string source, string displayText, string? parserText = null)
    {
        var safeText = Redact(displayText); var parsed = DebugLogParser.Parse(source, parserText ?? safeText); var item = new LogItem { Timestamp = DateTimeOffset.Now, Source = source, Level = parsed.Errors.Length == 0 ? "信息" : "错误", Category = parsed.Category, Sample = parsed.Sample ?? "—", Text = safeText, Errors = string.Join('|', parsed.Errors) };
        _store.Add(item); RunOnUi(() => Apply(item, parsed));
    }
    private void Apply(LogItem item, ParsedLog parsed)
    {
        _recent.Insert(0, item); if (_recent.Count > 120) _recent.RemoveAt(_recent.Count - 1);
        foreach (var error in parsed.Errors) if (_errors.TryGetValue(error, out var counter)) counter.Count++;
        if (parsed.Temperature is { } temperature) Temperature = $"{temperature:F2} °C";
        if (parsed.Humidity is { } humidity) Humidity = $"{humidity:F2} %RH";
        if (parsed.Illuminance is { } illuminance) Illuminance = $"{illuminance:F2} lx";
        LastUpdate = $"最后更新：{item.TimeText} · {item.Source}"; RefreshVisible(); Raise(nameof(LogSummary));
    }
    private void RefreshVisible()
    {
        var match = _recent.Where(item => string.IsNullOrWhiteSpace(SearchText) || item.Text.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || item.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || item.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).Take(14).ToArray();
        VisibleLogs.Clear(); foreach (var item in match) VisibleLogs.Add(item);
    }
    private static string Redact(string text)
    {
        var value = Regex.Replace(text, "(?i)(\\\"?(?:password|passwd|token|api[_-]?key)\\\"?\\s*[:=]\\s*\\\")([^\\\"]+)(\\\")", "$1***$3");
        return Regex.Replace(value, "(?i)((?:password|passwd|token|api[_-]?key)\\s*[:=]\\s*)([^\\s,;]+)", "$1***");
    }
    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else _ = dispatcher.BeginInvoke(action);
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(property); return true; }
    private void Raise(string? property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
