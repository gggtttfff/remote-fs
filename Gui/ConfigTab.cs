using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Logging;

namespace RemoteFs.Gui;

/// <summary>
/// 全局配置页：暴露 AppConfig 里除日志以外的全部配置项。
/// 日志相关配置在「日志设置」页单独管理。
/// </summary>
public sealed class ConfigTab : UserControl
{
    private readonly ConfigStore _configStore;

    private readonly TextBox _defaultHostBox = new();
    private readonly CheckBox _requireAllowedRootsCheckBox = new();
    private readonly NumericUpDown _maxReadBytesBox = new();
    private readonly NumericUpDown _maxOutputCharactersBox = new();
    private readonly NumericUpDown _maxListEntriesBox = new();
    private readonly NumericUpDown _maxTreeDepthBox = new();
    private readonly NumericUpDown _maxSearchResultsBox = new();
    private readonly NumericUpDown _connectionTimeoutBox = new();
    private readonly NumericUpDown _sftpOperationTimeoutBox = new();
    private readonly NumericUpDown _connectionIdleSecondsBox = new();
    private readonly ComboBox _remoteExecPolicyCombo = new();
    private readonly CheckBox _followSymbolicLinksCheckBox = new();
    private readonly CheckBox _cacheEnabledCheckBox = new();
    private readonly NumericUpDown _cacheTtlSecondsBox = new();
    private readonly NumericUpDown _backgroundJobTtlBox = new();
    private readonly NumericUpDown _jobRetentionDaysBox = new();
    private readonly NumericUpDown _jobMaxCountBox = new();
    private readonly CheckBox _stayInBackgroundCheckBox = new();
    private readonly CheckBox _startWithWindowsCheckBox = new();
    private readonly Label _statusLabel = new();
    private readonly Label _pathsLabel = new();

    public ConfigTab(ConfigStore configStore)
    {
        _configStore = configStore;
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
            RowCount = 3,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildSections(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        Controls.Add(root);
    }

    /// <summary>
    /// 配置分区
    /// </summary>
    private Control BuildSections()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 0,
            Padding = new Padding(4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 1. 主机与安全
        AddSectionTitle(table, "主机与安全");
        AddRow(table, "默认主机", _defaultHostBox, "工具省略 host 参数时使用的主机别名");
        AddRow(table, "强制路径白名单", _requireAllowedRootsCheckBox,
            "开启后，未配置 allowedRoots 的主机无法访问任何路径");

        _remoteExecPolicyCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _remoteExecPolicyCombo.Width = 180;
        foreach (var policy in Enum.GetValues<RemoteExecPolicy>())
        {
            _remoteExecPolicyCombo.Items.Add(policy.ToString());
        }
        AddRow(table, "远程命令策略", _remoteExecPolicyCombo,
            "Guard 尽力拦截越界路径；Allow 不检查；Deny 直接禁用 remote_exec");
        AddRow(table, "允许软链接", _followSymbolicLinksCheckBox,
            "开启后软链接可被跟随，可能借此访问白名单外的内容");

        // 2. 读取与输出限制
        AddSectionTitle(table, "读取与输出限制");
        AddRow(table, "单文件读取上限(KB)", CreateBytesBox(_maxReadBytesBox, 1024),
            "超过后 remote_read_file 截断，remote_edit_file 直接拒绝");
        AddRow(table, "单次输出上限(字符)", CreateNumberBox(_maxOutputCharactersBox, 1000, 100_000_000),
            "超过后工具输出被截断并提示");
        AddRow(table, "目录列表条目上限", CreateNumberBox(_maxListEntriesBox, 10, 1_000_000),
            "remote_ls 与 remote_tree 的最大展开条数");
        AddRow(table, "目录树最大深度", CreateNumberBox(_maxTreeDepthBox, 1, 100),
            "remote_tree 的深度上限");
        AddRow(table, "搜索结果条数上限", CreateNumberBox(_maxSearchResultsBox, 1, 100_000),
            "remote_search 最多返回的匹配条数");

        // 3. 连接
        AddSectionTitle(table, "连接");
        AddRow(table, "连接超时(ms)", CreateNumberBox(_connectionTimeoutBox, 1000, 600_000),
            "SSH 握手等待的最长时间");
        AddRow(table, "SFTP 操作超时(ms)", CreateNumberBox(_sftpOperationTimeoutBox, 1000, 600_000),
            "单次 SFTP 请求的最长等待");
        AddRow(table, "连接空闲回收(秒)", CreateNumberBox(_connectionIdleSecondsBox, 10, 86_400),
            "SFTP 连接空闲多久后回收，期间复用同一条连接");

        // 4. 缓存
        AddSectionTitle(table, "缓存");
        AddRow(table, "启用目录缓存", _cacheEnabledCheckBox,
            "缓存远程目录枚举结果，减少重复列举的开销");
        AddRow(table, "缓存有效期(秒)", CreateNumberBox(_cacheTtlSecondsBox, 1, 3600),
            "超过后重新枚举；写操作会立即让缓存失效");

        // 5. 远程任务
        AddSectionTitle(table, "远程任务");
        AddRow(table, "后台任务时长(分钟)", CreateNumberBox(_backgroundJobTtlBox, 1, 1_440),
            "background 任务的默认最长运行时间，到期由远程看护进程强制终止");
        AddRow(table, "任务保留天数", CreateNumberBox(_jobRetentionDaysBox, 1, 365),
            "已结束的任务目录超过该天数后自动清理");
        AddRow(table, "每主机任务上限", CreateNumberBox(_jobMaxCountBox, 1, 1_000),
            "每台主机最多保留的已结束任务数，超出后从最旧的开始清理");

        // 6. 程序行为
        AddSectionTitle(table, "程序行为");
        AddRow(table, "关闭窗口驻留后台", _stayInBackgroundCheckBox,
            "开启后点关闭只收进托盘，进程继续驻留；从托盘菜单选「退出」才真正结束");
        AddRow(table, "开机自动启动", _startWithWindowsCheckBox,
            "写入当前用户的启动项，开机后自动打开配置界面，不需要管理员权限");

        // 7. 路径说明
        AddSectionTitle(table, "数据位置");
        _pathsLabel.Dock = DockStyle.Fill;
        _pathsLabel.AutoSize = false;
        _pathsLabel.Height = 46;
        _pathsLabel.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(_pathsLabel, 1, table.RowCount);
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        scroll.Controls.Add(table);
        return scroll;
    }

    /// <summary>
    /// 底部：保存与路径提示
    /// </summary>
    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Margin = new Padding(3, 8, 3, 3);
        footer.Controls.Add(_statusLabel, 0, 0);

        var saveButton = new Button { Text = "保存配置", AutoSize = true };
        saveButton.Click += (_, _) => SaveSettings();

        var buttonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        buttonBar.Controls.Add(saveButton);
        footer.Controls.Add(buttonBar, 1, 0);
        return footer;
    }

