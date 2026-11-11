using RemoteFs.Config;
using RemoteFs.Services;

namespace RemoteFs.Gui;

/// <summary>
/// 从 ~/.ssh/config 导入主机的对话框
/// </summary>
public sealed class SshConfigImportDialog : Form
{
    private readonly ConfigStore _configStore;
    private readonly ListView _listView = new();
    private readonly Label _statusLabel = new();
    private List<SshConfigCandidate> _candidates;

    public SshConfigImportDialog(ConfigStore configStore)
    {
        _configStore = configStore;
        _candidates = new SshConfigImporter(configStore).Scan();

        InitializeLayout();
        LoadCandidates();
    }

    /// <summary>
    /// 组装界面
    /// </summary>
    private void InitializeLayout()
    {
        Text = "从 ~/.ssh/config 导入主机";
        Width = 780;
        Height = 480;
        MinimumSize = new Size(600, 360);
        StartPosition = FormStartPosition.CenterParent;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildList(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        Controls.Add(root);
    }

    /// <summary>
    /// 顶部说明
    /// </summary>
    private Control BuildHeader()
    {
        var label = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 48,
            Text = "扫描 ~/.ssh/config 里的 Host 条目，勾选需要的导入。导入后别名沿用 ssh_config 里的 Host 名，"
                + "地址、端口、用户名、私钥路径自动带入。指纹稍后可用「探测指纹」获取。\r\n"
                + "含 ProxyJump 的条目导入后仍需直连，本工具暂不支持跳板机。"
        };
        return label;
    }

    /// <summary>
    /// 候选列表
    /// </summary>
    private Control BuildList()
    {
        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.CheckBoxes = true;
        _listView.HideSelection = false;
        _listView.Columns.Add("导入", 50);
        _listView.Columns.Add("别名", 130);
        _listView.Columns.Add("地址", 180);
        _listView.Columns.Add("端口", 55);
        _listView.Columns.Add("用户", 80);
        _listView.Columns.Add("私钥", 220);
        _listView.Columns.Add("备注", 160);
        _listView.ItemChecked += OnItemChecked;
        return _listView;
    }

    /// <summary>
    /// 底部按钮
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

        var importButton = new Button { Text = "导入选中项", AutoSize = true };
        importButton.Click += (_, _) => ImportSelected();

        var rescanButton = new Button { Text = "重新扫描", AutoSize = true };
        rescanButton.Click += (_, _) =>
        {
            _candidates = new SshConfigImporter(_configStore).Scan();
            LoadCandidates();
        };

        var cancelButton = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };

        var buttonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        buttonBar.Controls.Add(importButton);
        buttonBar.Controls.Add(rescanButton);
        buttonBar.Controls.Add(cancelButton);
        footer.Controls.Add(buttonBar, 1, 0);

        CancelButton = cancelButton;
        return footer;
    }

    /// <summary>
    /// 填充候选列表
    /// </summary>
    private void LoadCandidates()
    {
        _listView.Items.Clear();

        if (_candidates.Count == 0)
        {
            _statusLabel.Text = "没有找到可导入的主机。检查 ~/.ssh/config 是否存在且包含 Host 条目。";
            return;
        }

        foreach (var candidate in _candidates)
        {
            var item = new ListViewItem(string.Empty) { Tag = candidate, Checked = candidate.Selected };
            item.SubItems.Add(candidate.Pattern);
            item.SubItems.Add(candidate.Host);
            item.SubItems.Add(candidate.Port.ToString());
            item.SubItems.Add(candidate.User);
            item.SubItems.Add(candidate.IdentityFile ?? "(未指定)");
            item.SubItems.Add(candidate.IdentityFileExists ? "私钥存在" : "私钥不存在");
            _listView.Items.Add(item);
        }

        _statusLabel.Text = $"共 { _candidates.Count } 条可导入，已存在的同名主机会自动跳过";
    }

    /// <summary>
    /// 勾选状态变化时同步到候选对象
    /// </summary>
    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (e.Item.Tag is SshConfigCandidate candidate)
        {
            candidate.Selected = e.Item.Checked;
        }
    }

    /// <summary>
    /// 执行导入
    /// </summary>
    private void ImportSelected()
    {
        try
        {
            var created = new SshConfigImporter(_configStore).Import(_candidates);
            if (created.Count == 0)
            {
                MessageBox.Show("没有勾选任何条目。", "导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            MessageBox.Show(
                $"已导入 {created.Count} 台主机：{string.Join("、", created.Select(host => host.Name))}\r\n\r\n"
                + "下一步：对每台主机点「探测指纹」拿到指纹并填入，连接才会启用指纹校验。",
                "导入完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"导入失败：{exception.Message}", "导入失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
