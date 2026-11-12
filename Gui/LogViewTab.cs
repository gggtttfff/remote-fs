using System.Diagnostics;
using System.Text;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Logging;

namespace RemoteFs.Gui;

/// <summary>
/// 日志查看页：实时跟踪日志文件，支持等级过滤、底部跟随与详情查看。
/// </summary>
public sealed class LogViewTab : UserControl
{
    /// <summary>内存中最多保留的日志条数</summary>
    private const int MaxEntries = 20_000;

    /// <summary>打开时加载的历史日志字节数</summary>
    private const long TailBytes = 5 * 1024 * 1024;

    private readonly ConfigStore _configStore;
    private LogFileReader _reader;
    private readonly List<LogEntry> _entries = new();

    private readonly ListView _listView = new();
    private readonly TextBox _detailBox = new();
    private readonly ComboBox _levelFilter = new();
    private readonly CheckBox _followCheckBox = new();
    private readonly Label _newCountLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _warningLabel = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    private LogLevel _minimumLevel = LogLevel.Debug;
    private int _pendingNewCount;
    private bool _followEnabled = true;

    public LogViewTab(ConfigStore configStore)
    {
        _configStore = configStore;
        _reader = new LogFileReader(AuditLoggerPath());

        InitializeLayout();
        BindEvents();
        LoadInitialEntries();
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
    /// 工具栏：等级过滤、自动跟随、清空视图、打开目录
    /// </summary>
    private Control BuildToolbar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        bar.Controls.Add(new Label { Text = "等级", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });

        _levelFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _levelFilter.Width = 110;
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            _levelFilter.Items.Add(level.ToString());
        }
        _levelFilter.SelectedItem = "Debug";
        bar.Controls.Add(_levelFilter);

