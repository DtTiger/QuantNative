using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using QuantNative.Core;

namespace QuantNative.UI
{
    /// <summary>
    /// 自绘行情图（分时 / 分钟K / 日周月K）。
    ///
    /// 不引第三方图表库：WinForms 里可选的都是几十 MB 的重依赖，而这里要画的只是
    /// 折线 + 蜡烛 + 量柱 + 均线，GDI+ 三百行足够，还能完全贴合应用配色。
    /// </summary>
    public sealed class ChartControl : Control
    {
        private List<MinutePoint> _minute = new List<MinutePoint>();
        private List<KLineBar> _bars = new List<KLineBar>();
        private bool _isMinute = true;
        private bool _isPercent;
        private bool _isNav;
        private double _prevClose;
        private string _caption = "";
        private string _emptyText = "暂无数据";

        /// <summary>日/周/月 K 每根蜡烛至少占的像素（决定可见根数）。</summary>
        private const int MinCandleWidth = 4;

        public ChartControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface1;
        }

        public void ShowMinute(List<MinutePoint> points, double prevClose, string caption)
        {
            _isPercent = false;
            _isNav = false;
            _isMinute = true;
            _minute = points ?? new List<MinutePoint>();
            _prevClose = prevClose;
            _caption = caption ?? "";
            Invalidate();
        }

        public void ShowKLine(List<KLineBar> bars, string caption)
        {
            _isNav = false;
            _isPercent = false;
            _isMinute = false;
            _bars = bars ?? new List<KLineBar>();
            _caption = caption ?? "";
            Invalidate();
        }

        public void SetEmpty(string text)
        {
            _isNav = false;
            _isPercent = false;
            _emptyText = text ?? "暂无数据";
            _isMinute = true;
            _minute = new List<MinutePoint>();
            _bars = new List<KLineBar>();
            Invalidate();
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Surface1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            Rectangle plot = CalcPlot();
            if (plot.Width < 40 || plot.Height < 40) return;

            if (_isNav) DrawNav(g, plot);
            else if (_isPercent) DrawPercent(g, plot);
            else if (_isMinute) DrawMinute(g, plot);
            else DrawCandles(g, plot);
        }

        public void ShowPercentTrend(List<MinutePoint> points, string caption)
        {
            _isNav = false;
            _isPercent = true; _minute = points; _caption = caption; Invalidate();
        }

        public void ShowNavLine(List<KLineBar> bars, string caption)
        {
            _isNav = true; _isPercent = false; _bars = bars; _caption = caption; Invalidate();
        }
        private void DrawNav(Graphics g, Rectangle plot)
        {
            if (_bars.Count == 0) { DrawEmpty(g); return; }
            double min = double.MaxValue, max = double.MinValue;
            foreach (var bar in _bars) { min = Math.Min(min, bar.Close); max = Math.Max(max, bar.Close); }
            double padding = Math.Max(0.0001, (max - min) * 0.08); min -= padding; max += padding;
            DrawGrid(g, plot, min, max, 0);
            var points = new List<PointF>();
            for (int i = 0; i < _bars.Count; i++) points.Add(new PointF(plot.Left + i * plot.Width / (float)Math.Max(1, _bars.Count - 1), PriceToY(_bars[i].Close, plot, min, max)));
            using (var pen = new Pen(Theme.Accent, 1.8f)) if (points.Count > 1) g.DrawLines(pen, points.ToArray());
            DrawTimeLabels(g, plot, _bars.Count, false); DrawCaption(g, plot);
        }

