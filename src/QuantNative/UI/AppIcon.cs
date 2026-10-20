using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace QuantNative.UI
{
    /// <summary>
    /// 运行时生成应用图标（省掉一个 .ico 资源文件，也让仓库里没有二进制资源）。
    /// 造型：圆角蓝底 + 白色 K 线蜡烛，和「看盘工具」的语义一致。
    /// </summary>
    public static class AppIcon
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static Icon _cached;

        public static Icon Get()
        {
            if (_cached != null) return _cached;
            try
            {
                using (Bitmap bitmap = Draw(64))
                {
                    IntPtr handle = bitmap.GetHicon();
                    try
                    {
                        // 复制一份再销毁原始句柄，避免句柄泄漏
                        using (Icon temp = Icon.FromHandle(handle))
                        {
                            _cached = (Icon)temp.Clone();
                        }
                    }
                    finally
                    {
                        DestroyIcon(handle);
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Log.Warn("生成图标失败，改用系统图标: " + ex.Message);
                _cached = SystemIcons.Application;
            }
            return _cached;
        }

        private static Bitmap Draw(int size)
        {
            Bitmap bitmap = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Rectangle rect = new Rectangle(1, 1, size - 2, size - 2);
                using (GraphicsPath path = Theme.RoundedRect(rect, size / 4))
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    rect, Color.FromArgb(255, 0x2f, 0x81, 0xf7), Color.FromArgb(255, 0x1b, 0x4f, 0xa8), 60f))
                {
                    g.FillPath(brush, path);
                }

                // 两根蜡烛：一红一绿（A 股红涨绿跌）
                float unit = size / 8f;
                DrawCandle(g, 2.6f * unit, 1.4f * unit, 4.6f * unit, 5.2f * unit, Theme.Up);
                DrawCandle(g, 4.9f * unit, 2.6f * unit, 6.4f * unit, 3.6f * unit, Theme.Down);

                using (Pen line = new Pen(Color.FromArgb(220, 255, 255, 255), Math.Max(1f, size / 32f)))
                {
                    g.DrawLine(line, 2.2f * unit, 5.6f * unit, 6.4f * unit, 2.2f * unit);
                }
            }
            return bitmap;
        }

        private static void DrawCandle(Graphics g, float x, float top, float width, float bottom, Color color)
        {
            using (SolidBrush brush = new SolidBrush(color))
            using (Pen pen = new Pen(Color.White, Math.Max(1f, width / 6f)))
            {
                float midX = x + width / 2f;
                g.DrawLine(pen, midX, top - 2, midX, bottom + 2);
                g.FillRectangle(brush, x, top, width, Math.Max(2f, bottom - top));
            }
        }
    }
}
