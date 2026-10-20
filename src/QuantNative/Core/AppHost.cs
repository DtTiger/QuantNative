using System;
using System.Collections.Generic;
using System.Windows.Forms;
using QuantNative.Data;
using QuantNative.Interop;
using QuantNative.UI;

namespace QuantNative.Core
{
    /// <summary>
    /// 应用级单例：把「设置 / 自选 / 数据源 / 轮询中枢 / 各窗口」串起来。
    /// 相当于 Tauri 版里 Pinia stores + Rust 端 AppState 的合体，但只有一个进程内存对象，
    /// 窗口之间不再需要 IPC 与事件同步。
    /// </summary>
    public sealed class AppHost
    {
        public static AppHost Instance { get; private set; }

        public Store Store { get; private set; }
        public QuoteHub Hub { get; private set; }
        public SearchService Search { get; private set; }

        /// <summary>自选列表（内存态即真源，任何变更立即落盘）。</summary>
        public List<WatchItem> Watchlist { get; private set; }

        public MainForm MainWindow { get; set; }
        public TickerBarForm Ticker { get; set; }
        public TrayIconHost Tray { get; set; }

        public ApplicationContext Context { get; set; }

        public event Action WatchlistChanged;
        public event Action TickerPrefsChanged;
        public event Action ThemeChanged;
        public event Action SourceChanged;

        public AppHost()
        {
            Instance = this;
        }

        // ── 初始化 ──

        public void Init()
        {
            Store = new Store();
            Log.Init(Store.DataDir);
            Log.Info("QuantNative 启动，数据目录 " + Store.DataDir + "（便携模式 " + Store.IsPortable + "）");

            ApplyTickerFonts();
            MigrateUiStyleOnce();
            Theme.UseScheme(GetSetting("theme", Theme.DefaultScheme));

            Search = new SearchService();
            Hub = new QuoteHub();
            Hub.SetRefreshProfile(RefreshProfile);
            Hub.Register(new TencentSource());
            Hub.Register(new SinaSource());
            // 场外基金（新浪/腾讯都覆盖不到）——注册在最后，这样搜索合并时
            // 场内结果先入表，天天基金返回的同号场内 ETF 会被识别为重复丢掉。
            Hub.Register(new EastmoneyFundSource());
            Hub.SetActiveSource(GetSetting("active_datasource", "tencent"));

            Watchlist = Store.LoadWatchlist();
            Hub.SetWatchlist(Watchlist);
            SyncBoardLinks();
        }

