using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace QuantNative.UI
{
    /// <summary>
    /// 一套配色方案。涨跌色遵循 A 股惯例：红涨绿跌。
    ///
    /// 2.4.0 起新增 Card / CardBorder / AccentSoft 三个字段，用来画「卡片」：
    ///   Card       —— 卡片／面板的底色（比页面底色亮一档，形成卡片浮起感）
    ///   CardBorder —— 卡片描边（很浅，只用于分隔，不做重边框）
    ///   AccentSoft —— 强调色的浅底，用于选中态、标签、药丸底色
    /// </summary>
    public sealed class Palette
    {
        public string Key;
        public string Name;
        public bool Dark;
        public int Bg, Surface1, Surface2, Surface3, Border0, Border1;
        public int TextPrimary, TextSecondary, TextTertiary;
        public int Accent, Warning, Up, Down;
        public int Card, CardBorder, AccentSoft;

        /// <summary>卡片底色。老配色没单独定义时退回 Surface1。</summary>
        public Color CardColor { get { return Theme.C(Card != 0 ? Card : Surface1); } }
        public Color CardBorderColor { get { return Theme.C(CardBorder != 0 ? CardBorder : Border0); } }
        public Color AccentSoftColor
        {
            get
            {
                if (AccentSoft != 0) return Theme.C(AccentSoft);
                return Theme.Dark ? Color.FromArgb(38, 68, 102) : Color.FromArgb(211, 231, 252);
            }
        }
    }

    /// <summary>
    /// 配色与字体。
    ///
    /// 默认方案为「养基宝·浅色」——白底卡片 + 橙红强调色 + 大号加粗数值，
    /// 信息层级参照主流基金 App：名称弱、数值强、涨跌用色块。
    /// 方案由 AppHost 持久化到 settings 的 theme 键，切换后立刻生效（含浮窗）。
    /// </summary>
    public static class Theme
    {
        /// <summary>当前是否暗色（由 UseScheme 维护，供少数需要判断明暗的绘制代码使用）。</summary>
        public static bool Dark = true;

        /// <summary>当前方案 key。</summary>
        public static string SchemeKey { get; private set; }

        /// <summary>可选方案（设置页按此顺序展示，第一项为默认）。</summary>
        public static readonly Palette[] Schemes = new Palette[]
        {
            // ── 养基宝（默认）：白底卡片 + 橙红强调 ──
            new Palette { Key = "yangjibao", Name = "养基宝·浅色（推荐）", Dark = false,
                Bg = 0xf4f5f7, Surface1 = 0xffffff, Surface2 = 0xf2f4f7, Surface3 = 0xe7ebf1,
                Border0 = 0xeceff4, Border1 = 0xdadfe8,
                TextPrimary = 0x1a1a1a, TextSecondary = 0x6b7280, TextTertiary = 0x9ca3af,
                Accent = 0xff5b32, Warning = 0xe0a02c, Up = 0xe23b3b, Down = 0x12a45b,
                Card = 0xffffff, CardBorder = 0xeceff4, AccentSoft = 0xffede6 },

            new Palette { Key = "yangjibao-dark", Name = "养基宝·深色", Dark = true,
                Bg = 0x111114, Surface1 = 0x1b1b20, Surface2 = 0x25252c, Surface3 = 0x31313a,
                Border0 = 0x2a2a32, Border1 = 0x3b3b46,
                TextPrimary = 0xf5f5f7, TextSecondary = 0xa3a3ae, TextTertiary = 0x787881,
                Accent = 0xff7a50, Warning = 0xdfb15c, Up = 0xff6060, Down = 0x2ecc8f,
                Card = 0x1b1b20, CardBorder = 0x2e2e37, AccentSoft = 0x3a241c },

            // ── 原有配色保留，卡片色按各自底色派生 ──
            new Palette { Key = "dark", Name = "GitHub 暗色", Dark = true,
                Bg = 0x0d1117, Surface1 = 0x161b22, Surface2 = 0x1c2333, Surface3 = 0x252d3f,
                Border0 = 0x1e293b, Border1 = 0x30363d,
                TextPrimary = 0xe6edf3, TextSecondary = 0x8b949e, TextTertiary = 0x6e7681,
                Accent = 0x58a6ff, Warning = 0xd29922, Up = 0xf85149, Down = 0x3fb950,
                Card = 0x161b22, CardBorder = 0x1e293b, AccentSoft = 0x22334a },

            new Palette { Key = "light", Name = "浅色（GitHub）", Dark = false,
                Bg = 0xffffff, Surface1 = 0xf6f8fa, Surface2 = 0xeef1f5, Surface3 = 0xe2e5ea,
                Border0 = 0xd0d7de, Border1 = 0xc0c7cf,
                TextPrimary = 0x1f2328, TextSecondary = 0x656d76, TextTertiary = 0x8b949e,
                Accent = 0x0969da, Warning = 0x9a6700, Up = 0xd1242f, Down = 0x1a7f64,
                Card = 0xffffff, CardBorder = 0xd8dee6, AccentSoft = 0xd3e7fc },

            new Palette { Key = "midnight", Name = "深空蓝", Dark = true,
                Bg = 0x080e1c, Surface1 = 0x0f1830, Surface2 = 0x152142, Surface3 = 0x1c2b52,
                Border0 = 0x1e2d4d, Border1 = 0x2b3d63,
                TextPrimary = 0xdce7f7, TextSecondary = 0x8ba0c4, TextTertiary = 0x6a7fa3,
                Accent = 0x4d9fff, Warning = 0xd7a838, Up = 0xff6b6b, Down = 0x35c46f,
                Card = 0x0f1830, CardBorder = 0x1e2d4d, AccentSoft = 0x1b2a4a },

            new Palette { Key = "graphite", Name = "石墨黑", Dark = true,
                Bg = 0x0c0c0c, Surface1 = 0x151515, Surface2 = 0x1d1d1d, Surface3 = 0x262626,
                Border0 = 0x232323, Border1 = 0x333333,
                TextPrimary = 0xe8e8e8, TextSecondary = 0x9a9a9a, TextTertiary = 0x777777,
                Accent = 0xa8bccd, Warning = 0xc9a227, Up = 0xef5350, Down = 0x4caf50,
                Card = 0x151515, CardBorder = 0x232323, AccentSoft = 0x2b2f33 },

            new Palette { Key = "forest", Name = "墨绿", Dark = true,
                Bg = 0x07130e, Surface1 = 0x0e1f18, Surface2 = 0x142a20, Surface3 = 0x1b362a,
                Border0 = 0x1a2f25, Border1 = 0x27443a,
                TextPrimary = 0xdcf0e6, TextSecondary = 0x86a897, TextTertiary = 0x668876,
                Accent = 0x3fbf87, Warning = 0xd2a63a, Up = 0xf2554f, Down = 0x35c07a,
                Card = 0x0e1f18, CardBorder = 0x1a2f25, AccentSoft = 0x1b3a2e },

            new Palette { Key = "sepia", Name = "暖褐", Dark = true,
                Bg = 0x16110c, Surface1 = 0x211a13, Surface2 = 0x2b221a, Surface3 = 0x352a20,
                Border0 = 0x2e241a, Border1 = 0x40321f,
                TextPrimary = 0xf0e6d8, TextSecondary = 0xb3a08a, TextTertiary = 0x8d7b66,
                Accent = 0xd9a441, Warning = 0xd8a13a, Up = 0xff6b5e, Down = 0x63c07a,
                Card = 0x211a13, CardBorder = 0x2e241a, AccentSoft = 0x3a2c1c }
        };

        /// <summary>默认方案 key（新装 / 首次升级到这个版本时使用）。</summary>
        public const string DefaultScheme = "yangjibao";

        private static Palette _current = Schemes[0];

        /// <summary>切换配色方案；无法识别的 key 退回默认方案。返回是否发生切换。</summary>
        public static bool UseScheme(string key)
        {
            Palette palette = Find(key);
            if (palette == null) palette = Schemes[0];
            bool changed = !ReferenceEquals(_current, palette);
            _current = palette;
            SchemeKey = palette.Key;
            Dark = palette.Dark;
            return changed;
        }

        public static Palette Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < Schemes.Length; i++)
                if (string.Equals(Schemes[i].Key, key, StringComparison.OrdinalIgnoreCase)) return Schemes[i];
            return null;
        }

        public static Palette Current { get { return _current; } }

        // ── 颜色 ──
        public static Color Bg { get { return C(_current.Bg); } }
        public static Color Surface1 { get { return C(_current.Surface1); } }
        public static Color Surface2 { get { return C(_current.Surface2); } }
        public static Color Surface3 { get { return C(_current.Surface3); } }
        public static Color Border0 { get { return C(_current.Border0); } }
        public static Color Border1 { get { return C(_current.Border1); } }

        /// <summary>卡片底色（列表容器 / 面板块 / 指数卡）。</summary>
        public static Color Card { get { return _current.CardColor; } }

        /// <summary>卡片描边（很浅，仅用于描出卡片边界）。</summary>
        public static Color CardBorder { get { return _current.CardBorderColor; } }

        public static Color TextPrimary { get { return C(_current.TextPrimary); } }
        public static Color TextSecondary { get { return C(_current.TextSecondary); } }
        public static Color TextTertiary { get { return C(_current.TextTertiary); } }

        /// <summary>红涨绿跌（A 股惯例，与欧美相反）。</summary>
        public static Color Up { get { return C(_current.Up); } }
        public static Color Down { get { return C(_current.Down); } }

        public static Color Accent { get { return C(_current.Accent); } }
        // WinForms cells/buttons do not alpha-compose their backgrounds reliably.
        public static Color AccentDim { get { return _current.AccentSoftColor; } }
        public static Color Warning { get { return C(_current.Warning); } }
        public static Color Error { get { return C(_current.Up); } }

        /// <summary>悬停底色（表格行、列表项）。</summary>
        public static Color Hover { get { return Dark ? CA(0xffffff, 0.05f) : CA(0x000000, 0.04f); } }

        /// <summary>斑马纹行底色（浅色主题下让长列表更易横向追踪）。</summary>
        public static Color RowAlt { get { return Dark ? CA(0xffffff, 0.022f) : CA(0x000000, 0.017f); } }

        /// <summary>弱分隔线（表格横线、卡片内部分隔）。带 alpha，适合 GDI+ 绘制。</summary>
        public static Color Divider { get { return Dark ? CA(0xffffff, 0.07f) : CA(0x000000, 0.06f); } }

        /// <summary>
        /// 不透明的分隔线色。DataGridView.GridColor 这类属性不接受带 alpha 的颜色
        /// （会抛「不能将 GridColor 颜色设置为透明色」），所以这里把半透明色按底色预先合成。
        /// </summary>
        public static Color DividerSolid { get { return Blend(Divider, Surface1); } }

        /// <summary>斑马纹行底色的不透明版本（表格单元格背景同样不能依赖 alpha 合成）。</summary>
        public static Color RowAltSolid { get { return Blend(RowAlt, Surface1); } }

        /// <summary>把带 alpha 的前景色合成到不透明底色上，得到一个等效的实色。</summary>
        public static Color Blend(Color foreground, Color background)
        {
            float a = foreground.A / 255f;
            return Color.FromArgb(255,
                (int)Math.Round(background.R * (1 - a) + foreground.R * a),
                (int)Math.Round(background.G * (1 - a) + foreground.G * a),
                (int)Math.Round(background.B * (1 - a) + foreground.B * a));
        }

        /// <summary>浮窗底图 RGB（不带 alpha，alpha 由设置里的不透明度决定）。</summary>
        public static Color TickerPlate { get { return C(_current.Surface1); } }

        /// <summary>按涨跌取色（0 视为上涨色，与旧版一致）。</summary>
        public static Color Pct(double? changePct)
        {
            if (!changePct.HasValue) return TextTertiary;
            return changePct.Value >= 0 ? Up : Down;
        }

        /// <summary>涨跌对应的「浅底」色块（数值药丸底色）。</summary>
        public static Color PctSoft(double? changePct)
        {
            if (!changePct.HasValue) return Hover;
            Color baseColor = Pct(changePct);
            return Color.FromArgb(Dark ? 46 : 26, baseColor);
        }

        // ── 字体 ──
        // 微软雅黑 UI 做界面、Consolas 做数字（等宽保证小数点对齐）。
        // 浮窗可以在设置里换成用户本机的任意字体，这里保留可覆盖的族名。
        public const string DefaultUiFamily = "Microsoft YaHei UI";
        public const string DefaultMonoFamily = "Consolas";

        private static string _uiFamily = DefaultUiFamily;
        private static string _monoFamily = DefaultMonoFamily;

        /// <summary>界面字体族名（默认微软雅黑 UI）。</summary>
        public static string UiFamily
        {
            get { return _uiFamily; }
            set { _uiFamily = Normalize(value, DefaultUiFamily); }
        }

        /// <summary>数字字体族名（默认 Consolas，等宽）。</summary>
        public static string MonoFamily
        {
            get { return _monoFamily; }
            set { _monoFamily = Normalize(value, DefaultMonoFamily); }
        }

        /// <summary>本机是否装了某个字体族（空名视为「用默认」）。</summary>
        public static bool FamilyAvailable(string family)
        {
            if (string.IsNullOrEmpty(family)) return false;
            try
            {
                using (Font probe = new Font(family, 9f))
                    return string.Equals(probe.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string Normalize(string family, string fallback)
        {
            string name = (family ?? "").Trim();
            return name.Length == 0 ? fallback : name;
        }

        public static Font Ui(float size, FontStyle style)
        {
            return Create(_uiFamily, size, style, FontFamily.GenericSansSerif);
        }

        /// <summary>用指定的本机字体族做界面字体（浮窗专用，失败自动退回默认）。</summary>
        public static Font Ui(float size, FontStyle style, string family)
        {
            return Create(family, size, style, null) ?? Ui(size, style);
        }

        public static Font Mono(float size, FontStyle style)
        {
            return Create(_monoFamily, size, style, FontFamily.GenericMonospace);
        }

        /// <summary>用指定的本机字体族做数字字体（浮窗专用，失败自动退回默认）。</summary>
        public static Font Mono(float size, FontStyle style, string family)
        {
            return Create(family, size, style, null) ?? Mono(size, style);
        }

        /// <summary>
        /// 按字体族名创建字体；名称为空或该族不存在时返回 null（由调用方决定退回哪一个）。
        /// 传了 fallback 时直接给出可用字体，不产生中间 Font 对象。
        /// </summary>
        public static Font Create(string family, float size, FontStyle style, FontFamily fallback)
        {
            string name = (family ?? "").Trim();
            if (name.Length > 0)
            {
                try { return new Font(name, size, style); }
                catch { }
            }
            return fallback == null ? null : new Font(fallback, size, style);
        }

        /// <summary>
        /// 本机可用字体族名（已排序，去重）。供设置页的下拉框使用。
        /// 过滤掉以 @ 开头的竖排字体，避免用户误选后文字旋转 90°。
        /// </summary>
        public static string[] InstalledFamilies()
        {
            List<string> names = new List<string>();
            try
            {
                using (InstalledFontCollection fonts = new InstalledFontCollection())
                {
                    for (int i = 0; i < fonts.Families.Length; i++)
                    {
                        string name = fonts.Families[i].Name;
                        if (string.IsNullOrEmpty(name) || name[0] == '@') continue;
                        names.Add(name);
                    }
                }
            }
            catch { }
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names.ToArray();
        }

        // ── 字号层级 ──
        // 层级规则（参照主流基金 App）：名称/标签弱而小，净值与涨跌幅大而粗，
        // 同一行里「数值 ≫ 名称」的字号差制造主次关系。
        public static readonly Font FSmall = Ui(8.75f, FontStyle.Regular);
        public static readonly Font FNormal = Ui(9.5f, FontStyle.Regular);
        public static readonly Font FBold = Ui(9.5f, FontStyle.Bold);
        public static readonly Font FTitle = Ui(13f, FontStyle.Bold);
        public static readonly Font FSection = Ui(10.5f, FontStyle.Bold);
        public static readonly Font FMono = Mono(9.5f, FontStyle.Regular);
        public static readonly Font FMonoBold = Mono(9.5f, FontStyle.Bold);
        public static readonly Font FMonoSmall = Mono(8.75f, FontStyle.Regular);

        /// <summary>标签 / 药丸上的小号粗体。</summary>
        public static readonly Font FTag = Ui(8f, FontStyle.Bold);
        /// <summary>表头 / 分组标题。</summary>
        public static readonly Font FHeader = Ui(9f, FontStyle.Bold);
        /// <summary>次级说明文字。</summary>
        public static readonly Font FHint = Ui(8.25f, FontStyle.Regular);
        /// <summary>关键数值（表格里的现价、涨跌幅）。</summary>
        public static readonly Font FValue = Mono(11.5f, FontStyle.Bold);
        /// <summary>大号数值（指数卡、基金净值、详情页标题数字）。</summary>
        public static readonly Font FValueLarge = Mono(14f, FontStyle.Bold);
        /// <summary>超大数值（概览页成交额等门面数字）。</summary>
        public static readonly Font FDisplay = Mono(17f, FontStyle.Bold);

        // ── 绘图小工具 ──

        public static Color C(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static Color CA(int rgb, float alpha)
        {
            int a = (int)Math.Round(Math.Max(0f, Math.Min(1f, alpha)) * 255f);
            return Color.FromArgb(a, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        /// <summary>系统 DPI 缩放系数（96 DPI = 1.0）。</summary>
        public static float Scale(System.Windows.Forms.Control control)
        {
            try
            {
                int dpi = control.DeviceDpi;
                if (dpi <= 0) dpi = 96;
                return dpi / 96f;
            }
            catch
            {
                return 1f;
            }
        }

        public static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            int d = radius * 2;
            d = Math.Min(d, Math.Min(rect.Width, rect.Height));
            if (d <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>画一张卡片：圆角填充 + 1px 浅描边。所有面板块统一走这里。</summary>
        public static void DrawCard(Graphics g, Rectangle rect, int radius)
        {
            DrawCard(g, rect, radius, Card, CardBorder);
        }

        public static void DrawCard(Graphics g, Rectangle rect, int radius, Color fill, Color border)
        {
            if (rect.Width <= 1 || rect.Height <= 1) return;
            using (GraphicsPath path = RoundedRect(rect, radius))
            {
                using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (border.A > 0)
                {
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, path);
                }
            }
        }

        /// <summary>画一个两端全圆的药丸（标签 / 涨跌幅色块）。</summary>
        public static void DrawPill(Graphics g, Rectangle rect, Color fill)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            int radius = Math.Max(1, Math.Min(rect.Height / 2, rect.Width / 2));
            using (GraphicsPath path = RoundedRect(rect, radius))
            using (SolidBrush brush = new SolidBrush(fill))
                g.FillPath(brush, path);
        }

        /// <summary>药丸 + 居中文字（涨跌幅色块用，文字自动取对比色）。</summary>
        public static void DrawPillText(Graphics g, Rectangle rect, Color fill, Color textColor, string text, Font font)
        {
            DrawPill(g, rect, fill);
            using (SolidBrush brush = new SolidBrush(textColor))
            using (StringFormat format = CenterMiddle())
                g.DrawString(text ?? "", font, brush, rect, format);
        }

        /// <summary>区块左侧的强调竖条（卡片标题前的装饰）。</summary>
        public static void DrawAccentBar(Graphics g, Rectangle rect)
        {
            int radius = Math.Max(1, rect.Width / 2);
            using (GraphicsPath path = RoundedRect(rect, radius))
            using (SolidBrush brush = new SolidBrush(Accent))
                g.FillPath(brush, path);
        }

        /// <summary>按矩形高度取一个「上下留白」的内缩矩形（文字垂直居中用）。</summary>
        public static RectangleF Inset(Rectangle rect, int x, int y)
        {
            return new RectangleF(rect.X + x, rect.Y + y, Math.Max(1, rect.Width - x * 2), Math.Max(1, rect.Height - y * 2));
        }

        /// <summary>
        /// 把矩形高度撑到至少「字体行高 + pad」，并保持垂直居中。
        ///
        /// 必须这么做的原因：GDI+ 的 DrawString 在矩形比字体行高还矮时，会**整行都不绘制**
        /// （不是裁切、也不是挤扁），看上去就是数字凭空消失。浮窗/概览这类「门面数字」用的
        /// 字号大（17pt 行高 27px），随手给一个 24px 的框就会踩这个坑，所有大号数字都要走这里。
        /// </summary>
        public static RectangleF TextRow(Rectangle rect, Font font, int pad)
        {
            if (font == null) return rect;
            float height = Math.Max(rect.Height, font.Height + pad);
            return new RectangleF(rect.X, rect.Y + (rect.Height - height) / 2f, rect.Width, height);
        }

        /// <summary>文本测量（避免每个绘制点重复 new StringFormat）。</summary>
        public static StringFormat LeftMiddle()
        {
            StringFormat f = new StringFormat(StringFormat.GenericTypographic);
            f.Alignment = StringAlignment.Near;
            f.LineAlignment = StringAlignment.Center;
            f.FormatFlags |= StringFormatFlags.NoWrap;
            f.Trimming = StringTrimming.EllipsisCharacter;
            return f;
        }

        public static StringFormat RightMiddle()
        {
            StringFormat f = LeftMiddle();
            f.Alignment = StringAlignment.Far;
            return f;
        }

        public static StringFormat CenterMiddle()
        {
            StringFormat f = LeftMiddle();
            f.Alignment = StringAlignment.Center;
            return f;
        }

        /// <summary>文本宽度（绘制前预估列宽，代替硬编码像素）。</summary>
        public static int Measure(Graphics g, string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(g.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width);
        }

        /// <summary>一批文本里的最大宽度（用于按内容排版的列）。</summary>
        public static int MeasureMax(Graphics g, IEnumerable<string> texts, Font font)
        {
            int max = 0;
            foreach (string text in texts)
            {
                int width = Measure(g, text, font);
                if (width > max) max = width;
            }
            return max;
        }

        /// <summary>把方案列表转成下拉框用的显示名数组。</summary>
        public static string[] SchemeNames()
        {
            List<string> names = new List<string>();
            for (int i = 0; i < Schemes.Length; i++) names.Add(Schemes[i].Name);
            return names.ToArray();
        }

        /// <summary>按方案 key 找下拉框索引。</summary>
        public static int SchemeIndex(string key)
        {
            for (int i = 0; i < Schemes.Length; i++)
                if (string.Equals(Schemes[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }
    }
}
