using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using QuantNative.Core;

namespace QuantNative.UI
{
    /// <summary>
    /// 面板「设置」页。按功能分成五组：
    ///
    ///   浮窗外观   —— 不透明度 / 宽度 / 高度 / 字号 / 条数 / 轮播间隔 / 字体 / 置顶
    ///   显示时段   —— 每日定时显示与隐藏（支持跨零点）
    ///   数据刷新   —— 刷新档位（报价与概览的节奏）
    ///   基金关联   —— 一键自动关联（按重仓股行业 + 名称）
    ///   通用       —— 配色方案 / 开机自启 / 恢复默认
    ///
    /// 数值类设置即改即生效（浮窗立刻重绘/重排），不需要「应用」按钮。
    /// 布局按行游标自上而下排，整体不要超过一屏 —— 超了有 AutoScroll。
    /// </summary>
    public sealed class SettingsPage : UserControl
    {
        private const int LabelWidth = 88;
        private const int ValueWidth = 62;
        private const int RowHeight = 30;
        private const int TitleHeight = 26;

        /// <summary>字体下拉框里「跟随默认」那一项的文案。</summary>
        private const string DefaultFontItem = "（跟随主题默认）";

        private readonly AppHost _host;

        // ── 浮窗外观 ──
        private TrackBar _opacity, _width, _height, _fontSize, _rows, _interval;
        private Label _opacityValue, _widthValue, _heightValue, _fontSizeValue, _rowsValue, _intervalValue;

        // ── 下拉框 ──
        private ComboBox _uiFont, _numberFont, _profile, _scheme;
        private string[] _fontFamilies = new string[0];

        // ── 开关 ──
        private CheckBox _topMost, _autoStart, _schedule;

        // ── 时间 ──
        private DateTimePicker _from, _to;

        // ── 按钮 ──
        private Button _reset, _associate;

        // ── 标签 ──
        private Label _lOpacity, _lWidth, _lHeight, _lFont, _lRows, _lInterval;
        private Label _lUiFont, _lNumberFont, _lTop;
        private Label _lSchedule, _lFrom, _lTo;
        private Label _lProfile, _lAssoc, _lScheme, _lAuto, _lReset;
        private Label _sWindow, _sSchedule, _sData, _sAssoc, _sGeneral;
        private Label _scheduleSummary, _profileHint, _hint;

        /// <summary>程序化改控件值时抑制回调，避免「设置 → 事件 → 再设控件」的回环。</summary>
        private bool _syncing;

        public SettingsPage(AppHost host)
        {
            _host = host;
            BackColor = Theme.Surface1;
            AutoScroll = true;
            BuildUi();
            _host.TickerPrefsChanged += SyncFromHost;
            _host.ThemeChanged += ApplyTheme;
        }

