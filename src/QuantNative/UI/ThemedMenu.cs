using System;
using System.Drawing;
using System.Windows.Forms;
using QuantNative.Core;

namespace QuantNative.UI
{
    /// <summary>
    /// 深/浅色主题的右键菜单。WinForms 默认菜单是系统灰白，放在暗色应用里很突兀，
    /// 这里用自定义 ProfessionalColorTable 统一成应用配色。
    /// </summary>
    public static class ThemedMenu
    {
        public static ContextMenuStrip Create()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new Palette());
            menu.BackColor = Theme.Surface1;
            menu.ForeColor = Theme.TextPrimary;
            menu.Font = Theme.FNormal;
            menu.ShowImageMargin = false;
            menu.Padding = new Padding(2);
            menu.DropShadowEnabled = true;
            return menu;
        }

        public static ToolStripMenuItem Item(string text, EventHandler onClick, bool danger = false)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.ForeColor = danger ? Theme.Error : Theme.TextPrimary;
            item.BackColor = Theme.Surface1;
            item.Padding = new Padding(4, 2, 4, 2);
            if (onClick != null) item.Click += onClick;
            return item;
        }

        private sealed class Palette : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.AccentDim; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.AccentDim; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.AccentDim; } }
            public override Color MenuItemBorder { get { return Theme.Accent; } }
            public override Color MenuBorder { get { return Theme.Border1; } }
            public override Color MenuItemPressedGradientBegin { get { return Theme.Surface2; } }
            public override Color MenuItemPressedGradientEnd { get { return Theme.Surface2; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Surface1; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Surface1; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Surface1; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Surface1; } }
            public override Color SeparatorDark { get { return Theme.Border0; } }
            public override Color SeparatorLight { get { return Theme.Border0; } }
        }
    }

    /// <summary>
    /// 共享的菜单构造：浮窗右键与托盘右键共用同一套命令，避免两处各写一遍后行为漂移。
    /// 子菜单（刷新档位 / 显示时段）在弹出时动态重建，这样 ✓ 与当前时段说明永远是最新的。
    /// </summary>
    public static class AppMenus
    {
        public static ContextMenuStrip BuildTickerMenu(Core.AppHost host)
        {
            ContextMenuStrip menu = ThemedMenu.Create();

            menu.Items.Add(ThemedMenu.Item("管理自选…", delegate { host.OpenPanel("watchlist"); }));
            menu.Items.Add(ThemedMenu.Item("浮窗设置…", delegate { host.OpenPanel("settings"); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(ThemedMenu.Item("立即刷新行情", delegate { host.Hub.ForceRefresh(); }));
            menu.Items.Add(BuildProfileMenu(host));
            menu.Items.Add(BuildScheduleMenu(host));
            menu.Items.Add(ThemedMenu.Item("自动关联全部基金", delegate { AutoAssociate(host); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(ThemedMenu.Item("打开主界面", delegate { host.ShowMain(); }));
            menu.Items.Add(ThemedMenu.Item("隐藏浮窗", delegate { host.HideTicker(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(ThemedMenu.Item("退出 QuantNative", delegate { host.Quit(); }, true));
            return menu;
        }

        public static ContextMenuStrip BuildTrayMenu(Core.AppHost host)
        {
            ContextMenuStrip menu = ThemedMenu.Create();

            menu.Items.Add(ThemedMenu.Item("管理自选…", delegate { host.OpenPanel("watchlist"); }));
            menu.Items.Add(ThemedMenu.Item("浮窗设置…", delegate { host.OpenPanel("settings"); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(ThemedMenu.Item("显示主界面", delegate { host.ShowMain(); }));
            menu.Items.Add(ThemedMenu.Item("显示/隐藏浮窗", delegate { host.ToggleTicker(); }));
            menu.Items.Add(ThemedMenu.Item("立即刷新行情", delegate { host.Hub.ForceRefresh(); }));
            menu.Items.Add(BuildProfileMenu(host));
            menu.Items.Add(BuildScheduleMenu(host));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(ThemedMenu.Item("检查更新", delegate { Updater.CheckInteractive(host); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(ThemedMenu.Item("退出", delegate { host.Quit(); }, true));
            return menu;
        }

        /// <summary>刷新档位子菜单：弹出时才生成，保证 ✓ 打在当前的档位上。</summary>
        private static ToolStripMenuItem BuildProfileMenu(Core.AppHost host)
        {
            ToolStripMenuItem root = ThemedMenu.Item("刷新档位", null);
            root.DropDownOpening += delegate
            {
                root.DropDownItems.Clear();
                RefreshProfile current = host.RefreshProfile;
                RefreshProfile[] profiles = MarketClock.Profiles();
                for (int i = 0; i < profiles.Length; i++)
                {
                    RefreshProfile profile = profiles[i];
                    string label = MarketClock.ProfileName(profile) + (current == profile ? "   ✓" : "");
                    root.DropDownItems.Add(ThemedMenu.Item(label, delegate { host.RefreshProfile = profile; }));
                }
                root.DropDownItems.Add(new ToolStripSeparator());
                root.DropDownItems.Add(ThemedMenu.Item(MarketClock.ProfileHint(current), null));
            };
            return root;
        }

        /// <summary>浮窗显示时段子菜单：显示当前时段、一键开关、跳到设置页细调。</summary>
        private static ToolStripMenuItem BuildScheduleMenu(Core.AppHost host)
        {
            ToolStripMenuItem root = ThemedMenu.Item("浮窗显示时段", null);
            root.DropDownOpening += delegate
            {
                root.DropDownItems.Clear();
                root.DropDownItems.Add(ThemedMenu.Item(
                    (host.TickerScheduleEnabled ? "已启用 · " : "未启用 · ") + host.TickerScheduleSummary(), null));
                root.DropDownItems.Add(new ToolStripSeparator());
                root.DropDownItems.Add(ThemedMenu.Item(
                    host.TickerScheduleEnabled ? "关闭定时隐藏" : "启用定时隐藏（默认 09:15 – 23:30）",
                    delegate
                    {
                        host.TickerScheduleEnabled = !host.TickerScheduleEnabled;
                        host.ShowTicker();
                    }));
                root.DropDownItems.Add(ThemedMenu.Item("在设置里调整时间…", delegate { host.OpenPanel("settings"); }));
            };
            return root;
        }

        /// <summary>菜单里的「自动关联全部基金」——结果只写日志，用气泡提示告知用户。</summary>
        private static async void AutoAssociate(Core.AppHost host)
        {
            try
            {
                int linked = await host.AutoAssociateAllAsync(true);
                host.ShowMain();
                Log.Info("菜单触发自动关联：本次关联 " + linked + " 只基金");
            }
            catch (Exception ex)
            {
                Log.Warn("菜单自动关联失败: " + ex.Message);
            }
        }
    }
}
