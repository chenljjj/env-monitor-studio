using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace EnvMonitor.Studio;

public partial class MainWindow : Window
{
    private readonly StudioViewModel _viewModel = new();
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closed += OnClosed;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => new ConnectionSettingsWindow(_viewModel) { Owner = this }.ShowDialog();

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("将删除本机 SQLite 中保存的全部日志，且无法恢复。是否继续？", "清空日志", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) _viewModel.ClearLogs();
    }

    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出最近 24 小时日志", Filter = "CSV 文件|*.csv", FileName = $"env-monitor-logs-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        var rows = _viewModel.ExportableLogs();
        static string Cell(string? value) => value is null ? string.Empty : value.ContainsAny(new[] { ',', '"', '\r', '\n' }) ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        var csv = new StringBuilder("\uFEFFtime,source,level,category,sample,error_type,text\r\n");
        foreach (var row in rows) csv.AppendJoin(',', Cell(row.Timestamp.ToString("O")), Cell(row.Source), Cell(row.Level), Cell(row.Category), Cell(row.Sample), Cell(row.Errors), Cell(row.Text)).Append("\r\n");
        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
        MessageBox.Show($"已导出 {rows.Count} 条日志。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnClosed(object? sender, EventArgs e) => await _viewModel.DisposeAsync();
}