        /// <summary>
        /// 界面风格迁移：2.4.0 把默认配色换成了「养基宝」。
        /// 只在用户用的还是旧默认色（dark / 未设置）时切过去 ——
        /// 用户自己挑过的配色（深空蓝、墨绿…）保持不动。
        /// </summary>
        private void MigrateUiStyleOnce()
        {
            if (Store.Get("ui_style_rev", "") == "4") return;
            string theme = Store.Get("theme", "");
            Dictionary<string, string> values = new Dictionary<string, string>();
            if (theme.Length == 0 || string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
                values["theme"] = Theme.DefaultScheme;
            // 浮窗高度改为「可拖动」后需要一个固定高度字段，默认 0 = 自动
            if (Store.Get("ticker_height", null) == null) values["ticker_height"] = "0";
            values["ui_style_rev"] = "4";
            Store.SetMany(values);
        }

        public void Start()
        {
            _autoStarted = true;
            Hub.Start();
            AutoAssociateMissingAsync();
        }

        private readonly HashSet<string> _autoPending = new HashSet<string>(StringComparer.Ordinal);
        private bool _autoStarted;

        /// <summary>启动时把还没关联的场外基金补齐（只补缺，不动手动关联过的）。</summary>
        private async void AutoAssociateMissingAsync()
        {
            foreach (WatchItem item in new List<WatchItem>(Watchlist))
                if (Symbols.IsOpenFund(item.Code)) await AutoAssociateFundAsync(item, false);
        }

        /// <summary>
        /// 「一键自动关联」：重新匹配所有场外基金。
        /// refreshAuto=false 只补空缺；true 连「自动关联过」的结果也重算（手动关联的永不动）。
        /// 返回本次成功关联的基金只数。
        /// </summary>
        public async System.Threading.Tasks.Task<int> AutoAssociateAllAsync(bool refreshAuto)
        {
            int linked = 0;
            foreach (WatchItem item in new List<WatchItem>(Watchlist))
            {
                if (!Symbols.IsOpenFund(item.Code)) continue;
                if (await AutoAssociateFundAsync(item, refreshAuto)) linked++;
            }
            return linked;
        }

        /// <summary>
        /// 给「关联板块」对话框用的推荐（与自动关联同一套信号）：
        /// 先读前十大重仓股 → 所属行业 → 匹配板块，再退回基金名称匹配。
        /// </summary>
        public async System.Threading.Tasks.Task<List<BoardLink>> SuggestBoardsAsync(string fundCode, string fundName)
        {
            List<string> industries = await FetchFundIndustriesAsync(fundCode);
            return await Hub.Boards.SuggestForFundAsync(fundName, industries);
        }

        /// <summary>该基金是「自动关联」还是「手动关联」（未关联返回空串）。</summary>
        public string FundBoardMode(string code)
        {
            return Store.Get("fund_boards_mode." + (code ?? "").ToLowerInvariant(), "");
        }

        /// <summary>关联来源的中文说明，供表格与浮窗标注。</summary>
        public string FundBoardSourceText(string code)
        {
            switch (FundBoardMode(code))
            {
                case "manual": return "手动关联";
                case "auto": return "自动匹配";
                default: return "";
            }
        }

        /// <summary>
        /// 重仓股 → 所属行业。用于自动关联的第三路信号；
        /// 任何一步取不到数据都返回空列表，调用方自然退回名称匹配。
        /// </summary>
        private async System.Threading.Tasks.Task<List<string>> FetchFundIndustriesAsync(string code)
        {
            List<string> industries = new List<string>();
            IQuoteSource source = null;
            for (int i = 0; i < Hub.Sources.Count; i++)
                if (Hub.Sources[i].Name == EastmoneyFundSource.SourceName) source = Hub.Sources[i];
            EastmoneyFundSource fund = source as EastmoneyFundSource;
            if (fund == null) return industries;

            List<FundHolding> holdings = await fund.FetchTopHoldingsAsync(code);
            if (holdings.Count == 0) return industries;

            List<string> stockCodes = new List<string>();
            for (int i = 0; i < holdings.Count && i < 10; i++)
                if (!string.IsNullOrEmpty(holdings[i].Code)) stockCodes.Add(holdings[i].Code);

            industries = await Hub.Boards.FetchStockIndustriesAsync(stockCodes);
            return industries;
        }

        private async System.Threading.Tasks.Task<bool> AutoAssociateFundAsync(WatchItem item, bool refreshAuto)
        {
            string code = item.Code.ToLowerInvariant();
            string mode = Store.Get("fund_boards_mode." + code, "");
            if (mode == "manual") return false;                                     // 手动关联永不覆盖
            bool alreadyAuto = mode == "auto";
            if (alreadyAuto && !refreshAuto) return false;
            if (!alreadyAuto && Store.Get("fund_boards." + code, null) != null) return false;
            if (!alreadyAuto && GetFundBoards(code).Count > 0) return false;
            if (IsPlaceholderName(item.Name, item.Code)) return false;               // 名称还没回来，等一下再匹配
            if (!_autoPending.Add(code)) return false;

            try
            {
                List<string> industries = await FetchFundIndustriesAsync(item.Code);
                List<BoardLink> suggestions = await Hub.Boards.SuggestForFundAsync(item.Name, industries);

                // 等待期间用户可能手动改过、或把这只基金删了 —— 逐项复核后才落盘
                mode = Store.Get("fund_boards_mode." + code, "");
                if (mode == "manual" || !Watchlist.Contains(item)) return false;
                if (suggestions.Count == 0) return false;
                if (mode == "auto" && SameBoards(GetFundBoards(code), suggestions)) return false;

                Store.SetMany(new Dictionary<string, string> {
                    { "fund_boards." + code, BoardLink.Encode(suggestions) },
                    { "fund_boards_mode." + code, "auto" }
                });
                Log.Info("基金自动关联 " + code + " → " + DescribeBoards(suggestions)
                    + (industries.Count > 0 ? "（重仓行业 " + string.Join("/", industries.ToArray()) + "）" : "（按名称匹配）"));
                SyncBoardLinks();
                Action handler = WatchlistChanged;
                if (handler != null) handler();
                return true;
            }
            catch (Exception ex) { Log.Warn("基金自动关联失败 " + code + ": " + ex.Message); return false; }
            finally { _autoPending.Remove(code); }
        }

        private static bool SameBoards(IList<BoardLink> left, IList<BoardLink> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!string.Equals(left[i].Code, right[i].Code, StringComparison.Ordinal)) return false;
            return true;
        }

