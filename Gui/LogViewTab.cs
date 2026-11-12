using System.Diagnostics;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Logging;

namespace RemoteFs.Gui;

/// <summary>
/// 日志查看页：虚拟列表实时跟踪日志文件，支持等级、关键词和失败过滤，以及详情查看。
/// </summary>
public sealed class LogViewTab : UserControl
{
    /// <summary>内存中最多保留的日志条数</summary>
    private const int MaxEntries = 20_000;

    /// <summary>打开时加载的历史日志字节数</summary>
    private const long TailBytes = 5 * 1024 * 1024;

    private static readonly (string Label, LogLevel Level)[] LevelOptions =
    [
        ("调试及以上", LogLevel.Debug),
        ("信息及以上", LogLevel.Info),
        ("警告及以上", LogLevel.Warn),
        ("仅错误", LogLevel.Error)
    ];

    private LogFileReader _reader;
    private readonly List<LogDisplayRow> _all = new();
    private readonly List<LogDisplayRow> _visible = new();

    private readonly BufferedListView _listView = new();
    private readonly TextBox _detailBox = new();
    private readonly TextBox _searchBox = new();
    private readonly ComboBox _levelFilter = new();
    private readonly CheckBox _failuresOnlyBox = new();
    private readonly CheckBox _followCheckBox = new();
    private readonly CheckBox _wrapCheckBox = new();
    private readonly LinkLabel _newCountLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _warningLabel = new();
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new();
    private SplitContainer _split = new();

    private LogLevel _minimumLevel = LogLevel.Debug;
    private string[] _keywords = [];
    private int _pendingNewCount;
    private bool _failuresOnly;
    private bool _followEnabled = true;
    private bool _suppressDetail;
    private bool _splitterReady;
    private bool _fittingColumn;

    public LogViewTab(ConfigStore configStore)
    {
        // 构造参数留给宿主，日志页本身只读磁盘上的日志文件
        _ = configStore;
        _reader = new LogFileReader(AuditLoggerPath());

        InitializeLayout();
        BindEvents();
        LoadInitialEntries();
        AuditLogger.Debug($"日志页面已加载 {_all.Count} 条历史记录");
        StartRefreshTimer();
    }

    /// <summary>
    /// 日志文件路径
    /// </summary>
    private static string AuditLoggerPath()
    {
        return Path.Combine(LogFileWriter.DefaultDirectory, "remote-fs.log");
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

        root.Controls.Add(BuildToolbar(), 0, 0);
        root.Controls.Add(BuildWarning(), 0, 1);
        root.Controls.Add(BuildContent(), 0, 2);
        root.Controls.Add(BuildStatusBar(), 0, 3);

        Controls.Add(root);
    }

    /// <summary>
    /// 工具栏：等级、搜索、失败过滤、自动跟随
    /// </summary>
    private Control BuildToolbar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        bar.Controls.Add(new Label { Text = "等级", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });

        _levelFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _levelFilter.Width = 128;
        foreach (var (label, _) in LevelOptions)
        {
            _levelFilter.Items.Add(label);
        }

        _levelFilter.SelectedIndex = 0;
        bar.Controls.Add(_levelFilter);

        _searchBox.Width = 220;
        _searchBox.PlaceholderText = "工具、主机、路径、关键词";
        _searchBox.Margin = new Padding(8, 2, 0, 0);
        bar.Controls.Add(_searchBox);

        _failuresOnlyBox.Text = "只看失败";
        _failuresOnlyBox.AutoSize = true;
        _failuresOnlyBox.Margin = new Padding(8, 6, 0, 0);
        bar.Controls.Add(_failuresOnlyBox);

        _followCheckBox.Text = "自动跟随";
        _followCheckBox.Checked = true;
        _followCheckBox.AutoSize = true;
        _followCheckBox.Margin = new Padding(8, 6, 0, 0);
        bar.Controls.Add(_followCheckBox);

        var refreshButton = new Button { Text = "刷新", AutoSize = true };
        refreshButton.Click += (_, _) => ReloadFromDisk();
        bar.Controls.Add(refreshButton);

        var clearViewButton = new Button { Text = "清空视图", AutoSize = true };
        clearViewButton.Click += (_, _) => ClearView();
        bar.Controls.Add(clearViewButton);

        var openFolderButton = new Button { Text = "打开日志目录", AutoSize = true };
        openFolderButton.Click += (_, _) => OpenLogDirectory();
        bar.Controls.Add(openFolderButton);

