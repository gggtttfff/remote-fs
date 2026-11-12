using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace RemoteFs.Ssh;

/// <summary>
/// 文件权限与属主快照
/// </summary>
/// <param name="Mode">POSIX 权限位，例如 0644</param>
/// <param name="UserId">属主 uid</param>
/// <param name="GroupId">属组 gid</param>
public sealed record FileAttributesSnapshot(short Mode, int UserId, int GroupId);

/// <summary>
/// 文件属性快照与恢复。
/// 原子写是「写临时文件 → rename 覆盖」，新文件的权限与属主来自临时文件而不是原文件，
/// 所以覆盖写之后必须把原来的权限和属主补回去，否则 600 的私钥会变成 644。
/// SSH.NET 的 ChangePermissions 写入的模式位是错的（实测 644 落成 0420），
/// 因此恢复统一走服务端 chmod / chown。
/// </summary>
public static class RemoteFileAttributes
{
    /// <summary>
    /// 读取文件权限与属主快照。文件不存在或读取失败返回 null
    /// </summary>
    public static FileAttributesSnapshot? Capture(SftpClient sftp, string remotePath)
    {
        try
        {
            var attributes = sftp.GetAttributes(remotePath);
            if (attributes.IsDirectory)
            {
                return null;
            }
            return new FileAttributesSnapshot(
                ToMode(attributes), (int)attributes.UserId, (int)attributes.GroupId);
        }
        catch (Exception)
        {
            // 文件不存在或没有权限，调用方按「无需恢复」处理
            return null;
        }
    }

    /// <summary>
    /// 生成恢复命令。当前属性已经和快照一致时返回 null，避免白跑一条命令
    /// </summary>
    public static string? BuildRestoreCommand(
        SftpClient sftp, string remotePath, FileAttributesSnapshot original)
    {
        SftpFileAttributes current;
        try
        {
            current = sftp.GetAttributes(remotePath);
        }
        catch (Exception)
        {
            return null;
        }

        var commands = new List<string>();
        var currentMode = ToMode(current);
        if (currentMode != original.Mode)
        {
            var octal = Convert.ToString(original.Mode, 8).PadLeft(4, '0');
            commands.Add($"chmod {octal} -- {Common.ShellQuoting.Quote(remotePath)}");
        }

        if (current.UserId != original.UserId || current.GroupId != original.GroupId)
        {
            commands.Add($"chown {original.UserId}:{original.GroupId} -- "
                + Common.ShellQuoting.Quote(remotePath));
        }

        return commands.Count == 0 ? null : string.Join("; ", commands);
    }

    /// <summary>
    /// 把属性里的九个权限位还原成八进制模式
    /// </summary>
    private static short ToMode(SftpFileAttributes attributes)
    {
        var mode = 0;
        if (attributes.OwnerCanRead) mode |= 4 << 6;
        if (attributes.OwnerCanWrite) mode |= 2 << 6;
        if (attributes.OwnerCanExecute) mode |= 1 << 6;
        if (attributes.GroupCanRead) mode |= 4 << 3;
        if (attributes.GroupCanWrite) mode |= 2 << 3;
        if (attributes.GroupCanExecute) mode |= 1 << 3;
        if (attributes.OthersCanRead) mode |= 4;
        if (attributes.OthersCanWrite) mode |= 2;
        if (attributes.OthersCanExecute) mode |= 1;
        return (short)mode;
    }
}