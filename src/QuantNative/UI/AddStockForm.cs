using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.Data;
using QuantNative.Interop;

namespace QuantNative.UI
{
    /// <summary>
    /// 主窗口的「添加自选」对话框：输入代码 / 名称 / 拼音搜索，
    /// 用「市场」和「类型」两组勾选项过滤结果，回车或双击加入。加入后不关闭，方便连续添加。
    /// </summary>
    public sealed class AddStockForm : Form
    {
        private const int DialogWidth = 560;
        private const int DialogHeight = 430;

        private readonly AppHost _host;
        private TextBox _search;
        private ListBox _list;
        private Label _status;
        private Label _capMarket, _capType;
        private Button _add, _close;
        private Timer _debounce;

        private CheckBox _mSh, _mSz, _mBj, _mHk, _mUs, _mOf;
        private CheckBox _tStock, _tFund, _tIndex, _tBond;

        /// <summary>搜索返回的全部结果。</summary>
        private List<StockBrief> _results = new List<StockBrief>();
        /// <summary>通过筛选的结果，与列表框条目一一对应（下标必须同步）。</summary>
        private List<StockBrief> _visible = new List<StockBrief>();
        private int _searchSeq;

        public AddStockForm(AppHost host)
        {
            _host = host;

            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Text = "添加自选";
            BackColor = Theme.Surface1;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FNormal;
            ClientSize = new Size(DialogWidth, DialogHeight);
            Icon = AppIcon.Get();

            BuildUi();
            _host.ThemeChanged += ApplyTheme;
        }

        private void BuildUi()
        {
            _debounce = new Timer();
            _debounce.Interval = 300;
            _debounce.Tick += delegate { _debounce.Stop(); RunSearch(); };

            _search = Ui.Text("");
            _search.Location = new Point(12, 12);
            _search.Width = ClientSize.Width - 24;
            _search.KeyDown += OnSearchKeyDown;
            _search.TextChanged += delegate
            {
                _debounce.Stop();
                if (_search.Text.Trim().Length == 0)
                {
                    _results = new List<StockBrief>();
                    RefreshList();
                    SetStatus("");
                }
                else _debounce.Start();
            };
            Controls.Add(_search);

            // ── 筛选：市场 ──
            _capMarket = Ui.Label("市场", Theme.FSmall, Theme.TextTertiary);
            _capMarket.Location = new Point(12, 47);
            Controls.Add(_capMarket);

            _mSh = Ui.Check("沪市", true);
            _mSz = Ui.Check("深市", true);
            _mBj = Ui.Check("北交所", true);
            _mHk = Ui.Check("港股", true);
            _mUs = Ui.Check("美股", true);
            _mOf = Ui.Check("场外", true);
            CheckBox[] markets = new CheckBox[] { _mSh, _mSz, _mBj, _mHk, _mUs, _mOf };
            int mx = 12 + 34;
            for (int i = 0; i < markets.Length; i++)
            {
                markets[i].Location = new Point(mx, 44);
                markets[i].CheckedChanged += OnFilterChanged;
                Controls.Add(markets[i]);
                mx += markets[i].PreferredSize.Width + 12;
            }

            // ── 筛选：类型 ──
            _capType = Ui.Label("类型", Theme.FSmall, Theme.TextTertiary);
            _capType.Location = new Point(12, 71);
            Controls.Add(_capType);

            _tStock = Ui.Check("股票", true);
            _tFund = Ui.Check("基金", true);
            _tIndex = Ui.Check("指数", true);
            _tBond = Ui.Check("可转债", true);
            CheckBox[] types = new CheckBox[] { _tStock, _tFund, _tIndex, _tBond };
            int tx = 12 + 34;
            for (int i = 0; i < types.Length; i++)
            {
                types[i].Location = new Point(tx, 68);
                types[i].CheckedChanged += OnFilterChanged;
                Controls.Add(types[i]);
                tx += types[i].PreferredSize.Width + 12;
            }

            int listTop = 98;

            _list = new ListBox();
            _list.Location = new Point(12, listTop);
            _list.Size = new Size(ClientSize.Width - 24, ClientSize.Height - listTop - 62);
            _list.BackColor = Theme.Surface1;
            _list.ForeColor = Theme.TextPrimary;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = 24;
            _list.Font = Theme.FNormal;
            _list.DrawItem += OnDrawItem;
            _list.DoubleClick += delegate { AddSelected(); };
            _list.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) AddSelected(); };
            Controls.Add(_list);

            _status = Ui.Label("", Theme.FSmall, Theme.TextTertiary);
            _status.Location = new Point(12, _list.Bottom + 6);
            _status.AutoSize = false;
            _status.Width = ClientSize.Width - 24;
            _status.Height = 20;
            Controls.Add(_status);

            _add = Ui.Button("加入自选", delegate { AddSelected(); });
            _add.SetBounds(ClientSize.Width - 198, ClientSize.Height - 38, 90, 26);
            Controls.Add(_add);

            _close = Ui.Button("关闭", delegate { Close(); });
            _close.SetBounds(ClientSize.Width - 100, ClientSize.Height - 38, 88, 26);
            Controls.Add(_close);

