using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteFs.Common;

namespace RemoteFs.Gui;

/// <summary>
/// MCP 配置片段对话框：展示当前程序的 mcpServers 片段，供用户自行复制粘贴。
/// 不直接修改任何外部配置文件。
/// </summary>
public sealed class McpConfigDialog : Form
{
    private McpConfigDialog(string snippet)
    {
        Text = "MCP 配置片段";
        Width = 720;
        Height = 400;
        MinimumSize = new Size(520, 300);
        StartPosition = FormStartPosition.CenterParent;

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(8, 8, 8, 4),
            Text = "把下面这段合并到 Claude Code 配置文件的 mcpServers 中，保存后重启客户端。"
                + Environment.NewLine
                + "本工具不会直接修改任何配置文件。"
        };

        var snippetBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9F),
            Text = snippet,
            BackColor = SystemColors.Window
        };

        var copyButton = new Button { Text = "复制到剪贴板", AutoSize = true };
        copyButton.Click += (_, _) => CopySnippet(snippet);

        var closeButton = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.OK };

        var buttonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = Padding.Empty
        };
        buttonBar.Controls.Add(closeButton);
        buttonBar.Controls.Add(copyButton);

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
        root.Controls.Add(hint, 0, 0);
        root.Controls.Add(snippetBox, 0, 1);
        root.Controls.Add(buttonBar, 0, 2);

        Controls.Add(root);
        AcceptButton = closeButton;
        CancelButton = closeButton;
    }

    /// <summary>
    /// 弹出配置片段窗口
    /// </summary>
    public static void ShowSnippet(IWin32Window owner)
    {
        using var dialog = new McpConfigDialog(BuildSnippet());
        dialog.ShowDialog(owner);
    }

    /// <summary>
    /// 按当前可执行文件真实路径生成 mcpServers 片段
    /// </summary>
    public static string BuildSnippet()
    {
        var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["remote-fs"] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = executablePath,
                    ["args"] = new JsonArray("serve")
                }
            }
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// 复制到剪贴板，失败时给出提示而不是静默吞掉
    /// </summary>
    private static void CopySnippet(string snippet)
    {
        try
        {
            Clipboard.SetText(snippet);
            AuditLogger.Info("已复制 MCP 配置片段到剪贴板");
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"复制到剪贴板失败: {exception.Message}");
            MessageBox.Show($"复制失败，请手动选中文本复制：{exception.Message}",
                "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}