        private void BuildUi()
        {
            // ── 分组标题 ──
            _sWindow = Ui.SectionTitle("浮窗外观");
            _sSchedule = Ui.SectionTitle("显示时段（定时隐藏）");
            _sData = Ui.SectionTitle("数据刷新");
            _sAssoc = Ui.SectionTitle("自选基金关联");
            _sGeneral = Ui.SectionTitle("通用");

            // ── 行标签 ──
            _lOpacity = Ui.Label("底图不透明度", Theme.FNormal, Theme.TextSecondary);
            _lWidth = Ui.Label("浮窗宽度", Theme.FNormal, Theme.TextSecondary);
            _lHeight = Ui.Label("浮窗高度", Theme.FNormal, Theme.TextSecondary);
            _lFont = Ui.Label("文本大小", Theme.FNormal, Theme.TextSecondary);
            _lRows = Ui.Label("常态条数", Theme.FNormal, Theme.TextSecondary);
            _lInterval = Ui.Label("轮播间隔", Theme.FNormal, Theme.TextSecondary);
            _lUiFont = Ui.Label("正文字体", Theme.FNormal, Theme.TextSecondary);
            _lNumberFont = Ui.Label("数字字体", Theme.FNormal, Theme.TextSecondary);
            _lTop = Ui.Label("浮窗始终置顶", Theme.FNormal, Theme.TextSecondary);
            _lSchedule = Ui.Label("启用定时隐藏", Theme.FNormal, Theme.TextSecondary);
            _lFrom = Ui.Label("显示自", Theme.FNormal, Theme.TextSecondary);
            _lTo = Ui.Label("至", Theme.FNormal, Theme.TextSecondary);
            _lProfile = Ui.Label("刷新档位", Theme.FNormal, Theme.TextSecondary);
            _lAssoc = Ui.Label("一键关联", Theme.FNormal, Theme.TextSecondary);
            _lScheme = Ui.Label("配色方案", Theme.FNormal, Theme.TextSecondary);
            _lAuto = Ui.Label("开机自启", Theme.FNormal, Theme.TextSecondary);
            _lReset = Ui.Label("恢复默认", Theme.FNormal, Theme.TextSecondary);

            // ── 滑块 ──
            _opacity = Ui.Slider(0, 100, _host.TickerOpacity);
            _width = Ui.Slider(AppHost.MinTickerWidth, AppHost.MaxTickerWidth, _host.TickerWidth);
            _width.TickFrequency = 116;
            _height = Ui.Slider(0, 600, _host.TickerHeight);
            _height.TickFrequency = 60;
            _fontSize = Ui.Slider(AppHost.MinTickerFontSize, AppHost.MaxTickerFontSize, _host.TickerFontSize);
            _rows = Ui.Slider(AppHost.MinTickerRows, AppHost.MaxTickerRows, _host.TickerRows);
            _interval = Ui.Slider(500, 10000, _host.TickerIntervalMs);
            _interval.TickFrequency = 500;

            _opacityValue = Ui.Label("", Theme.FMono, Theme.TextPrimary);
            _widthValue = Ui.Label("", Theme.FMono, Theme.TextPrimary);
            _heightValue = Ui.Label("", Theme.FMono, Theme.TextPrimary);
            _fontSizeValue = Ui.Label("", Theme.FMono, Theme.TextPrimary);
            _rowsValue = Ui.Label("", Theme.FMono, Theme.TextPrimary);
            _intervalValue = Ui.Label("", Theme.FMono, Theme.TextPrimary);

            _opacity.Scroll += delegate { _host.TickerOpacity = _opacity.Value; };
            _width.Scroll += delegate { _host.TickerWidth = _width.Value; };
            _height.Scroll += delegate
            {
                // 0 = 回到「按条数 × 字号自动算高」
                _host.TickerHeight = _height.Value;
                RefreshValueLabels();
            };
            _fontSize.Scroll += delegate { _host.TickerFontSize = _fontSize.Value; };
            _rows.Scroll += delegate { _host.TickerRows = _rows.Value; };
            _interval.Scroll += delegate { _host.TickerIntervalMs = _interval.Value; };

            // ── 开关 ──
            _topMost = Ui.Check("", _host.TickerAlwaysOnTop);
            _topMost.CheckedChanged += delegate
            {
                if (_syncing) return;
                _host.TickerAlwaysOnTop = _topMost.Checked;
                UpdateToggleText(_topMost, "已开启", "已关闭");
            };

            _autoStart = Ui.Check("", _host.AutoStart);
            _autoStart.CheckedChanged += delegate
            {
                if (_syncing) return;
                _host.SetAutoStart(_autoStart.Checked);
                UpdateToggleText(_autoStart, "已开启", "已关闭");
            };

            _schedule = Ui.Check("", _host.TickerScheduleEnabled);
            _schedule.CheckedChanged += delegate
            {
                if (_syncing) return;
                _host.TickerScheduleEnabled = _schedule.Checked;
                UpdateToggleText(_schedule, "已开启", "已关闭");
                UpdateScheduleEnabled();
                SetHint(_schedule.Checked
                    ? "已启用定时隐藏：" + _host.TickerScheduleSummary()
                    : "已关闭定时隐藏，浮窗按你的手动操作显示/隐藏");
            };

            // ── 时间选择 ──
            _from = Ui.TimePicker(_host.TickerVisibleFrom);
            _to = Ui.TimePicker(_host.TickerVisibleTo);
            _from.ValueChanged += delegate
            {
                if (_syncing) return;
                _host.TickerVisibleFrom = TickerSchedule.Format(_from.Value.TimeOfDay);
                UpdateScheduleSummary();
            };
            _to.ValueChanged += delegate
            {
                if (_syncing) return;
                _host.TickerVisibleTo = TickerSchedule.Format(_to.Value.TimeOfDay);
                UpdateScheduleSummary();
            };

            // ── 字体下拉 ──
            _fontFamilies = Theme.InstalledFamilies();
            _uiFont = BuildFontCombo();
            _uiFont.SelectedIndexChanged += delegate
            {
                if (_syncing) return;
                _host.TickerFontFamily = SelectedFamily(_uiFont);
                SetHint("浮窗正文与界面字体已切换为「" + SelectedFamilyName(_uiFont) + "」");
            };
            _numberFont = BuildFontCombo();
            _numberFont.SelectedIndexChanged += delegate
            {
                if (_syncing) return;
                _host.TickerNumberFontFamily = SelectedFamily(_numberFont);
                SetHint("浮窗数字字体已切换为「" + SelectedFamilyName(_numberFont) + "」（等宽字体能让小数点严格对齐）");
            };

            // ── 刷新档位 ──
            _profile = Ui.Combo();
            RefreshProfile[] profiles = MarketClock.Profiles();
            for (int i = 0; i < profiles.Length; i++) _profile.Items.Add(MarketClock.ProfileName(profiles[i]));
            _profile.SelectedIndexChanged += delegate
            {
                if (_syncing) return;
                int index = _profile.SelectedIndex;
                if (index < 0 || index >= profiles.Length) return;
                _host.RefreshProfile = profiles[index];
                UpdateProfileHint();
            };

            // ── 配色 ──
            _scheme = Ui.Combo();
            _scheme.Items.AddRange(Theme.SchemeNames());
            _scheme.SelectedIndexChanged += delegate
            {
                if (_syncing) return;
                int index = _scheme.SelectedIndex;
                if (index < 0 || index >= Theme.Schemes.Length) return;
                _host.ApplyTheme(Theme.Schemes[index].Key);
                SetHint("配色方案已切换为「" + Theme.Schemes[index].Name + "」（主界面与浮窗同时生效）");
            };

            // ── 按钮 ──
            _associate = Ui.PrimaryButton("重新自动关联全部基金", OnAssociate);

            _reset = Ui.Button("恢复默认设置", delegate
            {
                _host.ResetTickerPrefs();
                SyncFromHost();
                SetHint("已恢复默认：宽度 " + 340 + " / 高度自动 / 不透明度 92% / 字号 11 / 6 条，"
                    + "并关闭定时隐藏（时段回到 " + TickerSchedule.DefaultFrom + " – " + TickerSchedule.DefaultTo + "）");
            });

            // ── 说明文字 ──
            _scheduleSummary = Ui.Label("", Theme.FHint, Theme.TextTertiary);
            _scheduleSummary.AutoSize = false;
            _profileHint = Ui.Label("", Theme.FHint, Theme.TextTertiary);
            _profileHint.AutoSize = false;
            _hint = Ui.Label("", Theme.FHint, Theme.TextTertiary);
            _hint.AutoSize = false;

            // ── 加入控件树 ──
            Label[] labels = new Label[] {
                _sWindow, _sSchedule, _sData, _sAssoc, _sGeneral,
                _lOpacity, _lWidth, _lHeight, _lFont, _lRows, _lInterval,
                _lUiFont, _lNumberFont, _lTop, _lSchedule, _lFrom, _lTo,
                _lProfile, _lAssoc, _lScheme, _lAuto, _lReset,
                _opacityValue, _widthValue, _heightValue, _fontSizeValue, _rowsValue, _intervalValue,
                _scheduleSummary, _profileHint, _hint
            };
            for (int i = 0; i < labels.Length; i++) Controls.Add(labels[i]);

            Controls.Add(_opacity); Controls.Add(_width); Controls.Add(_height);
            Controls.Add(_fontSize); Controls.Add(_rows); Controls.Add(_interval);
            Controls.Add(_uiFont); Controls.Add(_numberFont);
            Controls.Add(_topMost); Controls.Add(_schedule); Controls.Add(_from); Controls.Add(_to);
            Controls.Add(_profile); Controls.Add(_scheme);
            Controls.Add(_associate); Controls.Add(_reset); Controls.Add(_autoStart);

            UpdateToggleText(_topMost, "已开启", "已关闭");
            UpdateToggleText(_autoStart, "已开启", "已关闭");
            UpdateToggleText(_schedule, "已开启", "已关闭");
            SyncFromHost();
            UpdateScheduleEnabled();
            UpdateProfileHint();
        }

