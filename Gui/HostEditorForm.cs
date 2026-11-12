using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Ssh;

namespace RemoteFs.Gui;

/// <summary>
/// 主机配置编辑器：左侧主机列表，右侧逐项编辑，支持指纹探测与连接测试。
/// </summary>
public sealed class HostEditorForm : Form
{
    /// <summary>下拉框中「跟随全局配置」这一项的显示文本，其对应的实际编码值为 null</summary>
    private const string FollowGlobalEncodingText = "（跟随全局配置）";

    private readonly ConfigStore _configStore;
    private readonly AppConfig _appConfig;
    private readonly List<HostProfile> _hosts;
    private readonly bool _readOnly;

    // 左侧列表
    private readonly ListBox _hostList = new();
    private readonly Button _addButton = new() { Text = "添加" };
    private readonly Button _importButton = new() { Text = "从 ssh_config 导入" };
    private readonly Button _removeButton = new() { Text = "删除" };

    // 右侧编辑区
    private readonly TextBox _nameBox = new();
    private readonly TextBox _hostBox = new();
    private readonly NumericUpDown _portBox = new();
    private readonly TextBox _usernameBox = new();
    private readonly RadioButton _authAuto = new() { Text = "自动", AutoSize = true };
    private readonly RadioButton _authKey = new() { Text = "密钥", AutoSize = true };
    private readonly RadioButton _authPassword = new() { Text = "密码", AutoSize = true };
    private readonly TextBox _privateKeyPathBox = new();
    private readonly Button _browseKeyButton = new() { Text = "浏览…" };
    private readonly TextBox _passphraseBox = new() { UseSystemPasswordChar = true };
    private readonly TextBox _passwordBox = new() { UseSystemPasswordChar = true };
    private readonly TextBox _passwordEnvironmentVariableBox = new();
    private readonly TextBox _fingerprintBox = new();
    private readonly CheckBox _strictHostKeyCheck = new() { Text = "强制校验（指纹不一致时拒绝连接）", AutoSize = true };
    private readonly TextBox _allowedRootsBox = new();
    private readonly NumericUpDown _commandTimeoutBox = new();
    private readonly ComboBox _encodingCombo = new();
    private readonly TextBox _descriptionBox = new();
    private readonly Label _banner = new();
    private readonly Label _statusLabel = new();

    // 底部按钮
    private readonly Button _probeButton = new() { Text = "探测指纹" };
    private readonly Button _testButton = new() { Text = "测试连接" };
    private readonly Button _generateConfigButton = new() { Text = "生成 MCP 配置" };
    private readonly Button _saveButton = new() { Text = "保存" };

    private int _currentIndex = -1;
    private bool _loading;
    private bool _dirty;
    private HostProfile? _pendingNewHost;
    private NotifyIcon? _trayIcon;
    private bool _allowExit;
    private bool _trayHintShown;

    public HostEditorForm()
    {
        _configStore = new ConfigStore();
        _appConfig = _configStore.LoadConfig();

        // 1.1 加载主机配置；解析失败时进入只读模式，绝不覆盖已损坏的文件
        List<HostProfile> loadedHosts;
        try
        {
            loadedHosts = _configStore.LoadHosts();
        }
        catch (Exception exception)
        {
            AuditLogger.Error($"GUI 加载主机配置失败: {exception.Message}");
            loadedHosts = new List<HostProfile>();
            _readOnly = true;
            AuditLogger.Warn("GUI 已切换到只读模式，本次修改不会落盘");
        }
        _hosts = loadedHosts;

        // 1.2 构建界面并渲染首台主机
        InitializeWindow();
        BuildLayout();
        BindEvents();
        InitializeTray();
        RebuildHostList(0);
    }

    /// <summary>
    /// 托盘图标：配置里开启驻留后台后，关窗口只是收进托盘
    /// </summary>
    private void InitializeTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "remote-fs 主机配置",
            Visible = false,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    /// <summary>
    /// 收进托盘
    /// </summary>
    private void HideToTray()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = true;
        Hide();