        _newCountLabel.Text = string.Empty;
        _newCountLabel.AutoSize = true;
        _newCountLabel.Visible = false;
        _newCountLabel.Margin = new Padding(12, 6, 0, 0);
        _newCountLabel.LinkColor = Color.DarkOrange;
        _newCountLabel.VisitedLinkColor = Color.DarkOrange;
        _newCountLabel.ActiveLinkColor = Color.Chocolate;
        _newCountLabel.LinkBehavior = LinkBehavior.HoverUnderline;
        bar.Controls.Add(_newCountLabel);

        _toolTip.SetToolTip(_levelFilter, "只显示这个级别以及更严重的日志");
        _toolTip.SetToolTip(_searchBox, "多个词要同时出现。Esc 清空，Ctrl+F 聚焦到这里");
        _toolTip.SetToolTip(_failuresOnlyBox, "只显示失败的工具调用，以及错误级别的日志");
        _toolTip.SetToolTip(_followCheckBox, "停在底部时自动滚到新日志；往上翻时暂停，并提示新增条数");

        return bar;
    }

    /// <summary>
    /// 目录不可写等异常提示条
    /// </summary>
    private Control BuildWarning()
    {
        _warningLabel.Dock = DockStyle.Fill;
        _warningLabel.AutoSize = false;
        _warningLabel.Height = 34;
        _warningLabel.Padding = new Padding(8, 0, 8, 0);
        _warningLabel.TextAlign = ContentAlignment.MiddleLeft;
        _warningLabel.BackColor = Color.FromArgb(255, 235, 235);
        _warningLabel.ForeColor = Color.DarkRed;
        _warningLabel.Visible = false;
        return _warningLabel;
    }

    /// <summary>
    /// 列表 + 详情。列表用虚拟模式，避免每次刷新重建上万行。
    /// </summary>
    private Control BuildContent()
    {
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal
        };

        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.VirtualMode = true;
        _listView.FullRowSelect = true;
        _listView.GridLines = true;
        _listView.HideSelection = false;
        _listView.MultiSelect = false;
        _listView.ShowItemToolTips = true;
        _listView.AllowColumnReorder = false;
        _listView.Columns.Add("时间", 150);
        _listView.Columns.Add("级别", 56);
        _listView.Columns.Add("工具", 156);
        _listView.Columns.Add("主机", 110);
        _listView.Columns.Add("耗时", 72);
        _listView.Columns.Add("结果", 56);
        _listView.Columns.Add("摘要", 280);
        _split.Panel1.Controls.Add(_listView);

        _split.Panel2.Controls.Add(BuildDetailPanel());
        return _split;
    }

    /// <summary>
    /// 详情：复制、换行，下面是只读文本
    /// </summary>
    private Control BuildDetailPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0)
        };

        var copyButton = new Button { Text = "复制详情", AutoSize = true };
        copyButton.Click += (_, _) => CopyDetail();
        bar.Controls.Add(copyButton);

        _wrapCheckBox.Text = "自动换行";
        _wrapCheckBox.Checked = true;
        _wrapCheckBox.AutoSize = true;
        _wrapCheckBox.Margin = new Padding(8, 6, 0, 0);
        bar.Controls.Add(_wrapCheckBox);

        _detailBox.Dock = DockStyle.Fill;
        _detailBox.Multiline = true;
        _detailBox.ReadOnly = true;
        _detailBox.ScrollBars = ScrollBars.Vertical;
        _detailBox.WordWrap = true;
        _detailBox.HideSelection = false;
        // 多行文本框默认最多 32767 字，出入参加起来会超，0 表示不限制
        _detailBox.MaxLength = 0;
        _detailBox.Font = new Font("Consolas", 9F);
        _detailBox.BackColor = SystemColors.Window;

        panel.Controls.Add(bar, 0, 0);
        panel.Controls.Add(_detailBox, 0, 1);
        return panel;
    }

    /// <summary>
    /// 底部状态栏
    /// </summary>
    private Control BuildStatusBar()
    {
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Margin = new Padding(0, 4, 0, 0);
        return _statusLabel;
    }

    /// <summary>
    /// 绑定事件
    /// </summary>
    private void BindEvents()
    {
        _listView.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _listView.SelectedIndexChanged += (_, _) => ShowDetail();
        _listView.Resize += (_, _) => FitSummaryColumn();
        _listView.ColumnWidthChanged += (_, e) =>
        {
            if (e.ColumnIndex != _listView.Columns.Count - 1)
            {
                FitSummaryColumn();
            }
        };
        _listView.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            var hit = _listView.HitTest(e.Location);
            if (hit.Item is null)
            {
                return;
            }

            _listView.SelectedIndices.Clear();
            _listView.SelectedIndices.Add(hit.Item.Index);
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("复制摘要", null, (_, _) => CopySummary());
        menu.Items.Add("复制详情", null, (_, _) => CopyDetail());
        _listView.ContextMenuStrip = menu;

        _levelFilter.SelectedIndexChanged += (_, _) =>
        {
            var index = _levelFilter.SelectedIndex;
            if (index < 0 || index >= LevelOptions.Length)
            {
                return;
            }

            var level = LevelOptions[index].Level;
            if (level == _minimumLevel && IsHandleCreated)
            {
                return;
            }

            _minimumLevel = level;
            if (!IsHandleCreated)
            {
                return;
            }

            Refilter(keepSelection: true, scrollToBottom: true);
            ClearPending();
        };

        _searchBox.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                _searchTimer.Stop();
                CommitSearch();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                _searchBox.Clear();
                e.SuppressKeyPress = true;
            }
        };

        _failuresOnlyBox.CheckedChanged += (_, _) =>
        {
            if (_failuresOnly == _failuresOnlyBox.Checked)
            {
                return;
            }

            _failuresOnly = _failuresOnlyBox.Checked;
            if (!IsHandleCreated)
            {
                return;
            }

            Refilter(keepSelection: true, scrollToBottom: true);
            ClearPending();
        };

        _followCheckBox.CheckedChanged += (_, _) =>
        {
            _followEnabled = _followCheckBox.Checked;
            if (_followEnabled)
            {
                ClearPending();
                ScrollToBottom();
            }

            UpdateStatus();
        };

        _wrapCheckBox.CheckedChanged += (_, _) =>
        {
            _detailBox.WordWrap = _wrapCheckBox.Checked;
            _detailBox.ScrollBars = _wrapCheckBox.Checked ? ScrollBars.Vertical : ScrollBars.Both;
        };

        _newCountLabel.LinkClicked += (_, _) =>
        {
            ScrollToBottom();
            ClearPending();
            _followCheckBox.Checked = true;
        };

        _split.SizeChanged += (_, _) => EnsureSplitter();

        Disposed += (_, _) =>
        {
            _refreshTimer.Stop();
            _searchTimer.Stop();
            _refreshTimer.Dispose();
            _searchTimer.Dispose();
            _toolTip.Dispose();
        };
    }

    /// <summary>
    /// 句柄创建后再挂上虚拟列表条数，否则设了也不会画出来
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetVirtualCount(resetCache: true);
        if (_followEnabled)
        {
            ScrollToBottom();
        }

        FitSummaryColumn();
        EnsureSplitter();
    }

    /// <summary>
    /// Ctrl+F 聚焦搜索。列表上的 Ctrl+C 复制摘要，详情框仍走自己的复制。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F))
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
            return true;
        }

        if (keyData == (Keys.Control | Keys.C) && !_detailBox.Focused && !_searchBox.Focused)
        {
            CopySummary();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// 定时增量读取
    /// </summary>
    private void StartRefreshTimer()
    {
        _refreshTimer.Interval = 800;
        _refreshTimer.Tick += (_, _) => RefreshEntries();
        _refreshTimer.Start();

        _searchTimer.Interval = 200;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            CommitSearch();
        };
    }

    /// <summary>
    /// 首次加载历史日志，或用户点了刷新
    /// </summary>
    private void LoadInitialEntries()
    {
        _all.Clear();
        foreach (var entry in _reader.ReadTail(TailBytes))
        {
            _all.Add(LogDisplayRow.Create(entry));
        }

        TrimEntries();
        Refilter(keepSelection: false, scrollToBottom: true);
        ClearPending();
    }

    /// <summary>
    /// 重新从磁盘加载
    /// </summary>
    private void ReloadFromDisk()
    {
        _reader = new LogFileReader(AuditLoggerPath());
        LoadInitialEntries();
    }

    /// <summary>
    /// 增量刷新。停在底部才跟随；用户在看历史时只累计新条数。
    /// </summary>
    private void RefreshEntries()
    {
        try
        {
            RefreshWarning();

            var newEntries = _reader.ReadNew();
            if (newEntries.Count == 0)
            {
                return;
            }

            // 1. 先判断用户是否停在底部：列表还没追加新行时判断才准
            var wasAtBottom = IsAtBottom();
            var created = new List<LogDisplayRow>(newEntries.Count);
            foreach (var entry in newEntries)
            {
                created.Add(LogDisplayRow.Create(entry));
            }

            _all.AddRange(created);
            if (TrimEntries())
            {
                var stick = _followEnabled && wasAtBottom;
                var added = CountVisible(created);
                Refilter(keepSelection: !stick, scrollToBottom: stick);
                if (stick)
                {
                    ClearPending();
                }
                else if (added > 0)
                {
                    _pendingNewCount += added;
                    ShowPending();
                }

                return;
            }

            var visibleAdded = 0;
            foreach (var row in created)
            {
                if (!IsVisible(row))
                {
                    continue;
                }

                _visible.Add(row);
                visibleAdded++;
            }

            if (visibleAdded == 0)
            {
                UpdateStatus();
                return;
            }

            var top = CaptureTopIndex();
            SetVirtualCount(resetCache: false);
            if (_followEnabled && wasAtBottom)
            {
                ScrollToBottom();
                ClearPending();
            }
            else
            {
                RestoreTopIndex(top);
                _pendingNewCount += visibleAdded;
                ShowPending();
            }

            UpdateStatus();
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"日志页面刷新失败: {exception.Message}");
        }
    }

    /// <summary>
    /// 更新目录不可写等提示
    /// </summary>
    private void RefreshWarning()
    {
        var reason = AuditLogger.IsDisabled ? AuditLogger.DisabledReason : null;
        if (string.IsNullOrEmpty(reason))
        {
            if (_warningLabel.Visible)
            {
                _warningLabel.Visible = false;
            }

            return;
        }

        _warningLabel.Text = reason;
        if (!_warningLabel.Visible)
        {
            _warningLabel.Visible = true;
        }
    }

    /// <summary>
    /// 内存条目超量时丢弃最旧的。返回 true 表示调用方需要按当前过滤重刷列表。
    /// </summary>
    private bool TrimEntries()
    {
        if (_all.Count <= MaxEntries)
        {
            return false;
        }

        _all.RemoveRange(0, _all.Count - MaxEntries);
        return true;
    }

    /// <summary>
    /// 按当前等级、失败开关和关键词重建可见行
    /// </summary>
    private void Refilter(bool keepSelection, bool scrollToBottom)
    {
        var previous = keepSelection ? SelectedRow()?.Entry : null;
        var filtered = new List<LogDisplayRow>();
        foreach (var row in _all)
        {
            if (IsVisible(row))
            {
                filtered.Add(row);
            }
        }

        _suppressDetail = true;
        try
        {
            try
            {
                if (IsHandleCreated && _listView.IsHandleCreated && _listView.SelectedIndices.Count > 0)
                {
                    _listView.SelectedIndices.Clear();
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // 虚拟列表在条数切换瞬间可能拒绝清空选中
            }

            _visible.Clear();
            _visible.AddRange(filtered);
            SetVirtualCount(resetCache: true);
        }
        finally
        {
            _suppressDetail = false;
        }

        if (previous is not null && TrySelect(previous))
        {
            UpdateStatus();
            return;
        }

        _detailBox.Clear();
        if (scrollToBottom)
        {
            ScrollToBottom();
        }

        UpdateStatus();
    }

    /// <summary>
    /// 判断条目是否满足当前过滤
    /// </summary>
    private bool IsVisible(LogDisplayRow row)
    {
        if (!Enum.TryParse<LogLevel>(row.Entry.Level, out var level) || level < _minimumLevel)
        {
            return false;
        }

        if (_failuresOnly)
        {
            var isError = string.Equals(row.Entry.Level, nameof(LogLevel.Error), StringComparison.Ordinal);
            if (row.Entry.Success != false && !isError)
            {
                return false;
            }
        }

        foreach (var word in _keywords)
        {
            if (row.SearchText.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 把搜索框里的词提交到过滤条件。内容没变就不动列表。
    /// </summary>
    private void CommitSearch()
    {
        var keywords = _searchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (keywords.SequenceEqual(_keywords))
        {
            return;
        }

        _keywords = keywords;
        if (!IsHandleCreated)
        {
            return;
        }

        Refilter(keepSelection: true, scrollToBottom: true);
        ClearPending();
    }

    /// <summary>
    /// 展示选中条目的完整内容
    /// </summary>
    private void ShowDetail()
    {
        if (_suppressDetail)
        {
            return;
        }

        var row = SelectedRow();
        _detailBox.Text = row is null ? string.Empty : row.BuildDetail();
        _detailBox.SelectionStart = 0;
        _detailBox.ScrollToCaret();
    }

    /// <summary>
    /// 判断用户是否停在列表底部
    /// </summary>
    private bool IsAtBottom()
    {
        if (!_listView.IsHandleCreated || _listView.Items.Count == 0)
        {
            return true;
        }

        try
        {
            var top = _listView.TopItem?.Index ?? 0;
            return top + ItemsPerPage() >= _listView.Items.Count;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// 滚动到列表底部
    /// </summary>
    private void ScrollToBottom()
    {
        if (!_listView.IsHandleCreated || _visible.Count == 0)
        {
            return;
        }

        try
        {
            _listView.EnsureVisible(_visible.Count - 1);
        }
        catch (Exception)
        {
            // 滚动失败不影响后续刷新
        }
    }

    /// <summary>
    /// 清空视图（不动磁盘上的日志）
    /// </summary>
    private void ClearView()
    {
        _all.Clear();
        _visible.Clear();
        SetVirtualCount(resetCache: true);
        _detailBox.Clear();
        ClearPending();
        UpdateStatus();
    }

    /// <summary>
    /// 用系统默认程序打开日志目录
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

    /// <summary>
    /// 刷新底部状态
    /// </summary>
    private void UpdateStatus()
    {
        var followText = _followEnabled ? "自动跟随已开" : "自动跟随已关";
        var text = $"显示 {_visible.Count} / {_all.Count} 条　|　{followText}";
        if (_failuresOnly)
        {
            text += "　|　只看失败";
        }

        var keyword = _searchBox.Text.Trim();
        if (keyword.Length > 0)
        {
            if (keyword.Length > 40)
            {
                keyword = keyword[..40] + "…";
            }

            text += $"　|　搜索「{keyword}」";
        }

        text += $"　|　日志目录: {LogFileWriter.DefaultDirectory}";
        _statusLabel.Text = text;
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if ((uint)e.ItemIndex >= (uint)_visible.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        e.Item = CreateItem(_visible[e.ItemIndex]);
    }

    private static ListViewItem CreateItem(LogDisplayRow row)
    {
        var item = new ListViewItem(row.TimeText);
        item.SubItems.Add(row.LevelText);
        item.SubItems.Add(row.ToolText);
        item.SubItems.Add(row.HostText);
        item.SubItems.Add(row.ElapsedText);
        item.SubItems.Add(row.ResultText);
        item.SubItems.Add(row.Summary);
        item.UseItemStyleForSubItems = true;
        item.BackColor = row.BackColor;
        item.ForeColor = row.ForeColor;
        if (!string.IsNullOrEmpty(row.Summary))
        {
            item.ToolTipText = row.Summary;
        }

        return item;
    }

    private void SetVirtualCount(bool resetCache)
    {
        if (!_listView.IsHandleCreated)
        {
            return;
        }

        var count = _visible.Count;
        _listView.BeginUpdate();
        try
        {
            if (resetCache && _listView.VirtualListSize != 0)
            {
                _listView.VirtualListSize = 0;
            }

            if (_listView.VirtualListSize != count)
            {
                _listView.VirtualListSize = count;
            }
            else
            {
                _listView.Invalidate();
            }
        }
        finally
        {
            _listView.EndUpdate();
        }
    }

    private LogDisplayRow? SelectedRow()
    {
        if (_listView.SelectedIndices.Count == 0)
        {
            return null;
        }

        var index = _listView.SelectedIndices[0];
        if ((uint)index >= (uint)_visible.Count)
        {
            return null;
        }

        return _visible[index];
    }

    private bool TrySelect(LogEntry entry)
    {
        if (!_listView.IsHandleCreated)
        {
            return false;
        }

        var index = -1;
        for (var i = 0; i < _visible.Count; i++)
        {
            if (ReferenceEquals(_visible[i].Entry, entry))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        try
        {
            _listView.SelectedIndices.Add(index);
            _listView.EnsureVisible(index);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        ShowDetail();
        return true;
    }

    private int CaptureTopIndex()
    {
        if (!_listView.IsHandleCreated || _listView.Items.Count == 0)
        {
            return 0;
        }

        try
        {
            return _listView.TopItem?.Index ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 虚拟列表不能直接设置 TopItem。先保证页尾可见，再把页首拉回来。
    /// </summary>
    private void RestoreTopIndex(int topIndex)
    {
        if (!_listView.IsHandleCreated || _visible.Count == 0)
        {
            return;
        }

        var top = Math.Clamp(topIndex, 0, _visible.Count - 1);
        try
        {
            var bottom = Math.Min(_visible.Count - 1, top + ItemsPerPage() - 1);
            _listView.EnsureVisible(bottom);
            _listView.EnsureVisible(top);
        }
        catch (Exception)
        {
            // 保持当前滚动位置即可
        }
    }

    private int ItemsPerPage()
    {
        if (_listView.Items.Count == 0)
        {
            return 1;
        }

        try
        {
            var height = _listView.Items[0].Bounds.Height;
            if (height <= 0)
            {
                height = 20;
            }

            return Math.Max(1, _listView.ClientSize.Height / height);
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private int CountVisible(List<LogDisplayRow> rows)
    {
        var count = 0;
        foreach (var row in rows)
        {
            if (IsVisible(row))
            {
                count++;
            }
        }

        return count;
    }

    private void ShowPending()
    {
        _newCountLabel.Text = $"有 {_pendingNewCount} 条新日志，点击跳到底部";
        _newCountLabel.LinkArea = new LinkArea(0, _newCountLabel.Text.Length);
        _newCountLabel.Visible = true;
    }

    private void ClearPending()
    {
        _pendingNewCount = 0;
        _newCountLabel.Text = string.Empty;
        _newCountLabel.Visible = false;
    }

    private void CopySummary()
    {
        var row = SelectedRow();
        if (row is null)
        {
            return;
        }

        var parts = new[]
        {
            row.Entry.Timestamp,
            row.LevelText,
            row.ToolText,
            row.HostText,
            row.ElapsedText,
            row.ResultText,
            row.Summary
        };
        CopyText(string.Join("\t", parts.Where(part => !string.IsNullOrEmpty(part))));
    }

    private void CopyDetail()
    {
        if (!string.IsNullOrEmpty(_detailBox.Text))
        {
            CopyText(_detailBox.Text);
            return;
        }

        CopySummary();
    }

    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"复制失败：{exception.Message}", "复制失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 最后一列吃掉剩余宽度，摘要不用再被固定成一小段
    /// </summary>
    private void FitSummaryColumn()
    {
        if (_fittingColumn || _listView.Columns.Count == 0 || _listView.ClientSize.Width <= 0)
        {
            return;
        }

        var used = 0;
        for (var i = 0; i < _listView.Columns.Count - 1; i++)
        {
            used += _listView.Columns[i].Width;
        }

        var width = Math.Max(80, _listView.ClientSize.Width - used - 4);
        var column = _listView.Columns[^1];
        if (column.Width == width)
        {
            return;
        }

        _fittingColumn = true;
        try
        {
            column.Width = width;
        }
        finally
        {
            _fittingColumn = false;
        }
    }

    /// <summary>
    /// 按当前高度把详情区留出来。构造时高度还是 0，写死分割位置会把详情挤没。
    /// </summary>
    private void EnsureSplitter()
    {
        if (_splitterReady)
        {
            return;
        }

        // 详情区至少留 120。分割位置要先落在这个范围内，再提高最小尺寸，否则会抛异常
        const int panel1Min = 80;
        const int panel2Min = 120;
        var height = _split.Height;
        var splitter = _split.SplitterWidth;
        var maxDistance = height - panel2Min - splitter;
        if (maxDistance < panel1Min)
        {
            return;
        }

        try
        {
            var currentMax = height - _split.Panel2MinSize - splitter;
            var desired = Math.Clamp((int)(height * 0.62), panel1Min, maxDistance);
            if (desired < _split.Panel1MinSize || desired > currentMax)
            {
                return;
            }

            _split.SplitterDistance = desired;
            _split.Panel1MinSize = panel1Min;
            _split.Panel2MinSize = panel2Min;
            _splitterReady = true;
        }
        catch (InvalidOperationException)
        {
            // 句柄还没布局完，等下一次 SizeChanged
        }
    }

    /// <summary>
    /// 减轻滚动时的闪烁。ListView 的双缓冲属性是受保护的。
    /// </summary>
    private sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
        }
    }
}
