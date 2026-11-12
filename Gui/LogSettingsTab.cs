using System.Diagnostics;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Logging;

namespace RemoteFs.Gui;

/// <summary>
/// 日志设置页：等级、载荷开关、分割与保留参数，以及清空日志。
/// </summary>
public sealed class LogSettingsTab : UserControl
{
    private readonly ConfigStore _configStore;
    private readonly Action _onSettingsChanged;

    private readonly ComboBox _levelCombo = new();
    private readonly CheckBox _recordPayloadsCheckBox = new();
    private readonly NumericUpDown _maxFileSizeBox = new();
    private readonly NumericUpDown _retentionDaysBox = new();
    private readonly NumericUpDown _maxFilesBox = new();
    private readonly Label _directoryLabel = new();
    private readonly Label _statusLabel = new();

    public LogSettingsTab(ConfigStore configStore, Action onSettingsChanged)
    {
        _configStore = configStore;
        _onSettingsChanged = onSettingsChanged;

        InitializeLayout();
        LoadSettings();
    }

    /// <summary>
    /// 组装界面
    /// </summary>
    private void InitializeLayout()
    {
        Dock = DockStyle.Fill;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildRiskWarning(), 0, 0);
        root.Controls.Add(BuildFields(), 0, 1);
        root.Controls.Add(BuildDirectoryInfo(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);

        Controls.Add(root);
    }

