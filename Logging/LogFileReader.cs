using System.Text;

namespace RemoteFs.Logging;

/// <summary>
/// 日志读取器：供 GUI 实时跟踪日志文件。
/// 只读取活动文件的尾部与增量，解析失败的行直接跳过，不会因为半行而卡住。
/// </summary>
public sealed class LogFileReader
{
    private readonly string _activeFilePath;
    private long _position;
    private string? _pendingFragment;

    public LogFileReader(string activeFilePath)
    {
        _activeFilePath = activeFilePath;
    }

    /// <summary>
    /// 读取当前文件尾部若干字节的历史日志
    /// </summary>
    public List<LogEntry> ReadTail(long maxBytes)
    {
        var entries = new List<LogEntry>();
        try
        {
            if (!File.Exists(_activeFilePath))
            {
                _position = 0;
                _pendingFragment = null;
                return entries;
            }

            _pendingFragment = null;
            var fileLength = new FileInfo(_activeFilePath).Length;
            var start = Math.Max(0, fileLength - maxBytes);

            using var stream = new FileStream(
                _activeFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[Math.Min(maxBytes, 512 * 1024)];
            var text = new StringBuilder();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            // 读过的字节不能再当新日志。位置停在开头的话，界面第一次刷新会把历史整段再追加一遍
            _position = stream.Position;

            // 1. 尾部截断可能导致第一行不完整，丢弃
            var content = text.ToString();
            var firstNewline = content.IndexOf('\n');
            if (firstNewline >= 0 && start > 0)
            {
                content = content[(firstNewline + 1)..];
            }

            // 2. 末尾若是半行，留给下次增量读取拼接，这次不解析
            var lastNewline = content.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                _pendingFragment = content.Length == 0 ? null : content;
                return entries;
            }

            if (lastNewline < content.Length - 1)
            {
                _pendingFragment = content[(lastNewline + 1)..];
                content = content[..(lastNewline + 1)];
            }

            foreach (var entry in ParseLines(content))
            {
                entries.Add(entry);
            }
        }
        catch (Exception)
        {
            // 读取失败不阻断界面
        }

        return entries;
    }

    /// <summary>
    /// 读取自上次读取以来新增的日志
    /// </summary>
    public List<LogEntry> ReadNew()
    {
        var entries = new List<LogEntry>();
        try
        {
            if (!File.Exists(_activeFilePath))
            {
                _position = 0;
                _pendingFragment = null;
                return entries;
            }

            var fileLength = new FileInfo(_activeFilePath).Length;

            // 1. 文件被轮转（变小）或换了名字，从头重新读
            if (fileLength < _position)
            {
                _position = 0;
                _pendingFragment = null;
            }

            if (fileLength == _position)
            {
                return entries;
            }

            using var stream = new FileStream(
                _activeFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_position, SeekOrigin.Begin);

            var content = new StringBuilder();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                content.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            _position = stream.Position;

            var text = content.ToString();
            if (_pendingFragment is not null)
            {
                text = _pendingFragment + text;
                _pendingFragment = null;
            }

            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                // 还没写完整一行，等下次
                _pendingFragment = text;
                return entries;
            }

            if (lastNewline < text.Length - 1)
            {
                _pendingFragment = text[(lastNewline + 1)..];
                text = text[..(lastNewline + 1)];
            }

            foreach (var entry in ParseLines(text))
            {
                entries.Add(entry);
            }
        }
        catch (Exception)
        {
            // 读取失败不阻断界面
        }

        return entries;
    }

    /// <summary>
    /// 解析 JSONL 文本，跳过非法行
    /// </summary>
    private static IEnumerable<LogEntry> ParseLines(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var entry = LogEntry.FromJsonLine(trimmed);
            if (entry is not null)
            {
                yield return entry;
            }
        }
    }
}
