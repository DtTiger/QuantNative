using System;
using System.Drawing;
using System.Windows.Forms;
using QuantNative.Core;

namespace QuantNative.UI
{
    /// <summary>
    /// 任务栏角标（托盘图标）。左键切换主界面、右键出菜单 —— 与旧版行为一致，
    /// 菜单里新增的「管理自选…/浮窗设置…」会直接打开浮窗面板。
    /// </summary>
    public sealed class TrayIconHost : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly AppHost _host;

        public TrayIconHost(AppHost host)
        {
            _host = host;

            _icon = new NotifyIcon();
            _icon.Icon = AppIcon.Get();
            _icon.Text = "QuantNative 实时行情";
            _icon.ContextMenuStrip = AppMenus.BuildTrayMenu(host);
            _icon.Visible = true;
            _icon.MouseClick += OnMouseClick;
            _icon.DoubleClick += delegate { _host.ShowMain(); };
        }

        private void OnMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_host.MainWindow != null && _host.MainWindow.Visible) _host.HideMain();
            else _host.ShowMain();
        }

        public void Dispose()
        {
            try
            {
                _icon.Visible = false;
                _icon.MouseClick -= OnMouseClick;
                _icon.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("释放托盘图标失败: " + ex.Message);
            }
        }
    }
}