        // 只在第一次提示，避免每次关闭都弹
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(
                3000, "remote-fs", "已最小化到托盘，双击图标可重新打开", ToolTipIcon.Info);
        }

        AuditLogger.Info("窗口已收到托盘，进程继续驻留");
    }

    /// <summary>
    /// 从托盘恢复窗口
    /// </summary>
    private void RestoreFromTray()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 真正退出程序
    /// </summary>
    private void ExitApplication()
    {
        _allowExit = true;
        Close();
    }

    /// <summary>
    /// 窗口基础属性
    /// </summary>
    private void InitializeWindow()
    {
        Text = "remote-fs 主机配置";
        Width = 940;
        Height = 640;
        MinimumSize = new Size(780, 540);
        StartPosition = FormStartPosition.CenterScreen;

        _portBox.Minimum = 1;
        _portBox.Maximum = 65535;
        _portBox.Value = 22;

        _commandTimeoutBox.Minimum = 1_000;
        _commandTimeoutBox.Maximum = 600_000;
        _commandTimeoutBox.Increment = 1_000;
        _commandTimeoutBox.Value = 60_000;

        _encodingCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        RefreshEncodingItems(null);

        _authAuto.Checked = true;
        _strictHostKeyCheck.Checked = true;

        _hostList.IntegralHeight = false;
        _hostList.Dock = DockStyle.Fill;

        _banner.AutoSize = false;
        _banner.Height = 34;
        _banner.Dock = DockStyle.Fill;
        _banner.TextAlign = ContentAlignment.MiddleLeft;
        _banner.Padding = new Padding(8, 0, 8, 0);
        _banner.BackColor = Color.FromArgb(255, 246, 214);
        _banner.Visible = false;

        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Margin = new Padding(3, 8, 3, 3);
    }

    /// <summary>
    /// 组装整体布局：标签页容器
    /// </summary>
    private void BuildLayout()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 6)
        };

        var hostTab = new TabPage("主机配置");
        hostTab.Padding = new Padding(8);
        hostTab.Controls.Add(BuildHostLayout());

        var logTab = new TabPage("日志");
        logTab.Padding = new Padding(4);
        logTab.Controls.Add(new LogViewTab(_configStore));

        var settingsTab = new TabPage("日志设置");
        settingsTab.Padding = new Padding(4);
        settingsTab.Controls.Add(new LogSettingsTab(_configStore, () =>
        {
            AuditLogger.Warn("日志设置已更新");
        }));

        var configTab = new TabPage("配置");
        configTab.Padding = new Padding(4);
        configTab.Controls.Add(new ConfigTab(_configStore));

        tabs.TabPages.Add(hostTab);
        tabs.TabPages.Add(logTab);
        tabs.TabPages.Add(settingsTab);
        tabs.TabPages.Add(configTab);

        Controls.Add(tabs);
    }

    /// <summary>
    /// 主机配置页：顶部提示条 + 左右分栏 + 底部操作栏
    /// </summary>
    private Control BuildHostLayout()
    {
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

        root.Controls.Add(_banner, 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        return root;
    }

    /// <summary>
    /// 左右分栏主体
    /// </summary>
    private Control BuildBody()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        body.Controls.Add(BuildHostListPanel(), 0, 0);
        body.Controls.Add(BuildEditorPanel(), 1, 0);
        return body;
    }

    /// <summary>
    /// 左侧：主机列表 + 增删按钮
    /// </summary>
    private Control BuildHostListPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 8, 0)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0)
        };
        buttonBar.Controls.Add(_addButton);
        buttonBar.Controls.Add(_importButton);
        buttonBar.Controls.Add(_removeButton);

        panel.Controls.Add(_hostList, 0, 0);
        panel.Controls.Add(buttonBar, 0, 1);
        return panel;
    }

    /// <summary>
    /// 右侧：可滚动的字段编辑区
    /// </summary>
    private Control BuildEditorPanel()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 0,
            Padding = new Padding(4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddEditorRow(table, "别名", _nameBox);
        AddEditorRow(table, "主机地址", _hostBox);
        AddEditorRow(table, "端口", _portBox);
        AddEditorRow(table, "用户名", _usernameBox);
        AddEditorRow(table, "认证方式", BuildAuthSelector());
        AddEditorRow(table, "私钥路径", BuildPrivateKeySelector());
        AddEditorRow(table, "私钥口令", _passphraseBox);
        AddEditorRow(table, "登录密码", _passwordBox);
        AddEditorRow(table, "密码环境变量", _passwordEnvironmentVariableBox);
        AddEditorRow(table, "主机指纹", BuildFingerprintSelector());
        AddEditorRow(table, string.Empty, _strictHostKeyCheck);
        AddEditorRow(table, "允许根目录", _allowedRootsBox);
        AddEditorRow(table, "命令超时(ms)", _commandTimeoutBox);
        AddEditorRow(table, "默认编码", _encodingCombo);
        AddEditorRow(table, "备注", _descriptionBox);

        scroll.Controls.Add(table);
        return scroll;
    }

    /// <summary>
    /// 认证方式单选组
    /// </summary>
    private Control BuildAuthSelector()
    {
        var group = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
        group.Controls.Add(_authAuto);
        group.Controls.Add(_authKey);
        group.Controls.Add(_authPassword);
        return group;
    }

    /// <summary>
    /// 私钥路径输入框 + 浏览按钮
    /// </summary>
    private Control BuildPrivateKeySelector()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _privateKeyPathBox.Dock = DockStyle.Fill;
        _browseKeyButton.AutoSize = true;
        _browseKeyButton.Margin = new Padding(4, 0, 0, 0);
        layout.Controls.Add(_privateKeyPathBox, 0, 0);
        layout.Controls.Add(_browseKeyButton, 1, 0);
        return layout;
    }

    /// <summary>
    /// 指纹输入框 + 探测按钮
    /// </summary>
    private Control BuildFingerprintSelector()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _fingerprintBox.Dock = DockStyle.Fill;
        _probeButton.AutoSize = true;
        _probeButton.Margin = new Padding(4, 0, 0, 0);
        layout.Controls.Add(_fingerprintBox, 0, 0);
        layout.Controls.Add(_probeButton, 1, 0);
        return layout;
    }

    /// <summary>
    /// 底部：状态提示 + 主要操作
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

        var buttonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = Padding.Empty
        };
        buttonBar.Controls.Add(_saveButton);
        buttonBar.Controls.Add(_testButton);
        buttonBar.Controls.Add(_generateConfigButton);

        footer.Controls.Add(_statusLabel, 0, 0);
        footer.Controls.Add(buttonBar, 1, 0);
        return footer;
    }

    /// <summary>
    /// 向编辑表追加一行「标签 + 控件」
    /// </summary>
    private static void AddEditorRow(TableLayoutPanel table, string label, Control control)
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

        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(control, 1, row);
    }

    /// <summary>
    /// 绑定全部交互事件
    /// </summary>
    private void BindEvents()
    {
        _hostList.SelectedIndexChanged += OnHostSelectionChanged;
        _addButton.Click += OnAddHost;
        _importButton.Click += OnImportFromSshConfig;
        _removeButton.Click += OnRemoveHost;
        _saveButton.Click += (_, _) => SaveCurrent(silent: false);
        _browseKeyButton.Click += OnBrowsePrivateKey;
        _probeButton.Click += OnProbeFingerprint;
        _testButton.Click += OnTestConnection;
        _generateConfigButton.Click += OnGenerateMcpConfig;
        FormClosing += OnFormClosing;

        // 任一字段变化都标记为脏，切换主机或关闭窗口时提示保存
        foreach (var control in TrackedInputs())
        {
            switch (control)
            {
                case TextBox textBox:
                    textBox.TextChanged += MarkDirty;
                    break;
                case CheckBox checkBox:
                    checkBox.CheckedChanged += MarkDirty;
                    break;
                case NumericUpDown numericUpDown:
                    numericUpDown.ValueChanged += MarkDirty;
                    break;
                case RadioButton radioButton:
                    radioButton.CheckedChanged += MarkDirty;
                    break;
                case ComboBox comboBox:
                    comboBox.SelectedIndexChanged += MarkDirty;
                    break;
            }
        }
    }

    /// <summary>
    /// 需要参与脏标记的输入控件
    /// </summary>
    private IEnumerable<Control> TrackedInputs()
    {
        yield return _nameBox;
        yield return _hostBox;
        yield return _portBox;
        yield return _usernameBox;
        yield return _authAuto;
        yield return _authKey;
        yield return _authPassword;
        yield return _privateKeyPathBox;
        yield return _passphraseBox;
        yield return _passwordBox;
        yield return _passwordEnvironmentVariableBox;
        yield return _fingerprintBox;
        yield return _strictHostKeyCheck;
        yield return _allowedRootsBox;
        yield return _commandTimeoutBox;
        yield return _encodingCombo;
        yield return _descriptionBox;
    }

    private void MarkDirty(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _dirty = true;
    }

    /// <summary>
    /// 重新渲染主机列表，并选中指定下标的条目
    /// </summary>
    private void RebuildHostList(int selectIndex)
    {
        _loading = true;
        try
        {
            _hostList.BeginUpdate();
            _hostList.Items.Clear();
            foreach (var host in _hosts)
            {
                _hostList.Items.Add(DescribeHost(host));
            }
            _hostList.EndUpdate();

            if (_hosts.Count == 0)
            {
                _currentIndex = -1;
                ClearForm();
            }
            else
            {
                _currentIndex = Math.Clamp(selectIndex, 0, _hosts.Count - 1);
                _hostList.SelectedIndex = _currentIndex;
                LoadHostToForm(_hosts[_currentIndex]);
            }
        }
        finally
        {
            _loading = false;
            _dirty = false;
        }
        UpdateButtonState();
    }

    private static string DescribeHost(HostProfile host)
        => $"{host.Name}  ({host.Username}@{host.Host}:{host.Port})";

    /// <summary>
    /// 主机配置回填到表单
    /// </summary>
    private void LoadHostToForm(HostProfile profile)
    {
        _nameBox.Text = profile.Name;
        _hostBox.Text = profile.Host;
        _portBox.Value = Math.Clamp(profile.Port, 1, 65535);
        _usernameBox.Text = profile.Username;
        _authAuto.Checked = profile.Auth == AuthMethod.Auto;
        _authKey.Checked = profile.Auth == AuthMethod.Key;
        _authPassword.Checked = profile.Auth == AuthMethod.Password;
        _privateKeyPathBox.Text = profile.PrivateKeyPath ?? string.Empty;
        _passphraseBox.Text = profile.PrivateKeyPassphrase ?? string.Empty;
        _passwordBox.Text = profile.Password ?? string.Empty;
        _passwordEnvironmentVariableBox.Text = profile.PasswordEnvironmentVariable ?? string.Empty;
        _fingerprintBox.Text = profile.KnownHostKeyFingerprint ?? string.Empty;
        _strictHostKeyCheck.Checked = profile.StrictHostKeyChecking;
        _allowedRootsBox.Text = string.Join(", ", profile.AllowedRoots);
        _commandTimeoutBox.Value = Math.Clamp(profile.CommandTimeoutMilliseconds, 1_000, 600_000);
        _descriptionBox.Text = profile.Description ?? string.Empty;

        RefreshEncodingItems(profile.DefaultEncoding);
        _encodingCombo.SelectedItem = ResolveEncodingSelection(profile.DefaultEncoding);
    }

    /// <summary>
    /// 重建编码下拉框的候选项：跟随全局 + 全部受支持的编码
    /// </summary>
    /// <param name="currentEncoding">当前主机配置的编码名，用于补进非标准的候选值</param>
    private void RefreshEncodingItems(string? currentEncoding)
    {
        _encodingCombo.Items.Clear();
        _encodingCombo.Items.Add(FollowGlobalEncodingText);
        foreach (var encodingName in TextEncodingResolver.SupportedNames)
        {
            _encodingCombo.Items.Add(encodingName);
        }

        // 兜底：历史配置里的非标准编码名补进候选项，避免保存时被悄悄改写
        if (!string.IsNullOrWhiteSpace(currentEncoding))
        {
            var normalized = TextEncodingResolver.Normalize(currentEncoding);
            if (!_encodingCombo.Items.Contains(normalized))
            {
                _encodingCombo.Items.Add(normalized);
            }
        }
    }

    /// <summary>
    /// 选出下拉框要显示的项；主机未单独配置编码时显示「跟随全局配置」
    /// </summary>
    private static string ResolveEncodingSelection(string? encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return FollowGlobalEncodingText;
        }
        return TextEncodingResolver.Normalize(encodingName);
    }

    /// <summary>
    /// 取下拉框对应的实际编码值；「跟随全局配置」对应 null
    /// </summary>
    private string? ResolveEncodingValue()
    {
        var selected = _encodingCombo.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selected) || selected == FollowGlobalEncodingText)
        {
            return null;
        }
        return TextEncodingResolver.Normalize(selected);
    }

    /// <summary>
    /// 表单内容组装成主机配置
    /// </summary>
    private HostProfile BuildProfileFromForm()
    {
        return new HostProfile
        {
            Name = _nameBox.Text.Trim(),
            Host = _hostBox.Text.Trim(),
            Port = (int)_portBox.Value,
            Username = _usernameBox.Text.Trim(),
            Auth = _authKey.Checked
                ? AuthMethod.Key
                : _authPassword.Checked ? AuthMethod.Password : AuthMethod.Auto,
            PrivateKeyPath = NullIfBlank(_privateKeyPathBox.Text),
            PrivateKeyPassphrase = NullIfBlank(_passphraseBox.Text),
            Password = NullIfBlank(_passwordBox.Text),
            PasswordEnvironmentVariable = NullIfBlank(_passwordEnvironmentVariableBox.Text),
            StrictHostKeyChecking = _strictHostKeyCheck.Checked,
            KnownHostKeyFingerprint = NullIfBlank(_fingerprintBox.Text),
            AllowedRoots = ParseAllowedRoots(_allowedRootsBox.Text),
            CommandTimeoutMilliseconds = (int)_commandTimeoutBox.Value,
            DefaultEncoding = ResolveEncodingValue(),
            Description = NullIfBlank(_descriptionBox.Text)
        };
    }

    /// <summary>
    /// 清空编辑区
    /// </summary>
    private void ClearForm()
    {
        _nameBox.Text = string.Empty;
        _hostBox.Text = string.Empty;
        _portBox.Value = 22;
        _usernameBox.Text = string.Empty;
        _authAuto.Checked = true;
        _privateKeyPathBox.Text = string.Empty;
        _passphraseBox.Text = string.Empty;
        _passwordBox.Text = string.Empty;
        _passwordEnvironmentVariableBox.Text = string.Empty;
        _fingerprintBox.Text = string.Empty;
        _strictHostKeyCheck.Checked = true;
        _allowedRootsBox.Text = string.Empty;
        _commandTimeoutBox.Value = 60_000;
        RefreshEncodingItems(null);
        _encodingCombo.SelectedItem = FollowGlobalEncodingText;
        _descriptionBox.Text = string.Empty;
    }

    /// <summary>
    /// 按当前选中状态刷新按钮可用性
    /// </summary>
    private void UpdateButtonState()
    {
        var hasSelection = _currentIndex >= 0 && _currentIndex < _hosts.Count;
        var editable = hasSelection && !_readOnly;

        _saveButton.Enabled = editable;
        _addButton.Enabled = !_readOnly;
        _removeButton.Enabled = editable;
        _testButton.Enabled = hasSelection;
        _probeButton.Enabled = hasSelection;
        // 生成配置片段只是展示内容，不受只读模式与选中状态限制
        _generateConfigButton.Enabled = true;

        foreach (var control in TrackedInputs())
        {
            control.Enabled = editable;
        }
        _browseKeyButton.Enabled = editable;

        if (_readOnly)
        {
            _banner.Visible = true;
            _banner.Text = "主机配置文件解析失败，已切换为只读模式。请先修复 hosts.json 后重新打开。";
        }
    }

    /// <summary>
    /// 切换主机：先处理当前未保存的修改
    /// </summary>
    private void OnHostSelectionChanged(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var targetIndex = _hostList.SelectedIndex;
        if (targetIndex < 0 || targetIndex >= _hosts.Count)
        {
            return;
        }

        // 1.1 选中的还是当前主机，无需处理
        var targetHost = _hosts[targetIndex];
        if (_currentIndex >= 0 && _currentIndex < _hosts.Count && ReferenceEquals(targetHost, _hosts[_currentIndex]))
        {
            return;
        }

        // 1.2 未保存则询问；用户取消时回退选中项
        if (!TryLeaveCurrent())
        {
            RestoreSelection();
            return;
        }

        // 1.3 丢弃新建主机后索引会偏移，按对象重新定位
        _currentIndex = _hosts.IndexOf(targetHost);
        _loading = true;
        if (_currentIndex >= 0)
        {
            _hostList.SelectedIndex = _currentIndex;
            LoadHostToForm(_hosts[_currentIndex]);
        }
        _loading = false;
        _dirty = false;
        UpdateButtonState();
        SetStatus(null);
    }

    /// <summary>
    /// 离开当前主机前的确认流程
    /// </summary>
    private bool TryLeaveCurrent()
    {
        if (!_dirty)
        {
            return true;
        }

        var answer = MessageBox.Show(
            this,
            "当前主机配置有未保存的修改，是否保存？",
            "未保存的修改",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (answer == DialogResult.Cancel)
        {
            return false;
        }
        if (answer == DialogResult.Yes)
        {
            return SaveCurrent(silent: true);
        }

        // 放弃修改时清理尚未落盘的新建主机，避免空配置被后续保存带入磁盘
        DropPendingNewHost();
        return true;
    }

    /// <summary>
    /// 清掉尚未保存的新建主机
    /// </summary>
    private void DropPendingNewHost()
    {
        if (_pendingNewHost is null)
        {
            return;
        }
        _hosts.Remove(_pendingNewHost);
        _pendingNewHost = null;
        _dirty = false;
    }

    /// <summary>
    /// 回退列表选中项到当前主机
    /// </summary>
    private void RestoreSelection()
    {
        _loading = true;
        if (_currentIndex >= 0 && _currentIndex < _hosts.Count)
        {
            _hostList.SelectedIndex = _currentIndex;
        }
        _loading = false;
    }

    /// <summary>
    /// 保存当前编辑的主机
    /// </summary>
    private bool SaveCurrent(bool silent)
    {
        if (_readOnly)
        {
            MessageBox.Show(this, "当前为只读模式，无法保存。", "只读模式",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (_currentIndex < 0 || _currentIndex >= _hosts.Count)
        {
            return false;
        }

        // 1.1 校验表单，不通过则不落盘
        var candidate = BuildProfileFromForm();
        var errors = HostProfileValidator.Validate(candidate, _hosts, _hosts[_currentIndex]);
        if (errors.Count > 0)
        {
            AuditLogger.Warn($"GUI 保存校验未通过 [{candidate.Name}]: {string.Join("; ", errors)}");
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "无法保存",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // 1.2 写回内存并落盘
        _hosts[_currentIndex] = candidate;
        try
        {
            _configStore.SaveHosts(_hosts);
        }
        catch (Exception exception)
        {
            AuditLogger.Error($"GUI 保存主机配置失败: {exception.Message}");
            MessageBox.Show(this, $"保存失败：{exception.Message}", "保存失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        // 1.3 刷新列表并复位脏标记
        _pendingNewHost = null;
        var index = _currentIndex;
        RebuildHostList(index);
        if (!silent)
        {
            SetStatus($"已保存主机 {candidate.Name}");
        }
        return true;
    }

    /// <summary>
    /// 新增主机，先创建内存条目，保存后才落盘
    /// </summary>
    private void OnAddHost(object? sender, EventArgs e)
    {
        if (!TryLeaveCurrent())
        {
            return;
        }

        var profile = new HostProfile
        {
            Name = GenerateUniqueName(),
            Host = string.Empty,
            Port = 22,
            Username = "root",
            Auth = AuthMethod.Auto,
            StrictHostKeyChecking = true,
            CommandTimeoutMilliseconds = 60_000
        };

        _hosts.Add(profile);
        _pendingNewHost = profile;
        RebuildHostList(_hosts.Count - 1);
        _dirty = true;
        SetStatus($"已创建主机 {profile.Name}，填写完整后点击保存");
    }

    /// <summary>
    /// 删除当前主机，立即落盘
    /// </summary>
    private void OnRemoveHost(object? sender, EventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _hosts.Count)
        {
            return;
        }

        var profile = _hosts[_currentIndex];
        var confirm = MessageBox.Show(
            this,
            $"确定删除主机 {profile.Name} 吗？该操作会立即写入配置文件。",
            "删除主机",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _hosts.RemoveAt(_currentIndex);
        if (ReferenceEquals(_pendingNewHost, profile))
        {
            _pendingNewHost = null;
        }

        try
        {
            _configStore.SaveHosts(_hosts);
        }
        catch (Exception exception)
        {
            // 兜底：删除后写盘失败，提示用户配置可能未同步
            AuditLogger.Error($"GUI 删除主机后保存失败 [{profile.Name}]: {exception.Message}");
            MessageBox.Show(this, $"删除后保存配置失败：{exception.Message}", "保存失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        AuditLogger.Info($"GUI 删除主机 {profile.Name}");
        _currentIndex = -1;
        RebuildHostList(0);
        SetStatus($"已删除主机 {profile.Name}");
    }

    /// <summary>
    /// 从 ~/.ssh/config 导入主机
    /// </summary>
    private void OnImportFromSshConfig(object? sender, EventArgs e)
    {
        try
        {
            using var dialog = new SshConfigImportDialog(_configStore);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            RebuildHostList(0);
            SetStatus("已从 ssh_config 导入主机");
        }
        catch (Exception exception)
        {
            AuditLogger.Error($"从 ssh_config 导入失败: {exception}");
            MessageBox.Show($"导入失败：{exception.Message}", "导入失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 选择私钥文件
    /// </summary>
    private void OnBrowsePrivateKey(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 SSH 私钥文件",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _privateKeyPathBox.Text = dialog.FileName;
        }
    }

    /// <summary>
    /// 连接一次服务器，取回主机密钥指纹
    /// </summary>
    private async void OnProbeFingerprint(object? sender, EventArgs e)
    {
        var probe = BuildProfileFromForm();
        // 探测阶段不做指纹比对，否则待验证的值会直接导致连接被拒
        probe.KnownHostKeyFingerprint = null;
        probe.StrictHostKeyChecking = false;

        SetStatus("正在探测主机指纹…");
        await RunRemoteActionAsync(
            () =>
            {
                var factory = new SshConnectionFactory(_appConfig);
                using var client = factory.CreateSshClient(probe, out var fingerprint);
                return fingerprint;
            },
            fingerprint =>
            {
                _fingerprintBox.Text = fingerprint;
                _dirty = true;
                SetStatus("指纹已填入，记得点击保存");
            },
            "探测指纹失败");
    }

    /// <summary>
    /// 测试当前配置能否连上
    /// </summary>
    private async void OnTestConnection(object? sender, EventArgs e)
    {
        var probe = BuildProfileFromForm();
        probe.KnownHostKeyFingerprint = null;
        probe.StrictHostKeyChecking = false;

        SetStatus("正在测试连接…");
        await RunRemoteActionAsync(
            () =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var factory = new SshConnectionFactory(_appConfig);
                using var client = factory.CreateSshClient(probe, out var fingerprint);
                stopwatch.Stop();
                return $"握手耗时 {stopwatch.ElapsedMilliseconds} 毫秒{Environment.NewLine}"
                    + $"服务器版本 {client.ConnectionInfo.ServerVersion}{Environment.NewLine}"
                    + $"主机密钥指纹 {fingerprint}";
            },
            message =>
            {
                SetStatus("连接成功");
                MessageBox.Show(this, message, "连接成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            },
            "连接测试失败");
    }

    /// <summary>
    /// 展示可直接粘贴的 MCP 配置片段，不修改任何外部配置文件
    /// </summary>
    private void OnGenerateMcpConfig(object? sender, EventArgs e)
    {
        McpConfigDialog.ShowSnippet(this);
    }

    /// <summary>
    /// 在后台执行远程操作，期间锁定按钮，结果通过回调回到界面线程
    /// </summary>
    private async Task RunRemoteActionAsync<T>(
        Func<T> action, Action<T> onSuccess, string failureTitle)
    {
        SetInteractionEnabled(false);
        Cursor = Cursors.WaitCursor;
        try
        {
            var result = await Task.Run(action);
            onSuccess(result);
        }
        catch (Exception exception)
        {
            // 兜底：连接类异常统一提示，避免窗口直接崩掉
            AuditLogger.Warn($"GUI 远程操作失败: {exception.Message}");
            SetStatus(null);
            MessageBox.Show(this, exception.Message, failureTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetInteractionEnabled(true);
            Cursor = Cursors.Default;
        }
    }

    /// <summary>
    /// 远程操作期间禁用会引发状态变化的按钮
    /// </summary>
    private void SetInteractionEnabled(bool enabled)
    {
        if (enabled)
        {
            UpdateButtonState();
            return;
        }
        _saveButton.Enabled = false;
        _addButton.Enabled = false;
        _removeButton.Enabled = false;
        _testButton.Enabled = false;
        _probeButton.Enabled = false;
    }

    /// <summary>
    /// 关闭窗口前处理未保存的修改
    /// </summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 1. 开启了驻留后台时，点关闭只收进托盘，不退出进程
        if (!_allowExit
            && _appConfig.StayInBackgroundOnExit
            && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (!TryLeaveCurrent())
        {
            e.Cancel = true;
            // 取消退出后要复位，否则下次点关闭会直接退出
            _allowExit = false;
        }
    }

    /// <summary>
    /// 窗口真正关闭时释放托盘图标
    /// </summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        base.OnFormClosed(e);
    }

    private void SetStatus(string? text)
    {
        _statusLabel.Text = text ?? string.Empty;
    }

    /// <summary>
    /// 逗号分隔的根目录转成规范化列表
    /// </summary>
    private static List<string> ParseAllowedRoots(string text)
    {
        return text
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Select(PathGuard.Normalize)
            .ToList();
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 生成不与现有主机冲突的默认别名
    /// </summary>
    private string GenerateUniqueName()
    {
        for (var index = 1; index <= 999; index++)
        {
            var candidate = $"host{index}";
            if (_hosts.All(item => !string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
        return "host-" + Guid.NewGuid().ToString("N")[..8];
    }
}