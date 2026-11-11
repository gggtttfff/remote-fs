using System.Runtime.InteropServices;
using RemoteFs.Common;

namespace RemoteFs.Gui;

/// <summary>
/// GUI 启动器：WinForms 要求 STA 线程，而入口 Main 是 async 方法，
/// 因此单独起一个 STA 线程承载消息循环。
/// </summary>
public static class GuiLauncher
{
    public static int Run()
    {
        var exitCode = 1;

        // 双击启动时先收掉多余的控制台窗口，只留界面
        HideConsoleWindowIfIsolated();

        AuditLogger.Info("图形配置界面已启动");

        var uiThread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new HostEditorForm());
                exitCode = 0;
            }
            catch (Exception exception)
            {
                // 兜底：界面初始化失败时给出可读提示，避免静默退出
                AuditLogger.Error($"GUI 启动失败: {exception}");
                MessageBox.Show(
                    $"界面启动失败：{exception.Message}",
                    "remote-fs",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        });

        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();

        return exitCode;
    }

    /// <summary>
    /// 双击启动时进程独占控制台，此时隐藏控制台窗口只留界面；
    /// 从终端启动时控制台里还有其他进程，保留输出方便看日志。
    /// </summary>
    private static void HideConsoleWindowIfIsolated()
    {
        try
        {
            var processBuffer = new uint[4];
            var processCount = GetConsoleProcessList(processBuffer, (uint)processBuffer.Length);
            if (processCount <= 1)
            {
                FreeConsole();
            }
        }
        catch (Exception)
        {
            // 隐藏失败不影响界面启动
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
}