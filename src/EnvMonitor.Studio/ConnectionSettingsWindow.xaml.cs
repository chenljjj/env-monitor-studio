using System.Windows;

namespace EnvMonitor.Studio;

public partial class ConnectionSettingsWindow : Window
{
    private readonly StudioViewModel _viewModel;
    public ConnectionSettingsWindow(StudioViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        LoadSettings();
        RefreshPorts();
    }

    private void LoadSettings()
    {
        var settings = _viewModel.Settings;
        StmBaudBox.Text = settings.Stm32BaudRate.ToString(); EspBaudBox.Text = settings.Esp32BaudRate.ToString();
        HostBox.Text = settings.MqttHost; PortBox.Text = settings.MqttPort.ToString(); ClientIdBox.Text = settings.MqttClientId; TelemetryTopicBox.Text = settings.TelemetryTopic; StatusTopicBox.Text = settings.StatusTopic; UsernameBox.Text = settings.MqttUsername; ValidateCertificateBox.IsChecked = settings.ValidateBrokerCertificate; CaCertificateBox.Text = settings.CaCertificatePath; ClientCertificateBox.Text = settings.ClientCertificatePath; PrivateKeyBox.Text = settings.PrivateKeyPath;
    }
    private void RefreshPorts()
    {
        var ports = _viewModel.Ports();
        StmPortBox.ItemsSource = ports; EspPortBox.ItemsSource = ports;
        StmPortBox.SelectedItem = _viewModel.Settings.Stm32Port; EspPortBox.SelectedItem = _viewModel.Settings.Esp32Port;
    }
    private StudioSettings ReadSettings()
    {
        if (!int.TryParse(StmBaudBox.Text, out var stmBaud) || stmBaud < 1200 || !int.TryParse(EspBaudBox.Text, out var espBaud) || espBaud < 1200 || !int.TryParse(PortBox.Text, out var mqttPort) || mqttPort is < 1 or > 65535) throw new InvalidOperationException("波特率或 MQTT 端口无效。");
        return new StudioSettings { Stm32Port = StmPortBox.SelectedItem?.ToString() ?? string.Empty, Stm32BaudRate = stmBaud, Esp32Port = EspPortBox.SelectedItem?.ToString() ?? string.Empty, Esp32BaudRate = espBaud, MqttHost = HostBox.Text.Trim(), MqttPort = mqttPort, MqttClientId = string.IsNullOrWhiteSpace(ClientIdBox.Text) ? $"env-monitor-studio-{Guid.NewGuid():N}"[..28] : ClientIdBox.Text.Trim(), TelemetryTopic = TelemetryTopicBox.Text.Trim(), StatusTopic = StatusTopicBox.Text.Trim(), MqttUsername = UsernameBox.Text.Trim(), ValidateBrokerCertificate = ValidateCertificateBox.IsChecked != false, CaCertificatePath = CaCertificateBox.Text.Trim(), ClientCertificatePath = ClientCertificateBox.Text.Trim(), PrivateKeyPath = PrivateKeyBox.Text.Trim(), ProtectedPassword = _viewModel.Settings.ProtectedPassword };
    }
    private void SaveSettings()
    {
        var password = PasswordBox.Password;
        _viewModel.SaveSettings(ReadSettings(), string.IsNullOrWhiteSpace(password) ? null : password);
    }
    private async void ConnectStm_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { SaveSettings(); await _viewModel.ConnectStm32Async(); }, "STM32 串口已连接。");
    private async void ConnectEsp_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { SaveSettings(); await _viewModel.ConnectEsp32Async(); }, "ESP32 串口已连接。");
    private async void ConnectMqtt_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { SaveSettings(); await _viewModel.ConnectMqttAsync(); }, "MQTT 已连接并订阅。" );
    private async void DisconnectMqtt_Click(object sender, RoutedEventArgs e) => await RunAsync(_viewModel.DisconnectMqttAsync, "MQTT 已断开。");
    private void DisconnectStm_Click(object sender, RoutedEventArgs e) { _viewModel.DisconnectStm32(); StatusText.Text = "STM32 串口已断开。"; }
    private void DisconnectEsp_Click(object sender, RoutedEventArgs e) { _viewModel.DisconnectEsp32(); StatusText.Text = "ESP32 串口已断开。"; }
    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();
    private async Task RunAsync(Func<Task> action, string success)
    {
        try { StatusText.Text = "正在处理…"; await action(); StatusText.Text = success; }
        catch (Exception exception) { StatusText.Text = $"连接失败：{exception.Message}"; }
    }
}