        private ComboBox BuildFontCombo()
        {
            ComboBox combo = Ui.Combo();
            combo.Items.Add(DefaultFontItem);
            for (int i = 0; i < _fontFamilies.Length; i++) combo.Items.Add(_fontFamilies[i]);
            return combo;
        }

        /// <summary>下拉框 → 字体族名（第 0 项表示「跟随默认」= 空串）。</summary>
        private static string SelectedFamily(ComboBox combo)
        {
            int index = combo.SelectedIndex;
            if (index <= 0) return "";
            return Convert.ToString(combo.Items[index]);
        }

        private static string SelectedFamilyName(ComboBox combo)
        {
            string family = SelectedFamily(combo);
            return family.Length == 0 ? "主题默认" : family;
        }

        private static void UpdateToggleText(CheckBox box, string on, string off)
        {
            box.Text = box.Checked ? on : off;
            box.ForeColor = box.Checked ? Theme.Accent : Theme.TextTertiary;
        }

        private void SetHint(string text)
        {
            if (_hint != null) _hint.Text = text;
        }

        /// <summary>时段开关关闭时，两个时间选择框置灰（仍可改，只是不生效）。</summary>
        private void UpdateScheduleEnabled()
        {
            bool on = _schedule != null && _schedule.Checked;
            if (_from != null) _from.Enabled = on;
            if (_to != null) _to.Enabled = on;
            UpdateScheduleSummary();
        }

