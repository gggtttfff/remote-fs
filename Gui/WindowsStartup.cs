using Microsoft.Win32;
using RemoteFs.Common;

namespace RemoteFs.Gui;

/// <summary>
/// 开机自启管理：写当前用户的 Run 键，不需要管理员权限。
/// 注册表是唯一事实来源，不在配置文件里做镜像，避免两边不一致。
/// </summary>
public static class WindowsStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "remote-fs";

    /// <summary>
    /// 是否已加入开机启动
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"读取开机启动项失败: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 加入开机启动，启动后直接打开配置界面
    /// </summary>
    public static bool Enable(out string? error)
    {
        error = null;
        try
        {
            var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开注册表启动项";
                return false;
            }

            // 带 gui 参数启动，行为明确不依赖默认分支
            key.SetValue(ValueName, $"\"{executablePath}\" gui");
            AuditLogger.Info($"已加入开机启动: {executablePath}");
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            AuditLogger.Warn($"加入开机启动失败: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 从开机启动中移除
    /// </summary>
    public static bool Disable(out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return true;
            }

            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AuditLogger.Info("已取消开机启动");
            }
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            AuditLogger.Warn($"取消开机启动失败: {exception.Message}");
            return false;
        }
    }
}