        _followCheckBox.Text = "自动跟随";
        _followCheckBox.Checked = true;
        _followCheckBox.AutoSize = true;
        _followCheckBox.Margin = new Padding(12, 6, 0, 0);
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
        _newCountLabel.ForeColor = Color.DarkOrange;
        _newCountLabel.AutoSize = true;
        _newCountLabel.Margin = new Padding(12, 6, 0, 0);
        bar.Controls.Add(_newCountLabel);

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
    /// 列表 + 详情
    /// </summary>
    private Control BuildContent()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 420
        };

        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines = true;
        _listView.HideSelection = false;
        _listView.Columns.Add("时间", 170);
        _listView.Columns.Add("级别", 60);
        _listView.Columns.Add("工具", 170);
        _listView.Columns.Add("主机", 120);
        _listView.Columns.Add("耗时", 70);
        _listView.Columns.Add("结果", 80);
        _listView.Columns.Add("摘要", 320);
        split.Panel1.Controls.Add(_listView);

        _detailBox.Dock = DockStyle.Fill;
        _detailBox.Multiline = true;
        _detailBox.ReadOnly = true;
        _detailBox.ScrollBars = ScrollBars.Both;
        _detailBox.WordWrap = false;
        _detailBox.Font = new Font("Consolas", 9F);
        _detailBox.BackColor = SystemColors.Window;
        split.Panel2.Controls.Add(_detailBox);

        return split;
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
        _levelFilter.SelectedIndexChanged += (_, _) =>
        {
            if (Enum.TryParse<LogLevel>(_levelFilter.SelectedItem?.ToString(), out var level))
            {
                _minimumLevel = level;
                ReloadFromDisk();
            }
        };

        _followCheckBox.CheckedChanged += (_, _) =>
        {
            _followEnabled = _followCheckBox.Checked;
            if (_followEnabled)
            {
                _pendingNewCount = 0;
                _newCountLabel.Text = string.Empty;
                ScrollToBottom();
            }
        };

        _listView.SelectedIndexChanged += (_, _) => ShowDetail();

        // 点击新日志提示直接跳到底部并恢复跟随
        _newCountLabel.Click += (_, _) =>
        {
            ScrollToBottom();
            _pendingNewCount = 0;
            _newCountLabel.Text = string.Empty;
            _followCheckBox.Checked = true;
        };

        Disposed += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>
    /// 定时增量读取
    /// </summary>
    private void StartRefreshTimer()
    {
        _refreshTimer.Interval = 800;
        _refreshTimer.Tick += (_, _) => RefreshEntries();
        _refreshTimer.Start();
    }

    /// <summary>
    /// 首次加载历史日志
    /// </summary>
    private void LoadInitialEntries()
    {
        _entries.Clear();
        _entries.AddRange(_reader.ReadTail(TailBytes));
        ApplyLevelFilter();
        UpdateStatus();

        // 排查时确认历史日志确实被解析进来了
        AuditLogger.Debug($"日志页面已加载 {_entries.Count} 条历史记录");
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
    /// 增量刷新
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

            _entries.AddRange(newEntries);
            TrimEntries();

            var added = ApplyLevelFilter(newEntries.Count);
            if (added == 0)
            {
                return;
            }

            if (_followEnabled && wasAtBottom)
            {
                ScrollToBottom();
                _pendingNewCount = 0;
                _newCountLabel.Text = string.Empty;
            }
            else
            {
                // 用户正在看历史，冻结视图并提示有多少条新日志
                _pendingNewCount += added;
                _newCountLabel.Text = $"有 {_pendingNewCount} 条新日志，点击跳到底部";
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
    /// 内存条目超量时丢弃最旧的
    /// </summary>
    private void TrimEntries()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        _entries.RemoveRange(0, _entries.Count - MaxEntries);
    }

    /// <summary>
    /// 按等级过滤后刷新列表，返回新增的可见条数
    /// </summary>
    private int ApplyLevelFilter(int newEntryCount = 0)
    {
        try
        {
            _listView.BeginUpdate();
            _listView.Items.Clear();

            foreach (var entry in _entries)
            {
                if (!IsVisible(entry))
                {
                    continue;
                }

                var item = new ListViewItem(entry.Timestamp);
                item.SubItems.Add(entry.Level);
                item.SubItems.Add(entry.Tool ?? entry.Event);
                item.SubItems.Add(entry.Host ?? string.Empty);
                item.SubItems.Add(entry.ElapsedMilliseconds is { } elapsed ? $"{elapsed}ms" : string.Empty);
                item.SubItems.Add(entry.Success is true ? "成功" : entry.Success is false ? "失败" : string.Empty);
                item.SubItems.Add(BuildSummary(entry));
                item.Tag = entry;
                _listView.Items.Add(item);
            }

            _listView.EndUpdate();
        }
        catch (Exception)
        {
            // 列表刷新失败不影响后续读取
        }

        return newEntryCount;
    }

    /// <summary>
    /// 判断条目是否满足当前等级过滤
    /// </summary>
    private bool IsVisible(LogEntry entry)
    {
        return Enum.TryParse<LogLevel>(entry.Level, out var level) && level >= _minimumLevel;
    }

    /// <summary>
    /// 生成列表摘要
    /// </summary>
    private static string BuildSummary(LogEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Error))
        {
            var firstLine = entry.Error.Split('\n')[0];
            return firstLine.Length > 80 ? firstLine[..80] : firstLine;
        }

        if (!string.IsNullOrEmpty(entry.Message))
        {
            return entry.Message.Length > 80 ? entry.Message[..80] : entry.Message;
        }

        if (!string.IsNullOrEmpty(entry.Response))
        {
            var firstLine = entry.Response.Split('\n')[0];
            return firstLine.Length > 80 ? firstLine[..80] : firstLine;
        }

        return string.Empty;
    }

    /// <summary>
    /// 展示选中条目的完整内容
    /// </summary>
    private void ShowDetail()
    {
        if (_listView.SelectedItems.Count == 0)
        {
            _detailBox.Text = string.Empty;
            return;
        }

        if (_listView.SelectedItems[0].Tag is not LogEntry entry)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"时间: {entry.Timestamp}");
        builder.AppendLine($"级别: {entry.Level}");
        builder.AppendLine($"事件: {entry.Event}");
        if (!string.IsNullOrEmpty(entry.Tool))
        {
            builder.AppendLine($"工具: {entry.Tool}");
        }
        if (!string.IsNullOrEmpty(entry.Host))
        {
            builder.AppendLine($"主机: {entry.Host}");
        }
        if (entry.ElapsedMilliseconds is { } elapsed)
        {
            builder.AppendLine($"耗时: {elapsed} 毫秒");
        }

        // 耗时追踪没有出入参，详情只保留上面这几项
        if (entry.Event == "tool_timing")
        {
            _detailBox.Text = builder.ToString();
            return;
        }

        builder.AppendLine();
        builder.AppendLine("=== 入参 ===");
        builder.AppendLine(entry.Arguments ?? "(未记录)");
        builder.AppendLine();
        builder.AppendLine("=== 出参 ===");
        builder.AppendLine(entry.Response ?? "(未记录)");
        if (!string.IsNullOrEmpty(entry.Error))
        {
            builder.AppendLine();
            builder.AppendLine("=== 错误 ===");
            builder.AppendLine(entry.Error);
        }

        _detailBox.Text = builder.ToString();
    }

    /// <summary>
    /// 判断用户是否停在列表底部。
    /// ListView 不公开滚动条与行矩形，用「首条可见项 + 每页条数」推算是否到底。
    /// </summary>
    private bool IsAtBottom()
    {
        if (_listView.Items.Count == 0)
        {
            return true;
        }

        try
        {
            var itemHeight = _listView.Items[0].Bounds.Height;
            if (itemHeight <= 0)
            {
                itemHeight = 20;
            }

            var itemsPerPage = Math.Max(1, _listView.ClientSize.Height / itemHeight);
            var topIndex = _listView.TopItem?.Index ?? 0;

            // 首条可见项加上整页仍到不了最后一条，说明用户往上翻了
            return topIndex + itemsPerPage >= _listView.Items.Count;
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
        if (_listView.Items.Count == 0)
        {
            return;
        }

        try
        {
            var lastItem = _listView.Items[_listView.Items.Count - 1];
            lastItem.EnsureVisible();
            _listView.TopItem = lastItem;
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
        _entries.Clear();
        _listView.Items.Clear();
        _detailBox.Text = string.Empty;
        _pendingNewCount = 0;
        _newCountLabel.Text = string.Empty;
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
        var visibleCount = _listView.Items.Count;
        var totalCount = _entries.Count;
        var followText = _followEnabled ? "自动跟随已开" : "自动跟随已关";
        _statusLabel.Text = $"显示 {visibleCount} / {totalCount} 条　|　{followText}"
            + $"　|　日志目录: {LogFileWriter.DefaultDirectory}";
    }
}