        private void UpdateScheduleSummary()
        {
            if (_scheduleSummary == null) return;
            string summary = "当前时段：" + _host.TickerScheduleSummary();
            if (_schedule != null && _schedule.Checked)
            {
                DateTime now = DateTime.Now;
                bool visible = _host.TickerShouldBeVisible(now);
                summary += visible ? "（此刻显示中）" : "（此刻已隐藏）";
            }
            _scheduleSummary.Text = summary;
        }

        private void UpdateProfileHint()
        {
            if (_profileHint == null) return;
            _profileHint.Text = MarketClock.ProfileHint(_host.RefreshProfile);
        }

        /// <summary>一键自动关联：按「重仓股行业 → 板块」+ 名称匹配重算所有场外基金。</summary>
        private async void OnAssociate(object sender, EventArgs e)
        {
            if (_associate == null) return;
            _associate.Enabled = false;
            string original = _associate.Text;
            _associate.Text = "正在匹配…";
            SetHint("正在读取基金重仓股行业并按板块匹配，请稍候…");
            try
            {
                int linked = await _host.AutoAssociateAllAsync(true);
                SetHint(linked > 0
                    ? "已重新关联 " + linked + " 只基金（手动关联过的不会被覆盖）"
                    : "没有需要重新关联的基金：可能都已手动关联，或接口暂时取不到重仓股数据");
            }
            catch (Exception ex)
            {
                SetHint("自动关联失败：" + ex.Message);
            }
            finally
            {
                _associate.Text = original;
                _associate.Enabled = true;
            }
        }

