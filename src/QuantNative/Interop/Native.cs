using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QuantNative.Interop
{
    /// <summary>
    /// Win32 互操作：分层窗口（逐像素透明度）、隐藏任务栏图标、开机自启、DPI。
    ///
    /// 为什么需要分层窗口：浮窗要「底图可调透明度，但文字保持不透明」。
    /// WinForms 的 Form.Opacity 会把整窗（含文字）一起变淡，TransparencyKey 又会在
    /// 抗锯齿文字边缘留下杂色 —— 只有 UpdateLayeredWindow 能做到仅底图半透明。
    /// </summary>
    internal static class Native
    {
        // ── 常量 ──
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;

            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;

            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        // ── user32 / gdi32 ──
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        // 用 GetWindowLong/SetWindowLong 而不是 ...Ptr 版本：两者在 32/64 位下都可用，
        // 而 GWL_EXSTYLE 的值在 32 位内，不需要指针宽度。
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        // ── 分层窗口 ──

        /// <summary>给窗口加上 WS_EX_LAYERED（UpdateLayeredWindow 的前置条件）。</summary>
        public static void EnsureLayered(IntPtr hwnd)
        {
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((style & WS_EX_LAYERED) == 0)
                SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED);
        }

        /// <summary>
        /// 关闭任务栏与 Alt-Tab 中的显示（等价于 Tauri 版的 WS_EX_TOOLWINDOW）。
        /// 需要窗口已创建句柄，且改完样式后要让框架重算非工作区。
        /// </summary>
        public static void HideFromTaskbar(IntPtr hwnd)
        {
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((style & WS_EX_TOOLWINDOW) != 0) return;
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
        }

        /// <summary>
        /// 把 32bpp ARGB 位图整幅推到分层窗口。
        /// Bitmap.GetHbitmap(Color.FromArgb(0)) 得到的是预乘 alpha 的 DIB，
        /// 配合 AC_SRC_ALPHA 正是 UpdateLayeredWindow 需要的格式。
        /// </summary>
        public static bool PushLayeredBitmap(IntPtr hwnd, Bitmap bitmap, int x, int y)
        {
            if (hwnd == IntPtr.Zero || bitmap == null) return false;

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                memDc = CreateCompatibleDC(screenDc);
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                SIZE size = new SIZE(bitmap.Width, bitmap.Height);
                POINT srcLoc = new POINT(0, 0);
                POINT dstLoc = new POINT(x, y);
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                return UpdateLayeredWindow(
                    hwnd, screenDc, ref dstLoc, ref size, memDc, ref srcLoc, 0, ref blend, ULW_ALPHA);
            }
            catch (Exception ex)
            {
                Core.Log.Warn("UpdateLayeredWindow 失败: " + ex.Message);
                return false;
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>把窗口钉在最前（置顶开关切换时用）。</summary>
        public static void SetTopMost(IntPtr hwnd, bool topMost)
        {
            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOSIZE = 0x0001;
            const uint SWP_NOACTIVATE = 0x0010;
            IntPtr after = topMost ? HWND_TOPMOST : new IntPtr(-2); // HWND_NOTOPMOST
            SetWindowPos(hwnd, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisibleNative(IntPtr hWnd);

        /// <summary>
        /// 窗口是否真的画在屏幕上。
        /// Form.Visible 只是托管标志位：句柄没建、被别的置顶窗盖住、
        /// 或被系统隐藏时它仍然是 true —— 判断「浮窗到底有没有显示」必须问系统。
        /// </summary>
        public static bool IsWindowVisible(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return IsWindowVisibleNative(hwnd); }
            catch { return false; }
        }

        // ── 输入框占位提示（WinForms 原生 TextBox 没有 PlaceholderText） ──

        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>设置输入框的灰色占位文字（Vista 以上系统原生支持，不额外画控件）。</summary>
        public static void SetCueBanner(System.Windows.Forms.TextBox box, string text)
        {
            try
            {
                if (box != null && box.IsHandleCreated)
                    SendMessageW(box.Handle, EM_SETCUEBANNER, new IntPtr(1), text ?? "");
            }
            catch (Exception ex)
            {
                Core.Log.Warn("SetCueBanner 失败: " + ex.Message);
            }
        }

        // ── 开机自启（注册表 Run 键，等价于 tauri-plugin-autostart） ──

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "QuantNative";

        public static bool GetAutoStart()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object value = key.GetValue(RunValueName);
                    return value != null && value.ToString().Length > 0;
                }
            }
            catch (Exception ex)
            {
                Core.Log.Warn("读取开机自启失败: " + ex.Message);
                return false;
            }
        }

        public static bool SetAutoStart(bool enabled)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return false;
                    if (enabled)
                    {
                        string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                        key.SetValue(RunValueName, "\"" + exe + "\"");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Core.Log.Warn("设置开机自启失败: " + ex.Message);
                return false;
            }
        }
    }
}