        private void DrawPercent(Graphics g, Rectangle plot)
        {
            if (_minute.Count == 0) { DrawEmpty(g); return; }
            double span = 0.5;
            foreach (MinutePoint p in _minute) span = Math.Max(span, Math.Abs(p.Price) * 1.1);
            using (var pen = new Pen(Theme.Border1)) using (var brush = new SolidBrush(Theme.TextSecondary))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = plot.Top + plot.Height * i / 4f;
                    g.DrawLine(pen, plot.Left, y, plot.Right, y);
                    g.DrawString((span - 2 * span * i / 4).ToString("F2") + "%", Theme.FMonoSmall, brush, plot.Right + 2, y - 7);
                }
                string[] labels = { "09:30", "11:30 / 13:00", "15:00" };
                for (int i = 0; i < labels.Length; i++) g.DrawString(labels[i], Theme.FSmall, brush, plot.Left + i * plot.Width / 2f - (i == 1 ? 38 : i == 2 ? 32 : 0), plot.Bottom + 4);
            }
            var line = new List<PointF>();
            foreach (MinutePoint point in _minute)
            {
                TimeSpan time; if (!TimeSpan.TryParse(point.Time, out time)) continue;
                double minute = time.TotalMinutes - 570;
                if (time.TotalMinutes >= 780) minute -= 90;
                if (minute < 0 || minute > 240) continue;
                line.Add(new PointF(plot.Left + (float)(minute / 240 * plot.Width), PriceToY(point.Price, plot, -span, span)));
            }
            if (line.Count > 1) using (var pen = new Pen(Theme.Accent, 1.8f)) g.DrawLines(pen, line.ToArray());
            DrawCaption(g, plot);
        }

        private Rectangle CalcPlot()
        {
            int left = 8;
            int right = 56;      // 价格刻度
            int top = 22;        // 图题
            int bottom = 46;     // 量柱 + 时间刻度
            return new Rectangle(left, top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));
        }

        // ── 分时 ──

        private void DrawMinute(Graphics g, Rectangle plot)
        {
            if (_minute.Count == 0)
            {
                DrawEmpty(g);
                return;
            }

            double min = double.MaxValue;
            double max = double.MinValue;
            for (int i = 0; i < _minute.Count; i++)
            {
                double price = _minute[i].Price;
                if (price <= 0) continue;
                if (price < min) min = price;
                if (price > max) max = price;
            }
            if (min > max) { DrawEmpty(g); return; }
            if (_prevClose > 0)
            {
                double span = Math.Max(max - _prevClose, _prevClose - min);
                if (span <= 0) span = _prevClose * 0.01;
                min = _prevClose - span;
                max = _prevClose + span;
            }
            double pad = Math.Max((max - min) * 0.06, 0.001);
            min -= pad;
            max += pad;

            DrawGrid(g, plot, min, max, _prevClose);
            DrawVolume(g, plot, null, true);

            // 价格折线
            List<PointF> line = new List<PointF>();
            List<PointF> avg = new List<PointF>();
            float stepX = _minute.Count > 1 ? (float)plot.Width / (_minute.Count - 1) : 0;
            for (int i = 0; i < _minute.Count; i++)
            {
                float x = plot.Left + i * stepX;
                if (_minute[i].Price > 0)
                    line.Add(new PointF(x, PriceToY(_minute[i].Price, plot, min, max)));
                if (_minute[i].AvgPrice > 0)
                    avg.Add(new PointF(x, PriceToY(_minute[i].AvgPrice, plot, min, max)));
            }

            if (line.Count > 1)
            {
                using (Pen pen = new Pen(Theme.Accent, 1.5f))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, line.ToArray());
                }
                // 折线下方淡淡填充，强调「价格走廊」
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddLines(line.ToArray());
                    path.AddLine(line[line.Count - 1].X, plot.Bottom, line[0].X, plot.Bottom);
                    path.CloseFigure();
                    using (SolidBrush brush = new SolidBrush(Theme.CA(0x58a6ff, 0.10f)))
                        g.FillPath(brush, path);
                }
            }
            if (avg.Count > 1)
            {
                using (Pen pen = new Pen(Theme.Warning, 1f))
                    g.DrawLines(pen, avg.ToArray());
            }

            DrawTimeLabels(g, plot, _minute.Count, true);
            DrawCaption(g, plot);
        }

        // ── K 线 ──

        private void DrawCandles(Graphics g, Rectangle plot)
        {
            if (_bars.Count == 0)
            {
                DrawEmpty(g);
                return;
            }

            int visible = Math.Min(_bars.Count, Math.Max(10, plot.Width / MinCandleWidth));
            int start = _bars.Count - visible;

            double min = double.MaxValue;
            double max = double.MinValue;
            for (int i = start; i < _bars.Count; i++)
            {
                if (_bars[i].Low < min) min = _bars[i].Low;
                if (_bars[i].High > max) max = _bars[i].High;
            }
            if (min > max) { DrawEmpty(g); return; }
            double pad = Math.Max((max - min) * 0.05, 0.001);
            min -= pad;
            max += pad;

            DrawGrid(g, plot, min, max, 0);
            DrawVolume(g, plot, start, false);

            float stepX = (float)plot.Width / visible;
            float bodyWidth = Math.Max(1f, stepX * 0.7f);

            for (int i = start; i < _bars.Count; i++)
            {
                KLineBar bar = _bars[i];
                float cx = plot.Left + (i - start) * stepX + stepX / 2f;
                float yOpen = PriceToY(bar.Open, plot, min, max);
                float yClose = PriceToY(bar.Close, plot, min, max);
                float yHigh = PriceToY(bar.High, plot, min, max);
                float yLow = PriceToY(bar.Low, plot, min, max);

                bool up = bar.Close >= bar.Open;
                Color color = up ? Theme.Up : Theme.Down;

                using (Pen wick = new Pen(color, 1f))
                    g.DrawLine(wick, cx, yHigh, cx, yLow);

                float top = Math.Min(yOpen, yClose);
                float height = Math.Max(1f, Math.Abs(yClose - yOpen));
                using (SolidBrush brush = new SolidBrush(color))
                {
                    if (up)
                    {
                        // 阳线：空心（A 股习惯），用描边表示
                        using (Pen border = new Pen(color, 1f))
                            g.DrawRectangle(border, cx - bodyWidth / 2f, top, bodyWidth, height);
                    }
                    else
                    {
                        g.FillRectangle(brush, cx - bodyWidth / 2f, top, bodyWidth, height);
                    }
                }
            }

            DrawMovingAverage(g, plot, min, max, start, visible, stepX, 5);
            DrawMovingAverage(g, plot, min, max, start, visible, stepX, 10);
            DrawMovingAverage(g, plot, min, max, start, visible, stepX, 20);

            DrawTimeLabels(g, plot, _bars.Count, false);
            DrawCaption(g, plot);
        }

        private void DrawMovingAverage(Graphics g, Rectangle plot, double min, double max, int start, int visible, float stepX, int period)
        {
            List<PointF> points = new List<PointF>();
            for (int i = start; i < _bars.Count; i++)
            {
                if (i - period + 1 < 0) continue;
                double sum = 0;
                for (int k = 0; k < period; k++) sum += _bars[i - k].Close;
                float cx = plot.Left + (i - start) * stepX + stepX / 2f;
                points.Add(new PointF(cx, PriceToY(sum / period, plot, min, max)));
            }
            if (points.Count < 2) return;

            Color color = period == 5 ? Theme.CA(0xffd479, 0.95f)
                : period == 10 ? Theme.CA(0x79c0ff, 0.95f)
                : Theme.CA(0xd2a8ff, 0.95f);
            using (Pen pen = new Pen(color, 1f))
                g.DrawLines(pen, points.ToArray());
        }

        // ── 公共绘制 ──

        private float PriceToY(double price, Rectangle plot, double min, double max)
        {
            if (max <= min) return plot.Bottom;
            double ratio = (price - min) / (max - min);
            return (float)(plot.Bottom - ratio * plot.Height);
        }

        private void DrawGrid(Graphics g, Rectangle plot, double min, double max, double reference)
        {
            using (Pen grid = new Pen(Theme.CA(Theme.Dark ? 0xffffff : 0x000000, 0.06f), 1f))
            using (SolidBrush textBrush = new SolidBrush(Theme.TextTertiary))
            using (StringFormat right = Theme.RightMiddle())
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = plot.Top + plot.Height * i / 4f;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    double price = max - (max - min) * i / 4.0;
                    g.DrawString(price.ToString(price >= 100 ? "F2" : "F3"),
                        Theme.FMonoSmall, textBrush,
                        new RectangleF(plot.Right + 4, y - 8, 50, 16), right);
                }
            }

            if (reference > 0 && reference >= min && reference <= max)
            {
                float y = PriceToY(reference, plot, min, max);
                using (Pen dashed = new Pen(Theme.CA(0x8b949e, 0.8f), 1f))
                {
                    dashed.DashStyle = DashStyle.Dash;
                    g.DrawLine(dashed, plot.Left, y, plot.Right, y);
                }
            }
        }

        private void DrawVolume(Graphics g, Rectangle plot, int? startIndex, bool minute)
        {
            int areaHeight = 34;
            int top = plot.Bottom + 6;

            long maxVolume = 1;
            int count = minute ? _minute.Count : _bars.Count;
            int start = startIndex.HasValue ? startIndex.Value : 0;
            for (int i = start; i < count; i++)
            {
                long volume = minute ? _minute[i].Volume : _bars[i].Volume;
                if (volume > maxVolume) maxVolume = volume;
            }
            if (count - start <= 0) return;

            float stepX = count - start > 1 ? (float)plot.Width / (count - start) : plot.Width;
            float barWidth = Math.Max(1f, stepX * (minute ? 0.8f : 0.7f));

            for (int i = start; i < count; i++)
            {
                long volume = minute ? _minute[i].Volume : _bars[i].Volume;
                if (volume <= 0) continue;
                float h = (float)((double)volume / maxVolume * areaHeight);
                float cx = plot.Left + (i - start) * stepX;

                Color color;
                if (minute)
                {
                    double prev = i > 0 ? _minute[i - 1].Price : _minute[i].Open;
                    color = _minute[i].Price >= prev ? Theme.CA(0xf85149, 0.55f) : Theme.CA(0x3fb950, 0.55f);
                }
                else
                {
                    color = _bars[i].Close >= _bars[i].Open ? Theme.CA(0xf85149, 0.55f) : Theme.CA(0x3fb950, 0.55f);
                }
                using (SolidBrush brush = new SolidBrush(color))
                    g.FillRectangle(brush, cx, top + areaHeight - h, barWidth, h);
            }
        }

        private void DrawTimeLabels(Graphics g, Rectangle plot, int count, bool minute)
        {
            if (count <= 0) return;
            using (SolidBrush brush = new SolidBrush(Theme.TextTertiary))
            using (StringFormat left = Theme.LeftMiddle())
            using (StringFormat right = Theme.RightMiddle())
            using (StringFormat center = Theme.CenterMiddle())
            {
                float y = plot.Bottom + 12;
                string first = Label(0, minute);
                string middle = Label(count / 2, minute);
                string last = Label(count - 1, minute);

                g.DrawString(first, Theme.FMonoSmall, brush, new RectangleF(plot.Left, y, 90, 16), left);
                g.DrawString(middle, Theme.FMonoSmall, brush, new RectangleF(plot.Left + plot.Width / 2f - 45, y, 90, 16), center);
                g.DrawString(last, Theme.FMonoSmall, brush, new RectangleF(plot.Right - 90, y, 90, 16), right);
            }
        }

        private string Label(int index, bool minute)
        {
            if (minute)
            {
                if (index < 0 || index >= _minute.Count) return "";
                return _minute[index].Time;
            }
            if (index < 0 || index >= _bars.Count) return "";
            string date = _bars[index].Date ?? "";
            return date.Length >= 10 ? date.Substring(0, 10) : date;
        }

        private void DrawCaption(Graphics g, Rectangle plot)
        {
            if (string.IsNullOrEmpty(_caption)) return;
            using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
            using (StringFormat left = Theme.LeftMiddle())
                g.DrawString(_caption, Theme.FSmall, brush, new RectangleF(plot.Left, 2, plot.Width, 18), left);
        }

        private void DrawEmpty(Graphics g)
        {
            using (SolidBrush brush = new SolidBrush(Theme.TextTertiary))
            using (StringFormat center = Theme.CenterMiddle())
                g.DrawString(_emptyText, Theme.FNormal, brush, new RectangleF(0, 0, Width, Height), center);
        }
    }
}