    /// <summary>
    /// 加一个分区标题
    /// </summary>
    private static void AddSectionTitle(TableLayoutPanel table, string title)
    {
        var row = table.RowCount;
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
            Margin = new Padding(3, 12, 3, 3)
        };
        table.Controls.Add(label, 0, row);
        table.SetColumnSpan(label, 3);
    }

    /// <summary>
    /// 加一行「标签 + 控件 + 说明」
    /// </summary>
    private static void AddRow(TableLayoutPanel table, string label, Control control, string description)
    {
        var row = table.RowCount;
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 3)
        }, 0, row);

        control.Margin = new Padding(3, 4, 3, 4);
        if (control is CheckBox checkBox)
        {
            checkBox.AutoSize = true;
        }
        else
        {
            control.Dock = DockStyle.Fill;
            control.Width = 180;
        }
        table.Controls.Add(control, 1, row);

        table.Controls.Add(new Label
        {
            Text = description,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 8, 3, 3)
        }, 2, row);
    }

    /// <summary>
    /// 以 KB 为单位配置字节数
    /// </summary>
    private static NumericUpDown CreateBytesBox(NumericUpDown box, int divisor)
    {
        box.Minimum = 1;
        box.Maximum = 1_000_000;
        box.Increment = 64;
        box.ThousandsSeparator = true;
        box.Tag = divisor;
        return box;
    }

    /// <summary>
    /// 普通的数字输入框
    /// </summary>
    private static NumericUpDown CreateNumberBox(NumericUpDown box, int minimum, int maximum)
    {
        box.Minimum = minimum;
        box.Maximum = maximum;
        box.ThousandsSeparator = true;
        return box;
    }

    /// <summary>
    /// 载入当前配置
    /// </summary>
    private void LoadSettings()
    {
        var config = _configStore.LoadConfig();

        _defaultHostBox.Text = config.DefaultHost ?? string.Empty;
        _requireAllowedRootsCheckBox.Checked = config.RequireAllowedRoots;
        _maxReadBytesBox.Value = Math.Clamp(config.MaxReadBytes / 1024, 1, 1_000_000);
        _maxOutputCharactersBox.Value = Math.Clamp(config.MaxOutputCharacters, 1000, 100_000_000);
        _maxListEntriesBox.Value = Math.Clamp(config.MaxListEntries, 10, 1_000_000);
        _maxTreeDepthBox.Value = Math.Clamp(config.MaxTreeDepth, 1, 100);
        _maxSearchResultsBox.Value = Math.Clamp(config.MaxSearchResults, 1, 100_000);
        _connectionTimeoutBox.Value = Math.Clamp(config.ConnectionTimeoutMilliseconds, 1000, 600_000);
        _sftpOperationTimeoutBox.Value = Math.Clamp(config.SftpOperationTimeoutMilliseconds, 1000, 600_000);
        _connectionIdleSecondsBox.Value = Math.Clamp(config.ConnectionIdleSeconds, 10, 86_400);
        _remoteExecPolicyCombo.SelectedItem = config.RemoteExecPolicy.ToString();
        _followSymbolicLinksCheckBox.Checked = config.FollowSymbolicLinks;
        _cacheEnabledCheckBox.Checked = config.CacheEnabled;
        _cacheTtlSecondsBox.Value = Math.Clamp(config.CacheTtlSeconds, 1, 3600);
        _backgroundJobTtlBox.Value = Math.Clamp(config.BackgroundJobTtlMinutes, 1, 1_440);
        _jobRetentionDaysBox.Value = Math.Clamp(config.JobRetentionDays, 1, 365);
        _jobMaxCountBox.Value = Math.Clamp(config.JobMaxCount, 1, 1_000);
        _stayInBackgroundCheckBox.Checked = config.StayInBackgroundOnExit;

        // 开机启动以注册表为准，不信任配置文件里的镜像
        _startWithWindowsCheckBox.Checked = WindowsStartup.IsEnabled();

        _pathsLabel.Text =
            $"配置目录：{ConfigStore.ConfigDirectory}{Environment.NewLine}"
            + $"缓存目录：{ConfigStore.CacheDirectory}{Environment.NewLine}"
            + $"日志目录：{LogFileWriter.DefaultDirectory}";
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    private void SaveSettings()
    {
        try
        {
            var config = _configStore.LoadConfig();

            config.DefaultHost = string.IsNullOrWhiteSpace(_defaultHostBox.Text)
                ? null
                : _defaultHostBox.Text.Trim();
            config.RequireAllowedRoots = _requireAllowedRootsCheckBox.Checked;
            config.MaxReadBytes = (int)((long)_maxReadBytesBox.Value * 1024);
            config.MaxOutputCharacters = (int)_maxOutputCharactersBox.Value;
            config.MaxListEntries = (int)_maxListEntriesBox.Value;
            config.MaxTreeDepth = (int)_maxTreeDepthBox.Value;
            config.MaxSearchResults = (int)_maxSearchResultsBox.Value;
            config.ConnectionTimeoutMilliseconds = (int)_connectionTimeoutBox.Value;
            config.SftpOperationTimeoutMilliseconds = (int)_sftpOperationTimeoutBox.Value;
            config.ConnectionIdleSeconds = (int)_connectionIdleSecondsBox.Value;

            if (Enum.TryParse<RemoteExecPolicy>(_remoteExecPolicyCombo.SelectedItem?.ToString(), out var policy))
            {
                config.RemoteExecPolicy = policy;
            }

            config.FollowSymbolicLinks = _followSymbolicLinksCheckBox.Checked;
            config.CacheEnabled = _cacheEnabledCheckBox.Checked;
            config.CacheTtlSeconds = (int)_cacheTtlSecondsBox.Value;
            config.BackgroundJobTtlMinutes = (int)_backgroundJobTtlBox.Value;
            config.JobRetentionDays = (int)_jobRetentionDaysBox.Value;
            config.JobMaxCount = (int)_jobMaxCountBox.Value;
            config.StayInBackgroundOnExit = _stayInBackgroundCheckBox.Checked;

            _configStore.SaveConfig(config);

            // 开机启动项跟配置文件分开处理，注册表才是事实来源
            ApplyStartupSetting();

            _statusLabel.Text = "已保存。需重启 Claude Code 才会对 MCP 服务端生效。";
            _statusLabel.ForeColor = Color.DarkGreen;
            AuditLogger.Info("全局配置已更新");
        }
        catch (Exception exception)
        {
            _statusLabel.Text = $"保存失败: {exception.Message}";
            _statusLabel.ForeColor = Color.DarkRed;
            AuditLogger.Error($"保存全局配置失败: {exception}");
        }
    }

    /// <summary>
    /// 按勾选状态同步开机启动项，失败时把勾选状态还原成注册表的真实值
    /// </summary>
    private void ApplyStartupSetting()
    {
        var wanted = _startWithWindowsCheckBox.Checked;
        var current = WindowsStartup.IsEnabled();
        if (wanted == current)
        {
            return;
        }

        var success = wanted
            ? WindowsStartup.Enable(out var enableError)
            : WindowsStartup.Disable(out enableError);

        if (success)
        {
            return;
        }

        // 兜底：写注册表失败要如实告知，不能显示保存成功
        _statusLabel.Text = $"开机启动设置失败：{enableError}";
        _statusLabel.ForeColor = Color.DarkRed;
        _startWithWindowsCheckBox.Checked = WindowsStartup.IsEnabled();
    }
}