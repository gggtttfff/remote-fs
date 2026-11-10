namespace RemoteFs.Common;

/// <summary>
/// 原子文件写入：先写临时文件再替换，避免写入中断导致配置文件损坏。
/// </summary>
public static class AtomicFileWriter
{
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        if (File.Exists(path))
        {
            // File.Move 带覆盖参数在目标存在时是原子替换
            File.Move(temporaryPath, path, true);
        }
        else
        {
            File.Move(temporaryPath, path);
        }
    }
}