        // ── 布局 ──

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        private void LayoutChildren()
        {
            int width = ClientSize.Width;
            if (width <= 0) return;
            if (_lOpacity == null || _opacity == null || _scheme == null || _reset == null) return;

            float scale = Theme.Scale(this);
            int rowH = (int)Math.Round(RowHeight * scale);
            int titleH = (int)Math.Round(TitleHeight * scale);
            int sliderLeft = LabelWidth + 8;
            int sliderWidth = Math.Max(60, width - sliderLeft - ValueWidth - 30);
            int comboWidth = Math.Min(240, Math.Max(120, width - sliderLeft - 28));
            int y = 0;

            // 浮窗外观
            PlaceTitle(_sWindow, ref y, titleH);
            PlaceSliderRow(_lOpacity, _opacity, _opacityValue, y, sliderLeft, sliderWidth, rowH); y += rowH;
            PlaceSliderRow(_lWidth, _width, _widthValue, y, sliderLeft, sliderWidth, rowH); y += rowH;
            PlaceSliderRow(_lHeight, _height, _heightValue, y, sliderLeft, sliderWidth, rowH); y += rowH;
            PlaceSliderRow(_lFont, _fontSize, _fontSizeValue, y, sliderLeft, sliderWidth, rowH); y += rowH;
            PlaceSliderRow(_lRows, _rows, _rowsValue, y, sliderLeft, sliderWidth, rowH); y += rowH;
            PlaceSliderRow(_lInterval, _interval, _intervalValue, y, sliderLeft, sliderWidth, rowH); y += rowH;
            PlaceComboRow(_lUiFont, _uiFont, y, sliderLeft, comboWidth, rowH); y += rowH;
            PlaceComboRow(_lNumberFont, _numberFont, y, sliderLeft, comboWidth, rowH); y += rowH;
            PlaceToggleRow(_lTop, _topMost, y, rowH); y += rowH;

            // 显示时段
            y += (int)Math.Round(6 * scale);
            PlaceTitle(_sSchedule, ref y, titleH);
            PlaceToggleRow(_lSchedule, _schedule, y, rowH); y += rowH;
            PlaceTimeRow(y, sliderLeft, rowH, scale); y += rowH;
            PlaceSubLabel(_scheduleSummary, y, width, rowH); y += rowH;

            // 数据刷新
            y += (int)Math.Round(6 * scale);
            PlaceTitle(_sData, ref y, titleH);
            PlaceComboRow(_lProfile, _profile, y, sliderLeft, comboWidth, rowH); y += rowH;
            PlaceSubLabel(_profileHint, y, width, rowH); y += rowH;

            // 自选基金关联
            y += (int)Math.Round(6 * scale);
            PlaceTitle(_sAssoc, ref y, titleH);
            _lAssoc.Location = new Point(0, y + (rowH - _lAssoc.Height) / 2);
            Size associateSize = Ui.ButtonSize(_associate);
            _associate.SetBounds(sliderLeft, y + 2,
                Math.Min(width - sliderLeft - 12, Math.Max(associateSize.Width + 30, 180)),
                Math.Max(24, rowH - 4));
            y += rowH;

            // 通用
            y += (int)Math.Round(6 * scale);
            PlaceTitle(_sGeneral, ref y, titleH);
            PlaceComboRow(_lScheme, _scheme, y, sliderLeft, comboWidth, rowH); y += rowH;
            PlaceToggleRow(_lAuto, _autoStart, y, rowH); y += rowH;
            _lReset.Location = new Point(0, y + 2);
            Size resetSize = Ui.ButtonSize(_reset);
            _reset.SetBounds(sliderLeft, y + 2, Math.Max(120, resetSize.Width), Math.Max(24, rowH - 4));
            y += rowH + (int)Math.Round(4 * scale);

            // 底部提示条
            _hint.SetBounds(0, y, Math.Max(80, width - 16), Math.Max(16, (int)Math.Round(32 * scale)));

            // 只在真的变了才写：AutoScrollMinSize 的变化会再触发一次布局，
            // 无条件赋值会变成「布局 → 改滚动范围 → 再布局」的死循环。
            Size wanted = new Size(0, y + _hint.Height + (int)Math.Round(8 * scale));
            if (AutoScrollMinSize != wanted) AutoScrollMinSize = wanted;
        }