        private static string DescribeBoards(IList<BoardLink> links)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < links.Count; i++) names.Add(links[i].Name);
            return string.Join("、", names.ToArray());
        }

        // ── 设置读写 ──

        public string GetSetting(string key, string fallback)
        {
            return Store.Get(key, fallback);
        }

        public void SetSetting(string key, string value)
        {
            Store.Set(key, value);
        }

        /// <summary>切换配色方案（key 见 Theme.Schemes）。会持久化并通知所有窗口重绘。</summary>
        public void ApplyTheme(string theme)
        {
            Theme.UseScheme(theme);
            SetSetting("theme", Theme.SchemeKey);
            Action handler = ThemeChanged;
            if (handler != null) handler();
        }

        /// <summary>当前配色方案 key。</summary>
        public string Scheme { get { return Theme.SchemeKey; } }

        /// <summary>在暗色 / 浅色之间快速切换（保留原有快捷键行为）。</summary>
        public void ToggleTheme()
        {
            ApplyTheme(Theme.Dark ? "light" : "dark");
        }

        /// <summary>按方案列表顺序切到下一个配色（主界面「配色」按钮用）。</summary>
        public void CycleScheme()
        {
            int index = Theme.SchemeIndex(Theme.SchemeKey);
            index = (index + 1) % Theme.Schemes.Length;
            ApplyTheme(Theme.Schemes[index].Key);
        }

        // ── 浮窗偏好（读写设置，并在变化后通知浮窗重绘/重排） ──

        public int TickerOpacity
        {
            get { return Store.GetInt("ticker_opacity", 92, 0, 100); }
            set { Store.SetInt("ticker_opacity", Clamp(value, 0, 100)); RaiseTickerPrefs(); }
        }

        /// <summary>浮窗宽度上下限。拖动右边框只能落在这个区间内。</summary>
        public const int MinTickerWidth = 240;
        public const int MaxTickerWidth = 1400;
        public const int MinTickerHeight = 48;
        public const int MaxTickerHeight = 1200;
        public const int MinTickerRows = 1;
        public const int MaxTickerRows = 20;
        public const int MinTickerFontSize = 9;
        public const int MaxTickerFontSize = 32;

        public int TickerWidth
        {
            get { return Store.GetInt("ticker_width", 340, MinTickerWidth, MaxTickerWidth); }
            set { Store.SetInt("ticker_width", Clamp(value, MinTickerWidth, MaxTickerWidth)); RaiseTickerPrefs(); }
        }

        public int TickerFontSize
        {
            get { return Store.GetInt("ticker_font_size", 11, MinTickerFontSize, MaxTickerFontSize); }
            set
            {
                Store.SetInt("ticker_font_size", Clamp(value, MinTickerFontSize, MaxTickerFontSize));
                // 改字号后高度按字号重算（用户之前拖出来的固定高度不再合理）
                Store.SetInt("ticker_height", 0);
                RaiseTickerPrefs();
            }
        }

        public int TickerRows
        {
            get { return Store.GetInt("ticker_rows", 6, MinTickerRows, MaxTickerRows); }
            set
            {
                Store.SetInt("ticker_rows", Clamp(value, MinTickerRows, MaxTickerRows));
                Store.SetInt("ticker_height", 0);
                RaiseTickerPrefs();
            }
        }

        /// <summary>
        /// 浮窗高度。0 = 按「条数 × 字号」自动算；拖动上下边框后写入固定值，
        /// 此时高度完全由用户决定（条数只决定最多显示多少行）。
        /// </summary>
        public int TickerHeight
        {
            get { return Store.GetInt("ticker_height", 0, 0, MaxTickerHeight); }
            set
            {
                int fixedHeight = value <= 0 ? 0 : Clamp(value, MinTickerHeight, MaxTickerHeight);
                Store.SetInt("ticker_height", fixedHeight);
                RaiseTickerPrefs();
            }
        }

        /// <summary>浮窗正文使用的本机字体族（空 = 用主题默认）。</summary>
        public string TickerFontFamily
        {
            get { return Store.Get("ticker_font_family", ""); }
            set
            {
                Store.Set("ticker_font_family", (value ?? "").Trim());
                ApplyTickerFonts();
                RaiseTickerPrefs();
            }
        }

        /// <summary>浮窗数字使用的本机字体族（空 = 用主题默认等宽字体）。</summary>
        public string TickerNumberFontFamily
        {
            get { return Store.Get("ticker_number_font_family", ""); }
            set
            {
                Store.Set("ticker_number_font_family", (value ?? "").Trim());
                ApplyTickerFonts();
                RaiseTickerPrefs();
            }
        }

        /// <summary>把浮窗字体设置同步到 Theme 的全局族名上（界面与浮窗都跟随）。</summary>
        public void ApplyTickerFonts()
        {
            Theme.UiFamily = BlankToNull(TickerFontFamily) ?? Theme.DefaultUiFamily;
            Theme.MonoFamily = BlankToNull(TickerNumberFontFamily) ?? Theme.DefaultMonoFamily;
        }

        private static string BlankToNull(string text)
        {
            string value = (text ?? "").Trim();
            return value.Length == 0 ? null : value;
        }

        // ── 浮窗定时隐藏（每日显示时段） ──

        public bool TickerScheduleEnabled
        {
            get { return Store.GetBool("ticker_schedule", false); }
            set
            {
                Store.SetBool("ticker_schedule", value);
                if (Ticker != null) Ticker.RefreshSchedule(true);
                RaiseTickerPrefs();
            }
        }

        /// <summary>每日开始显示的时刻，HH:mm。</summary>
        public string TickerVisibleFrom
        {
            get { return TickerSchedule.Normalize(Store.Get("ticker_from", TickerSchedule.DefaultFrom), TickerSchedule.DefaultFrom); }
            set
            {
                Store.Set("ticker_from", TickerSchedule.Normalize(value, TickerSchedule.DefaultFrom));
                if (Ticker != null) Ticker.RefreshSchedule(true);
                RaiseTickerPrefs();
            }
        }

        /// <summary>每日停止显示的时刻（小于 from 表示跨零点），HH:mm。</summary>
        public string TickerVisibleTo
        {
            get { return TickerSchedule.Normalize(Store.Get("ticker_to", TickerSchedule.DefaultTo), TickerSchedule.DefaultTo); }
            set
            {
                Store.Set("ticker_to", TickerSchedule.Normalize(value, TickerSchedule.DefaultTo));
                if (Ticker != null) Ticker.RefreshSchedule(true);
                RaiseTickerPrefs();
            }
        }

        /// <summary>当前时刻浮窗是否应当可见（未启用时序时恒为 true）。</summary>
        public bool TickerShouldBeVisible(DateTime now)
        {
            if (!TickerScheduleEnabled) return true;
            return TickerSchedule.IsWithin(TickerVisibleFrom, TickerVisibleTo, now);
        }

        public string TickerScheduleSummary()
        {
            return TickerSchedule.Describe(TickerVisibleFrom, TickerVisibleTo);
        }

        // ── 刷新档位 ──

        public RefreshProfile RefreshProfile
        {
            get { return MarketClock.ParseProfile(Store.Get("refresh_profile", "fast")); }
            set
            {
                Store.Set("refresh_profile", MarketClock.ProfileKey(value));
                if (Hub != null) Hub.SetRefreshProfile(value);
                RaiseTickerPrefs();
            }
        }

        public int TickerIntervalMs
        {
            get { return Store.GetInt("ticker_interval_ms", 2000, 500, 10000); }
            set { Store.SetInt("ticker_interval_ms", Clamp(value, 500, 10000)); RaiseTickerPrefs(); }
        }

        public bool TickerAlwaysOnTop
        {
            get { return Store.GetBool("ticker_always_on_top", true); }
            set
            {
                Store.SetBool("ticker_always_on_top", value);
                if (Ticker != null) Ticker.ApplyAlwaysOnTop();
                RaiseTickerPrefs();
            }
        }

        /// <summary>
        /// 浮窗的逻辑像素高度：用户拖过上下边框就用固定高度，否则按「条数 × 字号」折算。
        /// </summary>
        public int TickerBarHeight()
        {
            int fixedHeight = Store.GetInt("ticker_height", 0, 0, MaxTickerHeight);
            if (fixedHeight > 0) return fixedHeight;
            return TickerAutoHeight(TickerRows, TickerFontSize);
        }

        /// <summary>自动高度公式（设置页与浮窗共用，保证两边算法一致）。</summary>
        public static int TickerAutoHeight(int rows, int fontSize)
        {
            int rowHeight = (int)Math.Round(fontSize * 1.45);
            return rows * rowHeight + 10;   // 上下各 4px 内边距 + 2px 余量
        }

        /// <summary>行高（逻辑像素），浮窗与设置页共用。</summary>
        public static int TickerRowHeight(int fontSize)
        {
            return (int)Math.Round(fontSize * 1.45);
        }

        /// <summary>按高度反推能塞下多少行（拖动上下边框后用）。</summary>
        public static int RowsForHeight(int height, int fontSize, int maxRows)
        {
            int rowHeight = Math.Max(1, TickerRowHeight(fontSize));
            int rows = (int)Math.Round((height - 10) / (double)rowHeight);
            if (rows < MinTickerRows) rows = MinTickerRows;
            if (rows > maxRows) rows = maxRows;
            return rows;
        }

        public void ResetTickerPrefs()
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            values["ticker_opacity"] = "92";
            values["ticker_width"] = "340";
            values["ticker_height"] = "0";
            values["ticker_font_size"] = "11";
            values["ticker_font_family"] = "";
            values["ticker_number_font_family"] = "";
            values["ticker_rows"] = "6";
            values["ticker_interval_ms"] = "2000";
            values["ticker_always_on_top"] = "1";
            values["ticker_schedule"] = "0";
            values["ticker_from"] = TickerSchedule.DefaultFrom;
            values["ticker_to"] = TickerSchedule.DefaultTo;
            Store.SetMany(values);
            ApplyTickerFonts();
            if (Ticker != null)
            {
                Ticker.ApplyAlwaysOnTop();
                Ticker.RefreshSchedule(true);
            }
            RaiseTickerPrefs();
        }

        /// <summary>把浮窗尺寸设置成「用户拖出来的」尺寸（拖动结束时调用，一次落盘）。</summary>
        public void SetTickerUserSize(int width, int height)
        {
            int clampedWidth = Clamp(width, MinTickerWidth, MaxTickerWidth);
            int clampedHeight = Clamp(height, MinTickerHeight, MaxTickerHeight);
            int rows = RowsForHeight(clampedHeight, TickerFontSize, MaxTickerRows);
            Dictionary<string, string> values = new Dictionary<string, string>();
            values["ticker_width"] = clampedWidth.ToString();
            values["ticker_height"] = clampedHeight.ToString();
            values["ticker_rows"] = rows.ToString();
            Store.SetMany(values);
            RaiseTickerPrefs();
        }

        private void RaiseTickerPrefs()
        {
            Action handler = TickerPrefsChanged;
            if (handler != null) handler();
        }

        // ── 自选 CRUD ──

        public bool Contains(string code)
        {
            for (int i = 0; i < Watchlist.Count; i++)
                if (Watchlist[i].Code == code) return true;
            return false;
        }

        public bool AddWatch(StockBrief stock)
        {
            if (stock == null || string.IsNullOrEmpty(stock.Code)) return false;
            if (Contains(stock.Code)) return false;

            WatchItem item = new WatchItem();
            item.Code = stock.Code;
            item.Market = string.IsNullOrEmpty(stock.Market) ? "CN" : stock.Market;
            item.Name = stock.Name;
            item.TickerEnabled = true;
            // 记住这个标的是哪家搜出来的：场外基金只有天天基金能提供实时数据，
            // 场内标的为空则跟随当前数据源。
            item.Source = stock.Source ?? "";
            Watchlist.Add(item);
            PersistWatchlist();
            if (_autoStarted && Symbols.IsOpenFund(item.Code)) { var pending = AutoAssociateFundAsync(item, false); }
            return true;
        }

        /// <summary>按代码添加（用于「输入 6 位代码直接加自选」）。</summary>
        public bool AddWatchByCode(string code)
        {
            string raw = (code ?? "").Trim();
            // of 前缀 = 场外基金，必须交给天天基金；其余按交易所规则补前缀
            bool isFund = Symbols.IsOpenFund(raw);
            string full = isFund ? raw : Symbols.ToExchangeCode(raw, "CN");
            if (Contains(full)) return false;

            WatchItem item = new WatchItem();
            item.Code = full;
            item.Market = isFund ? "OF" : "CN";
            item.Name = Symbols.FormatCode(full);
            item.Source = isFund ? EastmoneyFundSource.SourceName : "";
            Watchlist.Add(item);
            PersistWatchlist();

            // 名称先占位，等下一轮行情回来再补真名（行情里有 name 字段）
            Hub.Wakeup();
            return true;
        }

        public void RemoveWatch(WatchItem item)
        {
            if (item == null) return;
            Watchlist.Remove(item);
            PersistWatchlist();
        }

        public void SetTickerEnabled(WatchItem item, bool enabled)
        {
            if (item == null || item.TickerEnabled == enabled) return;
            item.TickerEnabled = enabled;
            PersistWatchlist();
        }

        public void MoveWatchTop(WatchItem item)
        {
            if (item == null || Watchlist.Remove(item) == false) return;
            Watchlist.Insert(0, item);
            PersistWatchlist();
        }

        public void MoveWatch(WatchItem item, int delta)
        {
            if (item == null) return;
            int index = Watchlist.IndexOf(item);
            int target = index + delta;
            if (index < 0 || target < 0 || target >= Watchlist.Count) return;
            Watchlist.RemoveAt(index);
            Watchlist.Insert(target, item);
            PersistWatchlist();
        }

        public void ClearWatchlist()
        {
            Watchlist.Clear();
            PersistWatchlist();
        }

        /// <summary>
        /// 行情回来后补齐「只有代码、还没名称」的自选（用户直接输入 6 位代码添加时的占位名）。
        /// 名称本来就有的不做覆盖，避免把搜索结果里的规范名称改掉。
        /// </summary>
        public void SyncNamesFromQuotes(QuoteHub hub)
        {
            bool dirty = false;
            for (int i = 0; i < Watchlist.Count; i++)
            {
                WatchItem item = Watchlist[i];
                Quote quote = hub.GetQuote(item.Code, item.Market);
                if (quote == null || string.IsNullOrEmpty(quote.Name)) continue;
                if (!IsPlaceholderName(item.Name, item.Code)) continue;
                if (item.Name != quote.Name)
                {
                    item.Name = quote.Name;
                    dirty = true;
                }
            }
            if (!dirty) return;
            Store.SaveWatchlist(Watchlist);
            if (_autoStarted) foreach (WatchItem item in Watchlist) if (Symbols.IsOpenFund(item.Code)) { var pending = AutoAssociateFundAsync(item, false); }
            Action handler = WatchlistChanged;
            if (handler != null) handler();
        }

        private static bool IsPlaceholderName(string name, string code)
        {
            return string.IsNullOrEmpty(name) || name == Symbols.FormatCode(code);
        }

        private void PersistWatchlist()
        {
            Store.Reindex(Watchlist);
            Store.SaveWatchlist(Watchlist);
            Hub.SetWatchlist(Watchlist);
            SyncBoardLinks();
            Action handler = WatchlistChanged;
            if (handler != null) handler();
        }

        public List<BoardLink> GetFundBoards(string code)
        {
            return BoardLink.Decode(Store.Get("fund_boards." + code.ToLowerInvariant(), ""));
        }

        public void SetFundBoards(string code, IList<BoardLink> links)
        {
            if (!Symbols.IsOpenFund(code)) throw new ArgumentException("请选择场外基金");
            string key = code.ToLowerInvariant();
            Store.SetMany(new Dictionary<string,string> {
                { "fund_boards_mode." + key, "manual" },
                { "fund_boards." + key, BoardLink.Encode(links) }
            });
            SyncBoardLinks();
            Action handler = WatchlistChanged;
            if (handler != null) handler();
        }

        private void SyncBoardLinks()
        {
            var links = new List<BoardLink>();
            var funds = new List<string>();
            foreach (WatchItem item in Watchlist)
            {
                if (!Symbols.IsOpenFund(item.Code)) continue;
                links.AddRange(GetFundBoards(item.Code));
                funds.Add(item.Code);
            }
            Hub.SetBoardLinks(links);
            Hub.SetEstimateFunds(funds);
        }

        public string FundBoardSummary(string code)
        {
            var parts = new List<string>();
            foreach (BoardLink link in GetFundBoards(code))
                parts.Add(FundPresentation.BoardText(link, Hub.Boards.Get(link.Code), MarketClock.NowCst()));
            return parts.Count == 0 ? "未关联板块 · 点击关联板块设置" : string.Join("；", parts.ToArray());
        }

        /// <summary>
        /// 这只基金此刻的盘中估算。两路来源，优先级明确：
        ///   1. <b>前十大重仓加权</b>（<see cref="QuoteHub.Estimates"/>）—— 与养基宝同一口径，和持仓同向；
        ///   2. 拿不到重仓（QDII / 季报缺失 / 接口不通）时退到<b>关联板块等权</b>。
        /// 自选表 / 面板 / 浮窗 / 详情都走这一个入口，保证四处的涨跌是同一个数。
        /// </summary>
        public FundEstimate EstimateFund(string code)
        {
            FundEstimate holdings = Hub.Estimates.Get(code);
            if (holdings != null && holdings.HasValue) return holdings;

            List<BoardLink> links = GetFundBoards(code);
            if (links.Count == 0) return new FundEstimate();
            List<BoardQuote> quotes = new List<BoardQuote>(links.Count);
            for (int i = 0; i < links.Count; i++) quotes.Add(Hub.Boards.Get(links[i].Code));
            return FundPresentation.Estimate(quotes, MarketClock.NowCst());
        }

        // ── 数据源 ──

        public void SwitchSource(string name)
        {
            SetSetting("active_datasource", name);
            Hub.SetActiveSource(name);
            Action handler = SourceChanged;
            if (handler != null) handler();
        }

        // ── 窗口 ──

        public void ShowMain()
        {
            if (MainWindow == null) return;
            if (!MainWindow.Visible) MainWindow.Show();
            if (MainWindow.WindowState == FormWindowState.Minimized)
                MainWindow.WindowState = FormWindowState.Normal;
            MainWindow.ShowInTaskbar = true;
            MainWindow.Activate();
            MainWindow.BringToFront();
        }

        public void HideMain()
        {
            if (MainWindow != null) MainWindow.Hide();
        }

        /// <summary>用户主动显示浮窗（在下一个时段边界前不受定时隐藏影响）。</summary>
        public void ShowTicker()
        {
            if (Ticker == null) return;
            Ticker.ShowByUser();
            Store.SetBool("ticker_visible", true);
            Log.Info("浮窗 → 显示");
        }

        /// <summary>用户主动隐藏浮窗（在下一个时段边界前不会被定时调度重新拉起）。</summary>
        public void HideTicker()
        {
            if (Ticker == null) return;
            Ticker.HideByUser();
            Store.SetBool("ticker_visible", false);
            Log.Info("浮窗 → 隐藏");
        }

        /// <summary>
        /// 显示/隐藏切换。
        /// 判定用托管可见位（Form.Visible）而不是系统可见性：两者不一致时（窗口落在屏幕外、
        /// 被系统隐藏）真正的修复机会在 ShowBySchedule —— 它每次都会重新贴边、重新置顶、
        /// 重新推一帧位图，所以「隐藏 → 再显示」这一轮就能把窗口找回来。
        /// </summary>
        public void ToggleTicker()
        {
            if (Ticker == null) return;
            if (Ticker.Visible) HideTicker();
            else ShowTicker();
        }

        /// <summary>
        /// 启动时决定浮窗初始可见性：
        ///   · 上次退出时是「用户隐藏」状态 → 保持隐藏（否则启动一秒后调度器又把它拉出来，
        ///     这正是「隐藏按钮失效」的另一种表现）；
        ///   · 上次是显示状态 → 时段内直接显示，时段外交给调度到点拉起。
        /// </summary>
        public void StartTickerBySchedule()
        {
            if (Ticker == null) return;
            if (!Store.GetBool("ticker_visible", true))
            {
                Ticker.StartHiddenByIntent();
                Log.Info("浮窗按上次状态保持隐藏（到下一个时段边界再交给调度）");
                return;
            }
            if (TickerShouldBeVisible(DateTime.Now)) Ticker.ShowBySchedule();
            else
            {
                Ticker.HideBySchedule();
                Log.Info("浮窗处于隐藏时段（" + TickerScheduleSummary() + "），到点自动显示");
            }
            Ticker.RefreshSchedule(true);
        }

        /// <summary>打开 480×360 面板（管理自选 / 设置）。托盘菜单与浮窗右键都走这里。</summary>
        public void OpenPanel(string tab)
        {
            // 不再「先强制显示浮窗再打开面板」：那会把用户的隐藏意图改写成显示，
            // 关掉面板后浮窗又出现，看起来就像隐藏操作失效了。
            PanelForm.ShowPanel(this, tab);
        }

        public void Quit()
        {
            try
            {
                if (Hub != null) Hub.Stop();
                if (Tray != null) Tray.Dispose();
                if (Ticker != null) Ticker.ForceClose();
                if (MainWindow != null)
                {
                    MainWindow.AllowClose = true;
                    MainWindow.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("退出清理失败: " + ex.Message);
            }
            if (Context != null) Context.ExitThread();
            else Application.Exit();
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ── 开机自启 ──

        public bool AutoStart
        {
            get { return Native.GetAutoStart(); }
        }

        public void SetAutoStart(bool enabled)
        {
            if (!Native.SetAutoStart(enabled))
                MessageBox.Show("设置开机自启失败（可能被安全软件拦截）", "QuantNative",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Store.SetBool("auto_launch", enabled);
        }
    }
}
