using System;
using System.Drawing;
using System.Windows.Forms;

namespace QuantNative.UI
{
    /// <summary>
    /// DataGridView 统一外观。WinForms 的表格默认是系统灰白风格，这里改成应用配色；
    /// 主题切换后调用 <see cref="RefreshTheme"/> 重新套色。
    ///
    /// 2.4.0 的字号层级：表头小号粗体（弱），正文常规（中），
    /// 关键数值列（现价 / 涨跌幅）用加粗等宽字体（强）—— 一列数字扫过去，
    /// 数值比名称更抢眼，这是基金类 App 的标准信息层级。
    /// </summary>
    public static class GridStyle
    {
        public static void Apply(DataGridView grid)
        {
            float scale = Theme.Scale(grid);
            grid.BackgroundColor = Theme.Surface1;
            grid.BorderStyle = BorderStyle.None;
            // GridColor / 单元格背景都不接受带 alpha 的颜色，这里用预合成的实色
            grid.GridColor = Theme.DividerSolid;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = (int)Math.Round(36 * scale);
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.ReadOnly = true;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.ScrollBars = ScrollBars.Both;
            grid.RowTemplate.Height = (int)Math.Round(34 * scale);
            grid.ShowCellToolTips = true;
            grid.DoubleBuffered(true);
            RefreshTheme(grid);
        }

        public static void RefreshTheme(DataGridView grid)
        {
            float scale = Theme.Scale(grid);
            grid.BackgroundColor = Theme.Surface1;
            grid.GridColor = Theme.DividerSolid;
            grid.ColumnHeadersHeight = Math.Max(grid.ColumnHeadersHeight, (int)Math.Round(36 * scale));
            grid.RowTemplate.Height = (int)Math.Round(34 * scale);

            DataGridViewCellStyle header = grid.ColumnHeadersDefaultCellStyle;
            header.BackColor = Theme.Surface1;          // 表头与内容同底，靠一条分隔线区分（卡片观感）
            header.ForeColor = Theme.TextTertiary;
            header.SelectionBackColor = Theme.Surface1;
            header.SelectionForeColor = Theme.TextTertiary;
            header.Font = Theme.FHeader;
            header.Alignment = DataGridViewContentAlignment.MiddleLeft;
            header.Padding = new Padding(8, 0, 0, 0);

            DataGridViewCellStyle cell = grid.DefaultCellStyle;
            cell.BackColor = Theme.Surface1;
            cell.ForeColor = Theme.TextPrimary;
            cell.SelectionBackColor = Theme.AccentDim;
            cell.SelectionForeColor = Theme.TextPrimary;
            cell.Font = Theme.FNormal;
            cell.Padding = new Padding(8, 0, 8, 0);

            DataGridViewCellStyle alt = grid.AlternatingRowsDefaultCellStyle;
            alt.BackColor = Theme.RowAltSolid;
            alt.ForeColor = Theme.TextPrimary;
            alt.SelectionBackColor = Theme.AccentDim;
            alt.SelectionForeColor = Theme.TextPrimary;

            grid.Refresh();
        }

        /// <summary>数值列（等宽字体 + 右对齐）。</summary>
        public static DataGridViewTextBoxColumn NumberColumn(string header, int width)
        {
            return NumberColumn(header, width, false);
        }

        /// <summary>关键数值列（加粗大号等宽——净值 / 涨跌幅用）。</summary>
        public static DataGridViewTextBoxColumn EmphasisColumn(string header, int width)
        {
            return NumberColumn(header, width, true);
        }

        private static DataGridViewTextBoxColumn NumberColumn(string header, int width, bool emphasis)
        {
            DataGridViewTextBoxColumn column = TextColumn(header, width);
            column.DefaultCellStyle.Font = emphasis ? Theme.FValue : Theme.FMono;
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            return column;
        }

        public static DataGridViewTextBoxColumn TextColumn(string header, int width)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.HeaderText = header;
            column.Width = width;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            column.Resizable = DataGridViewTriState.False;
            return column;
        }

        /// <summary>让 DataGridView 用双缓冲绘制（避免滚动时闪烁）。</summary>
        public static void DoubleBuffered(this DataGridView grid, bool enabled)
        {
            typeof(DataGridView)
                .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(grid, enabled, null);
        }