        private void PlaceTitle(Label title, ref int y, int titleH)
        {
            // SectionTitle 自带左侧强调竖条，垂直居中对齐竖条
            title.Location = new Point(0, y + Math.Max(0, (titleH - title.Height) / 2));
            y += titleH;
        }

        private void PlaceSliderRow(Label label, TrackBar slider, Label value, int y, int left, int width, int rowH)
        {
            label.Location = new Point(0, y + (rowH - label.Height) / 2);
            slider.SetBounds(left, y, width, rowH);
            value.AutoSize = false;
            value.SetBounds(left + width + 6, y + (rowH - 18) / 2, ValueWidth - 6, 18);
            value.TextAlign = ContentAlignment.MiddleRight;
        }

        private void PlaceComboRow(Label label, Control combo, int y, int left, int width, int rowH)
        {
            label.Location = new Point(0, y + (rowH - label.Height) / 2);
            combo.SetBounds(left, y + 2, width, Math.Max(22, rowH - 4));
        }

        private void PlaceToggleRow(Label label, CheckBox box, int y, int rowH)
        {
            label.Location = new Point(0, y + (rowH - label.Height) / 2);
            box.Location = new Point(LabelWidth + 8, y + (rowH - box.Height) / 2);
        }

        private void PlaceTimeRow(int y, int left, int rowH, float scale)
        {
            int pickWidth = Math.Max(70, (int)Math.Round(78 * scale));
            int pickHeight = Math.Max(22, rowH - 4);
            _lFrom.Location = new Point(0, y + (rowH - _lFrom.Height) / 2);
            _from.SetBounds(left, y + 2, pickWidth, pickHeight);
            _lTo.Location = new Point(left + pickWidth + 4, y + (rowH - _lTo.Height) / 2);
            _to.SetBounds(left + pickWidth + 4 + _lTo.Width + 4, y + 2, pickWidth, pickHeight);
        }

        private void PlaceSubLabel(Label label, int y, int width, int rowH)
        {
            if (label == null) return;
            label.SetBounds(LabelWidth + 8, y, Math.Max(80, width - LabelWidth - 20), rowH);
        }

        // ── 数据同步 ──

        /// <summary>把 host 里的真实值刷到控件上（外部改动、恢复默认后调用）。</summary>
        public void SyncFromHost()
        {
            _syncing = true;
            try
            {
                SetSlider(_opacity, _host.TickerOpacity);
                SetSlider(_width, _host.TickerWidth);
                SetSlider(_height, _host.TickerHeight);
                SetSlider(_fontSize, _host.TickerFontSize);
                SetSlider(_rows, _host.TickerRows);
                SetSlider(_interval, _host.TickerIntervalMs);

                _topMost.Checked = _host.TickerAlwaysOnTop;
                _autoStart.Checked = _host.AutoStart;
                _schedule.Checked = _host.TickerScheduleEnabled;
                UpdateToggleText(_topMost, "已开启", "已关闭");
                UpdateToggleText(_autoStart, "已开启", "已关闭");
                UpdateToggleText(_schedule, "已开启", "已关闭");

                TimeSpan parsed;
                if (TickerSchedule.TryParseTime(_host.TickerVisibleFrom, out parsed))
                    _from.Value = DateTime.Today.Add(parsed);
                if (TickerSchedule.TryParseTime(_host.TickerVisibleTo, out parsed))
                    _to.Value = DateTime.Today.Add(parsed);

                SelectFamily(_uiFont, _host.TickerFontFamily);
                SelectFamily(_numberFont, _host.TickerNumberFontFamily);

                RefreshProfile[] profiles = MarketClock.Profiles();
                int profileIndex = 0;
                for (int i = 0; i < profiles.Length; i++)
                    if (profiles[i] == _host.RefreshProfile) { profileIndex = i; break; }
                if (_profile.SelectedIndex != profileIndex) _profile.SelectedIndex = profileIndex;

                int schemeIndex = Theme.SchemeIndex(_host.Scheme);
                if (_scheme.SelectedIndex != schemeIndex) _scheme.SelectedIndex = schemeIndex;

                UpdateScheduleEnabled();
                UpdateProfileHint();
            }
            finally
            {
                _syncing = false;
                RefreshValueLabels();
            }
        }