            AcceptButton = _add;
            CancelButton = _close;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetCueBanner(_search, "代码 / 名称 / 拼音：600519、茅台、gzmt、沪深300");
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (_list.Items.Count > 0) AddSelected();
                else { _debounce.Stop(); RunSearch(); }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            RefreshList();
        }

        private async void RunSearch()
        {
            string keyword = _search.Text.Trim();
            if (keyword.Length == 0) return;

            int seq = ++_searchSeq;
            SetStatus("搜索中…");
            try
            {
                List<StockBrief> hits = await _host.Search.SearchAsync(keyword, _host.Hub.ActiveSource, _host.Hub.Sources);
                if (seq != _searchSeq) return;

                _results = hits ?? new List<StockBrief>();
                RefreshList();
            }
            catch (Exception ex)
            {
                if (seq != _searchSeq) return;
                _results = new List<StockBrief>();
                RefreshList();
                SetStatus("搜索失败：" + ex.Message);
                Log.Warn("添加自选搜索失败: " + ex.Message);
            }
        }

        /// <summary>按「市场 + 类型」两组勾选过滤，并重建列表（_visible 与条目同步）。</summary>
        private void RefreshList()
        {
            _visible = new List<StockBrief>();
            _list.BeginUpdate();
            _list.Items.Clear();
            for (int i = 0; i < _results.Count; i++)
            {
                StockBrief brief = _results[i];
                if (!PassesFilter(brief)) continue;
                _visible.Add(brief);
                string tag = Symbols.MarketTag(brief.Code, brief.Category);
                // 场外基金额外标出提供实时数据的来源与基金类型 —— 用户能一眼看出
                // 这条结果靠谁取数（也是「多结果能稳定展示」的可解释性来源）
                string extra = "";
                if (Symbols.IsOpenFund(brief.Code))
                {
                    extra = "  " + (string.IsNullOrEmpty(brief.SourceName) ? "天天基金" : brief.SourceName);
                    if (!string.IsNullOrEmpty(brief.TypeDesc)) extra += " · " + brief.TypeDesc;
                }
                _list.Items.Add((tag.Length > 0 ? "[" + tag + "] " : "") + brief.Name
                    + "  " + Symbols.FormatCode(brief.Code) + extra
                    + (_host.Contains(brief.Code) ? "   （已添加）" : ""));
            }
            _list.EndUpdate();

            if (_results.Count == 0)
            {
                SetStatus("");
                return;
            }

            SetStatus(_visible.Count == 0
                ? "共 " + _results.Count + " 条结果，当前筛选下没有匹配（可调整下方勾选）"
                : "匹配 " + _visible.Count + " / " + _results.Count + " 条，回车加入");
            if (_visible.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
        }

        private void AddSelected()
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= _visible.Count) return;

            StockBrief brief = _visible[index];
            if (_host.AddWatch(brief))
            {
                SetStatus("已加入 " + brief.Name);
                _list.Items[index] = _list.Items[index].ToString() + "   （已添加）";
            }
            else
            {
                SetStatus(brief.Name + " 已在自选中");
            }
        }

        private void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _list.Items.Count) return;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush back = new SolidBrush(selected ? Theme.AccentDim : Theme.Surface1))
                e.Graphics.FillRectangle(back, e.Bounds);
            using (SolidBrush text = new SolidBrush(selected ? Theme.Accent : Theme.TextPrimary))
            using (StringFormat format = Theme.LeftMiddle())
                e.Graphics.DrawString(_list.Items[e.Index].ToString(), Theme.FNormal, text, e.Bounds, format);
        }

        private void SetStatus(string text)
        {
            if (_status != null) _status.Text = text ?? "";
        }

        // ── 筛选规则 ──
        // 两组各自「一个都不勾」视为不筛选（否则勾空会一片空白，不符合直觉）。

        private bool PassesFilter(StockBrief brief)
        {
            if (brief == null) return false;

            string market = Symbols.MarketGroup(brief.Code, brief.Market);
            bool anyMarket = _mSh.Checked || _mSz.Checked || _mBj.Checked || _mHk.Checked || _mUs.Checked || _mOf.Checked;
            if (anyMarket)
            {
                bool hit =
                    (_mSh.Checked && market == "SHA") ||
                    (_mSz.Checked && market == "SZA") ||
                    (_mBj.Checked && market == "BJ") ||
                    (_mHk.Checked && market == "HK") ||
                    (_mUs.Checked && market == "US") ||
                    (_mOf.Checked && market == "OF");
                if (!hit) return false;
            }

            string type = Symbols.TypeGroup(brief.Code, brief.Category);
            bool anyType = _tStock.Checked || _tFund.Checked || _tIndex.Checked || _tBond.Checked;
            if (anyType)
            {
                bool hit =
                    (_tStock.Checked && type == "STOCK") ||
                    (_tFund.Checked && type == "FUND") ||
                    (_tIndex.Checked && type == "INDEX") ||
                    (_tBond.Checked && type == "BOND");
                if (!hit) return false;
            }

            return true;
        }

        private void ApplyTheme()
        {
            BackColor = Theme.Surface1;
            ForeColor = Theme.TextPrimary;
            if (_list != null) { _list.BackColor = Theme.Surface1; _list.ForeColor = Theme.TextPrimary; }
            foreach (Control control in Controls)
            {
                if (control is Button)
                {
                    Button button = (Button)control;
                    button.BackColor = Theme.Surface1;
                    button.ForeColor = Theme.TextSecondary;
                    button.FlatAppearance.BorderColor = Theme.Border1;
                }
                else if (control is Label)
                {
                    control.ForeColor = Theme.TextTertiary;
                }
                else if (control is CheckBox)
                {
                    control.ForeColor = Theme.TextSecondary;
                }
            }
            _list.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _host.ThemeChanged -= ApplyTheme;
                if (_debounce != null) _debounce.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