        /// <summary>按整格的涨跌给单元格上色（红涨绿跌）。</summary>
        public static void ColorByPct(DataGridViewCellFormattingEventArgs e, bool up)
        {
            e.CellStyle.ForeColor = up ? Theme.Up : Theme.Down;
        }
    }

    /// <summary>常用小控件构造（统一颜色，避免每处重复设置）。</summary>
    public static class Ui
    {
        public static Label Label(string text, Font font, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = font;
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.AutoSize = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        public static Button Button(string text, EventHandler onClick)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = Theme.FNormal;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Theme.Border1;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Theme.Surface2;
            button.BackColor = Theme.Surface1;
            button.ForeColor = Theme.TextSecondary;
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            button.AutoSize = false;
            button.Padding = new Padding(4, 0, 4, 0);
            button.AutoEllipsis = false;
            if (onClick != null) button.Click += onClick;
            return button;
        }

        /// <summary>主按钮（强调色实底 + 白字），用于「添加自选」「保存」这类主操作。</summary>
        public static Button PrimaryButton(string text, EventHandler onClick)
        {
            Button button = Button(text, onClick);
            button.BackColor = Theme.Accent;
            button.ForeColor = Color.White;
            button.Font = Theme.FBold;
            button.FlatAppearance.BorderColor = Theme.Accent;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.Accent;
            return button;
        }

        /// <summary>分组标题（左侧强调竖条 + 粗体），用于替代裸文字标题。</summary>
        public static Label SectionTitle(string text)
        {
            Label label = Label(text, Theme.FSection, Theme.TextPrimary);
            label.Padding = new Padding(10, 0, 0, 0);
            label.Paint += delegate(object sender, PaintEventArgs e)
            {
                Label self = (Label)sender;
                if (self.Width < 6) return;
                Theme.DrawAccentBar(e.Graphics, new Rectangle(0, Math.Max(1, (self.Height - 12) / 2), 3, 12));
            };
            return label;
        }

        public static Size ButtonSize(Button button)
        {
            Size text = TextRenderer.MeasureText(button.Text, button.Font);
            return new Size(text.Width + 20, text.Height + 12);
        }

        public static CheckBox Check(string text, bool isChecked)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.Checked = isChecked;
            box.Font = Theme.FSmall;
            box.ForeColor = Theme.TextSecondary;
            box.BackColor = Color.Transparent;
            box.FlatStyle = FlatStyle.Flat;
            box.AutoSize = true;
            box.Cursor = Cursors.Hand;
            return box;
        }

        public static TextBox Text(string value)
        {
            TextBox box = new TextBox();
            box.Text = value;
            box.Font = Theme.FNormal;
            box.BackColor = Theme.Surface2;
            box.ForeColor = Theme.TextPrimary;
            box.BorderStyle = BorderStyle.FixedSingle;
            return box;
        }

        /// <summary>时间输入框（HH:mm，微调按钮）。用 DateTimePicker 是为了拿到系统级的时间选择体验。</summary>
        public static DateTimePicker TimePicker(string value)
        {
            DateTimePicker picker = new DateTimePicker();
            picker.Format = DateTimePickerFormat.Custom;
            picker.CustomFormat = "HH:mm";
            picker.ShowUpDown = true;
            picker.Font = Theme.FMono;
            picker.CalendarForeColor = Theme.TextPrimary;
            picker.Width = 84;
            System.TimeSpan parsed;
            if (Core.TickerSchedule.TryParseTime(value, out parsed))
                picker.Value = DateTime.Today.Add(parsed);
            return picker;
        }

        public static TrackBar Slider(int min, int max, int value)
        {
            TrackBar bar = new TrackBar();
            bar.Minimum = min;
            bar.Maximum = max;
            bar.Value = Math.Min(max, Math.Max(min, value));
            bar.TickFrequency = Math.Max(1, (max - min) / 5);
            bar.SmallChange = 1;
            bar.LargeChange = Math.Max(1, (max - min) / 10);
            bar.AutoSize = false;
            bar.Height = 24;
            bar.BackColor = Theme.Surface1;
            return bar;
        }

        /// <summary>下拉框（统一配色，避免每处重复）。</summary>
        public static ComboBox Combo()
        {
            ComboBox combo = new ComboBox();
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.Font = Theme.FNormal;
            combo.BackColor = Theme.Surface2;
            combo.ForeColor = Theme.TextPrimary;
            return combo;
        }

        /// <summary>
        /// 安全地设置背景色。Button / ComboBox / TrackBar 等控件不支持透明背景，
        /// 直接赋 Color.Transparent 会抛 ArgumentException（"控件不支持透明的背景色"），
        /// 而且在 ApplyTheme 这种遍历所有子控件的循环里会中断整段配色逻辑。
        /// </summary>
        public static void SetBackColor(Control control, Color color)
        {
            if (control == null) return;
            try
            {
                control.BackColor = color;
            }
            catch (ArgumentException)
            {
                // 该控件不支持这个背景色（通常是不支持透明），保留原样
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>安全地设置前景色（个别控件在句柄未创建时也可能抛异常）。</summary>
        public static void SetForeColor(Control control, Color color)
        {
            if (control == null) return;
            try
            {
                control.ForeColor = color;
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// 把按钮裁成圆角。用 Region 而不是自绘：FlatStyle 按钮自己会画背景，
        /// 再叠一层自绘会把焦点/按下态盖掉；裁剪则保留原生行为只改外形。
        /// </summary>
        public static void Round(Button button, int radius)
        {
            if (button == null) return;
            EventHandler apply = delegate
            {
                if (button.Width <= 2 || button.Height <= 2) return;
                using (System.Drawing.Drawing2D.GraphicsPath path = Theme.RoundedRect(
                    new Rectangle(0, 0, button.Width, button.Height), radius))
                {
                    Region previous = button.Region;
                    button.Region = new Region(path);
                    if (previous != null) previous.Dispose();
                }
            };
            button.SizeChanged += apply;
            apply(button, EventArgs.Empty);
        }
    }
}
