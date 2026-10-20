using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.UI;

namespace QuantNative
{
    /// <summary>
    /// 入口。启动顺序与旧版一致：先起浮窗（默认界面），主窗口只创建不显示。
    /// 没有主窗体，靠托盘图标 + 浮窗维持消息循环（ApplicationContext）。
    /// </summary>
    internal static class Program
    {
        private const string MutexName = "QuantNative.SingleInstance.v2";

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("QuantNative 已经在运行（请看任务栏右下角角标）。",
                        "QuantNative", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // .NET Framework 默认不启用 TLS 1.2，而新浪/东财的接口都是 https
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit = 32;
                ServicePointManager.Expect100Continue = false;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnDomainException;

                AppHost host = new AppHost();
                ApplicationContext context = new ApplicationContext();
                host.Context = context;

                try
                {
                    host.Init();

                    host.MainWindow = new MainForm(host);
                    host.Ticker = new TickerBarForm(host);
                    host.Tray = new TrayIconHost(host);

                    // 浮窗可见性：尊重上次退出时的隐藏状态，其余交给「每日显示时段」调度
                    host.StartTickerBySchedule();
                    host.Start();

                    Application.Run(context);
                }
                catch (Exception ex)
                {
                    Log.Error("启动失败", ex);
                    MessageBox.Show("QuantNative 启动失败：\r\n" + ex.Message
                        + "\r\n\r\n日志：" + Log.Path_,
                        "QuantNative", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    try { if (host.Hub != null) host.Hub.Stop(); } catch { }
                    try { if (host.Tray != null) host.Tray.Dispose(); } catch { }
                    try { if (host.Ticker != null) host.Ticker.Dispose(); } catch { }
                    try { if (host.MainWindow != null) host.MainWindow.Dispose(); } catch { }
                }
            }
        }

        /// <summary>界面线程异常：记录并提示，不让程序直接消失。</summary>
        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Log.Error("界面异常", e.Exception);
            try
            {
                MessageBox.Show("出现了一个错误，已记录到日志：\r\n" + e.Exception.Message
                    + "\r\n\r\n日志：" + Log.Path_,
                    "QuantNative", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            Log.Error("未处理异常", ex ?? new Exception("unknown"));
        }
    }
}