    /// <summary>
    /// 风险提示：记录完整出入参会把服务器上的文件内容明文落盘
    /// </summary>
    private Control BuildRiskWarning()
    {
        var label = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 56,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = Color.FromArgb(255, 246, 214),
            ForeColor = Color.FromArgb(150, 90, 0),
            Text = "提示：开启「记录完整出入参」后，AI 读取或写入的服务器文件内容会以明文写入本机日志文件，"
                + "其中可能包含密码、密钥等敏感信息。日志目录已限制为仅当前用户可访问。"
        };
        return label;
    }

    /// <summary>
    /// 设置项
    /// </summary>
    private Control BuildFields()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 0,
            Padding = new Padding(4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _levelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _levelCombo.Width = 160;
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            _levelCombo.Items.Add(level.ToString());
        }
        AddRow(table, "日志等级", _levelCombo);

        _recordPayloadsCheckBox.Text = "记录工具调用的完整出入参（含文件内容）";
        _recordPayloadsCheckBox.AutoSize = true;
        AddRow(table, "出入参", _recordPayloadsCheckBox);

        _maxFileSizeBox.Minimum = 1;
        _maxFileSizeBox.Maximum = 1024;
        _maxFileSizeBox.Value = 10;
        _maxFileSizeBox.Width = 160;
        AddRow(table, "单文件上限(MB)", _maxFileSizeBox);

        _retentionDaysBox.Minimum = 1;
        _retentionDaysBox.Maximum = 365;
        _retentionDaysBox.Value = 7;
        _retentionDaysBox.Width = 160;
        AddRow(table, "保留天数", _retentionDaysBox);

        _maxFilesBox.Minimum = 1;
        _maxFilesBox.Maximum = 1000;
        _maxFilesBox.Value = 20;
        _maxFilesBox.Width = 160;
        AddRow(table, "最多文件数", _maxFilesBox);

        return table;
    }

    /// <summary>
    /// 日志目录信息
    /// </summary>
    private Control BuildDirectoryInfo()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(4)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(new Label { Text = "日志目录", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 0);

        // 路径可能比这一列还长，允许换行显示，别直接裁掉
        _directoryLabel.AutoSize = true;
        _directoryLabel.MaximumSize = new Size(420, 0);
        _directoryLabel.Margin = new Padding(3, 4, 3, 3);
        panel.Controls.Add(_directoryLabel, 1, 0);

        var openButton = new Button { Text = "打开目录", AutoSize = true };
        openButton.Click += (_, _) => OpenLogDirectory();
        panel.Controls.Add(openButton, 1, 1);

        return panel;
    }

    /// <summary>
    /// 底部：保存、清空、重启提示
    /// </summary>
    private Control BuildFooter()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Margin = new Padding(3, 8, 3, 3);
        panel.Controls.Add(_statusLabel, 0, 0);

        var buttonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };

        var clearButton = new Button { Text = "清空所有日志", AutoSize = true };
        clearButton.Click += (_, _) => ClearAllLogs();
        buttonBar.Controls.Add(clearButton);

        var saveButton = new Button { Text = "保存设置", AutoSize = true };
        saveButton.Click += (_, _) => SaveSettings();
        buttonBar.Controls.Add(saveButton);

        panel.Controls.Add(buttonBar, 1, 0);
        return panel;
    }

    /// <summary>
    /// 往设置表里加一行
    /// </summary>
    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 3)
        }, 0, row);

        control.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(control, 1, row);
    }

    /// <summary>
    /// 载入当前配置
    /// </summary>
    private void LoadSettings()
    {
        var config = _configStore.LoadConfig();

        _levelCombo.SelectedItem = config.LogLevel.ToString();
        _recordPayloadsCheckBox.Checked = config.LogRecordPayloads;
        _maxFileSizeBox.Value = Math.Clamp(config.LogMaxFileSizeBytes / (1024 * 1024), 1, 1024);
        _retentionDaysBox.Value = Math.Clamp(config.LogRetentionDays, 1, 365);
        _maxFilesBox.Value = Math.Clamp(config.LogMaxFiles, 1, 1000);

        _directoryLabel.Text = LogFileWriter.DefaultDirectory;
    }

    /// <summary>
    /// 保存设置
    /// </summary>
    private void SaveSettings()
    {
        try
        {
            var config = _configStore.LoadConfig();

            if (Enum.TryParse<LogLevel>(_levelCombo.SelectedItem?.ToString(), out var level))
            {
                config.LogLevel = level;
            }
            config.LogRecordPayloads = _recordPayloadsCheckBox.Checked;
            config.LogMaxFileSizeBytes = (long)_maxFileSizeBox.Value * 1024 * 1024;
            config.LogRetentionDays = (int)_retentionDaysBox.Value;
            config.LogMaxFiles = (int)_maxFilesBox.Value;

            _configStore.SaveConfig(config);

            // GUI 自身的日志立即生效
            AuditLogger.UpdateSettings(LogSettings.FromConfig(config));

            _statusLabel.Text = "已保存。需重启 Claude Code 才会对 MCP 服务端生效。";
            _statusLabel.ForeColor = Color.DarkGreen;
            _onSettingsChanged?.Invoke();
        }
        catch (Exception exception)
        {
            _statusLabel.Text = $"保存失败: {exception.Message}";
            _statusLabel.ForeColor = Color.DarkRed;
            AuditLogger.Error($"保存日志设置失败: {exception}");
        }
    }

    /// <summary>
    /// 清空全部日志文件
    /// </summary>
    private void ClearAllLogs()
    {
        var confirm = MessageBox.Show(
            "确定清空全部日志文件吗？此操作不可恢复。",
            "清空日志",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        try
        {
            var success = AuditLogger.ClearAll();
            if (success)
            {
                _statusLabel.Text = "日志已清空。";
                _statusLabel.ForeColor = Color.DarkGreen;
                _onSettingsChanged?.Invoke();
            }
            else
            {
                _statusLabel.Text = $"清空失败: {AuditLogger.DisabledReason}";
                _statusLabel.ForeColor = Color.DarkRed;
            }
        }
        catch (Exception exception)
        {
            _statusLabel.Text = $"清空失败: {exception.Message}";
            _statusLabel.ForeColor = Color.DarkRed;
        }
    }

    /// <summary>
    /// 打开日志目录
    /// </summary>
    private void OpenLogDirectory()
    {
        try
        {
            var directory = LogFileWriter.DefaultDirectory;
            if (!Directory.Exists(directory))
            {
                MessageBox.Show($"日志目录还不存在：{directory}", "打开失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show($"打开目录失败：{exception.Message}", "打开失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