        private static void SelectFamily(ComboBox combo, string family)
        {
            string name = (family ?? "").Trim();
            int index = 0;
            if (name.Length > 0)
            {
                for (int i = 1; i < combo.Items.Count; i++)
                {
                    if (string.Equals(Convert.ToString(combo.Items[i]), name, StringComparison.OrdinalIgnoreCase))
                    { index = i; break; }
                }
            }
            if (combo.SelectedIndex != index) combo.SelectedIndex = index;
        }

        private static void SetSlider(TrackBar bar, int value)
        {
            if (value < bar.Minimum) value = bar.Minimum;
            if (value > bar.Maximum) value = bar.Maximum;
            if (bar.Value != value) bar.Value = value;
        }

        /// <summary>slider 的 Value 变化不触发 Scroll 事件，所以这里补一次标签刷新。</summary>
        private void RefreshValueLabels()
        {
            if (_opacityValue == null) return;
            _opacityValue.Text = _host.TickerOpacity + "%";
            _widthValue.Text = _host.TickerWidth + " px";
            int height = _host.TickerHeight;
            _heightValue.Text = height <= 0 ? "自动" : height + " px";
            _fontSizeValue.Text = _host.TickerFontSize + " px";
            _rowsValue.Text = _host.TickerRows + " 条";
            _intervalValue.Text = (_host.TickerIntervalMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " 秒";
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Surface1;
            foreach (Control control in Controls)
            {
                // ComboBox / Button / TrackBar 不支持透明背景，用安全封装避免抛异常中断整段配色
                if (control is TrackBar) Ui.SetBackColor(control, Theme.Surface1);
                else if (control is Label || control is CheckBox) Ui.SetBackColor(control, Color.Transparent);

                if (control is Label)
                {
                    Label label = (Label)control;
                    Ui.SetForeColor(label, label == _hint || label == _scheduleSummary || label == _profileHint
                        ? Theme.TextTertiary : Theme.TextSecondary);
                }
                else if (control is CheckBox) Ui.SetForeColor(control, Theme.TextSecondary);
            }
            ComboBox[] combos = new ComboBox[] { _scheme, _uiFont, _numberFont, _profile };
            for (int i = 0; i < combos.Length; i++)
            {
                if (combos[i] == null) continue;
                Ui.SetBackColor(combos[i], Theme.Surface2);
                Ui.SetForeColor(combos[i], Theme.TextPrimary);
            }
            DateTimePicker[] pickers = new DateTimePicker[] { _from, _to };
            for (int i = 0; i < pickers.Length; i++)
            {
                if (pickers[i] == null) continue;
                Ui.SetBackColor(pickers[i], Theme.Surface2);
                Ui.SetForeColor(pickers[i], Theme.TextPrimary);
            }
            if (_hint != null) _hint.ForeColor = Theme.TextTertiary;
            if (_scheduleSummary != null) _scheduleSummary.ForeColor = Theme.TextTertiary;
            if (_profileHint != null) _profileHint.ForeColor = Theme.TextTertiary;

            UpdateToggleText(_topMost, "已开启", "已关闭");
            UpdateToggleText(_autoStart, "已开启", "已关闭");
            UpdateToggleText(_schedule, "已开启", "已关闭");
            RefreshValueLabels();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _host.TickerPrefsChanged -= SyncFromHost;
                _host.ThemeChanged -= ApplyTheme;
            }
            base.Dispose(disposing);
        }
    }
}
