using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using QuantNative.Data;

namespace QuantNative.Core
{
    /// <summary>
    /// 行情中枢：轮询调度 + 内存缓存 + 事件广播。
    ///
    /// 设计要点（对齐 Rust 端 cache/mod.rs 的 Scheduler）：
    ///   * 用 WinForms Timer 驱动，HTTP 用 async/await —— 全部回调都在 UI 线程，
    ///     UI 直接订阅事件即可，不需要 Invoke/Marshal 往返（这是原生版比 WebView
    ///     版少一层 IPC 的地方）。
    ///   * 自适应节奏：进入交易时段先「探测」3 次，之后正常节奏；连续 10 次价格不变
    ///     或处于休市 → 退避到 30 秒。休市期间不再 2 秒空转。
    ///   * 数据源可切换；切换后立即唤醒刷新一次。
    /// </summary>
    public sealed class QuoteHub
    {
        private enum Pace
        {
            Probing,
            Normal,
            Idle
        }

        private readonly Timer _timer;                 // 1 秒心跳，决定这一秒要不要发请求
        private readonly Dictionary<string, Quote> _quotes = new Dictionary<string, Quote>(StringComparer.Ordinal);
        private List<IndexQuote> _indices = new List<IndexQuote>();
        private List<WatchItem> _watchlist = new List<WatchItem>();

        private readonly List<IQuoteSource> _sources = new List<IQuoteSource>();
        private readonly EastmoneyStockQuoteClient _eastmoneyStocks = new EastmoneyStockQuoteClient();
        public readonly BoardClient Boards = new BoardClient();
        /// <summary>场外基金的盘中估值（前十大重仓加权，主口径）。</summary>
        public readonly HoldingsEstimator Estimates = new HoldingsEstimator();
        private List<BoardLink> _boardLinks = new List<BoardLink>();
        private List<string> _estimateFunds = new List<string>();
        public void SetBoardLinks(List<BoardLink> links) { _boardLinks = links; Wakeup(); }
        /// <summary>需要算盘中估值的场外基金（自选里的 OF 代码）。</summary>
        public void SetEstimateFunds(List<string> fundCodes) { _estimateFunds = fundCodes ?? new List<string>(); Wakeup(); }
        private IQuoteSource _active;

        private DateTime _nextQuotePoll = DateTime.MinValue;
        private DateTime _nextOverviewPoll = DateTime.MinValue;
        private Pace _pace = Pace.Normal;
        private int _probeLeft;
        private int _unchangedPolls;
        private bool _quoteBusy;
        private bool _overviewBusy;
        private int _wakeupSeq;
        private bool _overviewEnabled;
        private string _overviewDirection = "up";
        private MarketSession _session;
        private string _lastError;
        private RefreshProfile _profile = RefreshProfile.Fast;
        private readonly List<string> _pollErrors = new List<string>();

        /// <summary>行情数据刷新（个股 + 指数）。</summary>
        public event Action QuotesUpdated;
        /// <summary>市场概览刷新。</summary>
        public event Action OverviewUpdated;
        /// <summary>交易时段切换（用于状态栏文案与轮询节奏）。</summary>
        public event Action SessionChanged;

        public QuoteHub()
        {
            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnHeartbeat;
            _session = MarketClock.Current();
        }

        // ── 数据源 ──

        public IQuoteSource ActiveSource { get { return _active; } }

        public IList<IQuoteSource> Sources { get { return _sources; } }

        public void Register(IQuoteSource source)
        {
            _sources.Add(source);
            if (_active == null) _active = source;
        }

        public MarketOverview Overview { get; private set; }

        public void SetActiveSource(string name)
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                if (_sources[i].Name == name)
                {
                    _active = _sources[i];
                    _lastError = null;
                    Wakeup();
                    return;
                }
            }
            Log.Warn("未知数据源: " + name);
        }

        // ── 生命周期 ──

        public void Start()
        {
            _timer.Start();
            Wakeup();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        /// <summary>立即刷新（自选变化、数据源切换、手动重试时调用）。</summary>
        public void Wakeup()
        {
            _nextQuotePoll = DateTime.MinValue;
            _wakeupSeq++;
        }

        /// <summary>
        /// 切换刷新档位。换档后立刻重排下一次轮询时间并立即刷一次，
        /// 这样用户调「极速」时是马上见效的，而不是等下一个周期。
        /// </summary>
        public void SetRefreshProfile(RefreshProfile profile)
        {
            _profile = profile;
            _nextQuotePoll = DateTime.MinValue;
            _nextOverviewPoll = DateTime.MinValue;
            _wakeupSeq++;
            Log.Info("刷新档位 → " + MarketClock.ProfileName(profile));
        }

        public RefreshProfile Profile { get { return _profile; } }

        /// <summary>手动「立即刷新」：清掉节奏限制并马上打一轮请求。</summary>
        public void ForceRefresh()
        {
            _nextQuotePoll = DateTime.MinValue;
            _nextOverviewPoll = DateTime.MinValue;
            _wakeupSeq++;
            if (!_quoteBusy) PollQuotesAsync();
            if (_overviewEnabled && !_overviewBusy) PollOverviewAsync();
        }

        /// <summary>自选列表变化时同步待轮询的代码集合。</summary>
        public void SetWatchlist(List<WatchItem> items)
        {
            _watchlist = items ?? new List<WatchItem>();
            Wakeup();
        }

        /// <summary>概览面板可见时才轮询概览，隐藏后不再发请求。</summary>
        public void SetOverviewEnabled(bool enabled)
        {
            _overviewEnabled = enabled;
            if (enabled) _nextOverviewPoll = DateTime.MinValue;
        }

        /// <summary>概览榜单方向：up=涨幅榜，down=跌幅榜。</summary>
        public void SetOverviewDirection(string direction)
        {
            if (direction == _overviewDirection) return;
            _overviewDirection = direction == "down" ? "down" : "up";
            if (_overviewEnabled) _nextOverviewPoll = DateTime.MinValue;
        }

        // ── 读取（供 UI 直接绑定） ──

        public Quote GetQuote(string code, string market)
        {
            Quote quote;
            return _quotes.TryGetValue(Key(market, code), out quote) ? quote : null;
        }

        public IReadOnlyList<IndexQuote> Indices { get { return _indices; } }

        public MarketSession Session { get { return _session; } }

        public DateTime LastUpdate { get; private set; }

        public string LastError { get { return _lastError; } }

        public static string Key(string market, string code)
        {
            return (market ?? "CN") + ":" + (code ?? "");
        }

        // ── 心跳 ──

        private void OnHeartbeat(object sender, EventArgs e)
        {
            MarketSession session = MarketClock.Current();
            if (session != _session)
            {
                _session = session;
                ApplySessionPolicy();
                Action handler = SessionChanged;
                if (handler != null) handler();
            }

            if (DateTime.Now >= _nextQuotePoll && !_quoteBusy)
            {
                // fire-and-forget：内部全 async，异常在方法内兜住，不会漏到 UI 线程
                PollQuotesAsync();
            }

            if (_overviewEnabled && DateTime.Now >= _nextOverviewPoll && !_overviewBusy)
            {
                PollOverviewAsync();
            }
        }

        /// <summary>进入新交易时段时切换节奏；休市退避到 30 秒。</summary>
        private void ApplySessionPolicy()
        {
            if (MarketClock.IsTrading(_session))
            {
                // 进入交易时段：先密集探测，尽快拿到开盘后的第一笔价格变化
                _pace = Pace.Probing;
                _probeLeft = 3;
            }
            else
            {
                _pace = Pace.Idle;
            }
            _unchangedPolls = 0;
            Wakeup();
            Log.Info("交易时段 → " + MarketClock.Name(_session)
                + "（" + MarketClock.ProfileName(_profile) + "，轮询 " + CurrentIntervalSeconds() + "s）");
        }

        private int CurrentIntervalSeconds()
        {
            if (_pace == Pace.Probing) return MarketClock.ProbeIntervalSeconds(_profile);
            if (_pace == Pace.Idle) return MarketClock.QuoteIntervalSeconds(_session, _profile);
            return MarketClock.QuoteIntervalSeconds(_session, _profile);
        }

        private async void PollQuotesAsync()
        {
            _quoteBusy = true;
            int wakeupAtStart = _wakeupSeq;
            _pollErrors.Clear();
            try
            {
                if (_active == null)
                {
                    _nextQuotePoll = DateTime.Now.AddSeconds(30);
                    return;
                }

                // 指数、个股、板块三路并行：互不依赖，串行会让周期翻倍。
                // 每路各自「到达即通知」—— 自选行情一到就先刷界面，
                // 不必等指数和板块都回来（这是拖慢观感的主要来源）。
                IQuoteSource indexSource = IndexSource();
                Task<List<IndexQuote>> indexTask = indexSource == null
                    ? Task.FromResult(new List<IndexQuote>())
                    : FetchIndicesSafeAsync(indexSource);
                Task<List<Quote>> quoteTask = FetchWatchQuotesAsync();
                Task boardTask = Boards.RefreshAsync(_boardLinks);
                // 重仓股估值与主行情并行；它自己内部吞异常，失败只会退回板块口径
                Task estimateTask = Task.FromResult(true);
                if (_estimateFunds.Count > 0)
                {
                    EastmoneyFundSource fundSource = FundSource();
                    IQuoteSource stockSource = StockSource();
                    if (fundSource != null && stockSource != null)
                        estimateTask = Estimates.RefreshAsync(_estimateFunds, fundSource, stockSource);
                }

                Task<bool> quotesApplied = ApplyQuotesAsync(quoteTask);
                Task<bool> indicesApplied = ApplyIndicesAsync(indexTask);
                Task boardNotify = _boardLinks.Count == 0 ? boardTask : NotifyBoardsAsync(boardTask);

                await Task.WhenAll(indicesApplied, quotesApplied, boardNotify, estimateTask);

                bool changed = quotesApplied.Result;
                _lastError = _pollErrors.Count == 0 ? null : string.Join("; ", _pollErrors.ToArray());
                UpdatePace(changed);

                Action handler = QuotesUpdated;
                if (handler != null) handler();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Warn("行情刷新失败: " + ex.Message);
                // 失败时退避，避免接口挂掉时每秒重试打满 CPU
                _nextQuotePoll = DateTime.Now.AddSeconds(Math.Max(5, CurrentIntervalSeconds()));
            }
            finally
            {
                // 轮询期间若有人调用过 Wakeup（自选增删、切数据源、换档位），不要用固定间隔
                // 覆盖掉那个「立刻再刷一次」的请求，否则用户操作后要等满一个周期。
                if (_wakeupSeq == wakeupAtStart
                    && (_nextQuotePoll <= DateTime.Now))
                    _nextQuotePoll = DateTime.Now.AddSeconds(CurrentIntervalSeconds());
                _quoteBusy = false;
            }
        }

        /// <summary>把自选行情写入缓存；一有数据就先通知界面一次。</summary>
        private async Task<bool> ApplyQuotesAsync(Task<List<Quote>> quoteTask)
        {
            List<Quote> quotes = await quoteTask;
            bool changed = false;
            for (int i = 0; i < quotes.Count; i++)
            {
                Quote fresh = quotes[i];
                Quote old;
                string key = Key(fresh.Market, fresh.Code);
                if (_quotes.TryGetValue(key, out old))
                {
                    if (old.Price != fresh.Price) changed = true;
                }
                else
                {
                    changed = true;
                }
                _quotes[key] = fresh;
            }
            if (quotes.Count > 0)
            {
                LastUpdate = DateTime.Now;
                NotifyQuotes();
            }
            return changed;
        }

        /// <summary>
        /// 把指数写入缓存并立刻通知（大盘数据比自选行情更早到时就先显示）。
        ///
        /// 顺序修正：数据源返回的顺序并不稳定（新浪把上证放第二、腾讯偶尔少返一项、
        /// 回落备用源时顺序又不同）。旧版直接按返回顺序平铺到指数条上，一旦顺序变了，
        /// 上证指数的位置就会显示成深证成指 —— 这就是「大盘数据显示位置不正确」的根因之一。
        /// 这里统一按 <see cref="Symbols.IndexOrder"/> 的固定名次重排，缺名的补兜底名，
        /// 让「上证 / 深成 / 创业板 / 科创50 / 中证500 …」永远各归各位。
        /// </summary>
        private async Task<bool> ApplyIndicesAsync(Task<List<IndexQuote>> indexTask)
        {
            List<IndexQuote> indices = await indexTask;
            if (indices == null || indices.Count == 0) return false;
            _indices = NormalizeIndices(indices);
            LastUpdate = DateTime.Now;
            NotifyQuotes();
            return true;
        }

        /// <summary>按固定名次重排指数，并给缺名的补上兜底名称（供单测直接调用）。</summary>
        public static List<IndexQuote> NormalizeIndices(List<IndexQuote> indices)
        {
            List<IndexQuote> result = new List<IndexQuote>(indices.Count);
            for (int i = 0; i < indices.Count; i++)
            {
                IndexQuote index = indices[i];
                if (index == null || string.IsNullOrEmpty(index.Code)) continue;
                if (string.IsNullOrEmpty(index.Name)) index.Name = Symbols.IndexFallbackName(index.Code);
                result.Add(index);
            }
            result.Sort(delegate(IndexQuote a, IndexQuote b)
            {
                int rank = Symbols.IndexRank(a.Code).CompareTo(Symbols.IndexRank(b.Code));
                if (rank != 0) return rank;
                // 名次相同（都是列表外的指数）就按代码字典序，保证排序结果稳定
                return string.CompareOrdinal(a.Code, b.Code);
            });
            return result;
        }

        private void NotifyQuotes()
        {
            Action handler = QuotesUpdated;
            if (handler != null) handler();
        }

        private async Task NotifyBoardsAsync(Task boardTask)
        {
            await boardTask;
            // 板块已到就先刷新浮窗与主表，不等基金净值或指数请求。
            Action handler = QuotesUpdated;
            if (handler != null) handler();
        }

        private async Task<List<Quote>> FetchWatchQuotesAsync()
        {
            // 按「数据源 + 市场」分组：场外基金走天天基金、场内标的走腾讯/新浪，
            // 一次轮询里各发各的请求（并发），某一家挂掉不影响其它标的继续显示。
            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Dictionary<string, IQuoteSource> owners = new Dictionary<string, IQuoteSource>(StringComparer.Ordinal);
            Dictionary<string, string> markets = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = 0; i < _watchlist.Count; i++)
            {
                WatchItem item = _watchlist[i];
                if (item == null || string.IsNullOrEmpty(item.Code)) continue;

                IQuoteSource source = ResolveSource(item);
                if (source == null) continue;

                string market = string.IsNullOrEmpty(item.Market) ? "CN" : item.Market;
                string key = source.Name + "|" + market;

                List<string> codes;
                if (!groups.TryGetValue(key, out codes))
                {
                    codes = new List<string>();
                    groups[key] = codes;
                    owners[key] = source;
                    markets[key] = market;
                }
                if (!codes.Contains(item.Code)) codes.Add(item.Code);
            }

            if (groups.Count == 0) return new List<Quote>();

            List<string> keys = new List<string>(groups.Keys);
            List<Task<List<Quote>>> tasks = new List<Task<List<Quote>>>();
            for (int i = 0; i < keys.Count; i++)
            {
                tasks.Add(FetchGroupAsync(owners[keys[i]], groups[keys[i]], markets[keys[i]]));
            }
            await Task.WhenAll(tasks).ConfigureAwait(true);

            List<Quote> result = new List<Quote>();
            for (int i = 0; i < tasks.Count; i++)
            {
                List<Quote> part = tasks[i].Result;
                if (part != null && part.Count > 0) result.AddRange(part);
            }
            return result;
        }

        private async Task<List<Quote>> FetchGroupAsync(IQuoteSource source, List<string> codes, string market)
        {
            List<Quote> primary = null;
            try
            {
                primary = await source.FetchRealtimeAsync(codes, market).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _pollErrors.Add(source.DisplayName + ": " + ex.Message);
                Log.Warn("数据源 " + source.Name + " 行情刷新失败: " + ex.Message);
            }
            if (primary == null) primary = new List<Quote>();
            // 腾讯和新浪互为备用：只补缺失代码，基金仍走正式净值接口。
            if (source is EastmoneyFundSource || market == "OF") return primary;
            var missing = new List<string>();
            foreach (string code in codes)
            {
                bool found = false;
                foreach (Quote quote in primary) if (quote.Code == code) { found = true; break; }
                if (!found) missing.Add(code);
            }
            if (missing.Count == 0) return primary;
            IQuoteSource alternate = source is TencentSource ? FindSource("sina") : FindSource("tencent");
            if (alternate != null && !ReferenceEquals(alternate, source)) try
            {
                List<Quote> supplement = await alternate.FetchRealtimeAsync(missing, market).ConfigureAwait(true);
                if (supplement != null) primary.AddRange(supplement);
            }
            catch (Exception ex)
            {
                _pollErrors.Add(alternate.DisplayName + ": " + ex.Message);
                Log.Warn("备用数据源 " + alternate.Name + " 行情刷新失败: " + ex.Message);
            }
            if (source is TencentSource || source is SinaSource)
            {
                missing.RemoveAll(delegate(string code) { foreach (Quote quote in primary) if (quote.Code == code) return true; return false; });
                if (missing.Count > 0) try { primary.AddRange(await _eastmoneyStocks.FetchAsync(missing)); }
                catch (Exception ex) { Log.Warn("东方财富备用股票行情失败: " + ex.Message); }
            }
            return primary;
        }

        private async Task<List<IndexQuote>> FetchIndicesSafeAsync(IQuoteSource source)
        {
            try { return await source.FetchIndicesAsync(); }
            catch (Exception ex)
            {
                _pollErrors.Add(source.DisplayName + " 指数: " + ex.Message);
                Log.Warn("指数刷新失败: " + ex.Message);
            }
            IQuoteSource alternate = source is TencentSource ? FindSource("sina")
                : source is SinaSource ? FindSource("tencent") : null;
            if (alternate != null) try { return await alternate.FetchIndicesAsync(); }
            catch (Exception ex) { Log.Warn("备用指数刷新失败: " + ex.Message); }
            return new List<IndexQuote>();
        }

        /// <summary>自选里的标的应交给哪个数据源（优先用它自己记录的来源）。</summary>
        private IQuoteSource ResolveSource(WatchItem item)
        {
            if (Symbols.IsOpenFund(item.Code) || item.Market == "OF")
                return FindSource(EastmoneyFundSource.SourceName);
            IQuoteSource source = FindSource(item.Source);
            if (source != null && !(source is EastmoneyFundSource)) return source;
            return IndexSource();
        }

        /// <summary>按代码找它所属的数据源（详情面板的盘口 / 分时 / K 线走这里）。</summary>
        private IQuoteSource ResolveSourceFor(string code)
        {
            if (!string.IsNullOrEmpty(code))
            {
                for (int i = 0; i < _watchlist.Count; i++)
                {
                    WatchItem item = _watchlist[i];
                    if (item != null && string.Equals(item.Code, code, StringComparison.Ordinal))
                        return ResolveSource(item);
                }
            }
            return ResolveSource(new WatchItem { Code = code });
        }

        private IQuoteSource FindSource(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _sources.Count; i++)
            {
                if (string.Equals(_sources[i].Name, name, StringComparison.Ordinal)) return _sources[i];
            }
            return null;
        }

        /// <summary>天天基金数据源（重仓股名单用它取，和自动关联共用同一份缓存）。</summary>
        public EastmoneyFundSource FundSource()
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                EastmoneyFundSource fund = _sources[i] as EastmoneyFundSource;
                if (fund != null) return fund;
            }
            return null;
        }

        /// <summary>A 股个股行情源（重仓股估算用它批量取价，走腾讯/新浪而不是被限流的 push2）。</summary>
        public IQuoteSource StockSource()
        {
            return ResolveSource(new WatchItem { Code = "sh600519", Market = "CN" });
        }

        /// <summary>
        /// 指数固定由「支持指数的数据源」提供：用户若把当前数据源切到天天基金
        /// （它只有场外基金），指数条不能因此变空。
        /// </summary>
        private IQuoteSource IndexSource()
        {
            if (_active != null && !(_active is EastmoneyFundSource)) return _active;
            for (int i = 0; i < _sources.Count; i++)
            {
                if (!(_sources[i] is EastmoneyFundSource)) return _sources[i];
            }
            return _active;
        }

        private void UpdatePace(bool changed)
        {
            if (changed) _unchangedPolls = 0;
            else _unchangedPolls++;

            if (MarketClock.IsTrading(_session))
            {
                // 即使价格暂时不动也继续刷新板块；旧退避会让基金看起来慢 30 秒。
                _pace = Pace.Normal;
                return;
            }
            if (_pace == Pace.Probing)
            {
                if (changed || --_probeLeft <= 0) _pace = Pace.Normal;
            }
            else if (_pace == Pace.Normal)
            {
                if (_unchangedPolls >= 10) _pace = Pace.Idle;
            }
            else if (_pace == Pace.Idle && changed)
            {
                _pace = Pace.Normal;
            }
        }

        // ── 市场概览（独立节奏：比报价慢得多） ──

        public async void PollOverviewAsync()
        {
            if (_overviewBusy) return;
            _overviewBusy = true;
            try
            {
                MarketOverview overview = await OverviewClientInstance.FetchAsync(_overviewDirection);
                Overview = overview;
                Action handler = OverviewUpdated;
                if (handler != null) handler();
            }
            catch (Exception ex)
            {
                Log.Warn("市场概览刷新失败: " + ex.Message);
            }
            finally
            {
                _nextOverviewPoll = DateTime.Now.AddSeconds(MarketClock.OverviewIntervalSeconds(_session, _profile));
                _overviewBusy = false;
            }
        }

        public string OverviewDirection { get { return _overviewDirection; } }

        public DateTime NextOverviewPoll { get { return _nextOverviewPoll; } }

        private OverviewClient _overviewClient;
        public OverviewClient OverviewClientInstance
        {
            get
            {
                if (_overviewClient == null) _overviewClient = new OverviewClient();
                return _overviewClient;
            }
        }

        // ── 按需请求（详情面板：盘口 / 分时 / K 线） ──

        public async Task<Depth> FetchDepthAsync(string code, string market)
        {
            IQuoteSource source = ResolveSourceFor(code);
            if (source == null) return new Depth { Code = code };
            return await source.FetchDepthAsync(code, market);
        }

        public async Task<List<MinutePoint>> FetchMinuteAsync(string code, string market)
        {
            IQuoteSource source = ResolveSourceFor(code);
            if (source == null) return new List<MinutePoint>();
            return await source.FetchMinuteAsync(code, market);
        }

        public Task<List<KLineBar>> FetchKLineAsync(string code, string market, ChartPeriod period)
        {
            IQuoteSource source = ResolveSourceFor(code);
            if (source == null) return Task.FromResult(new List<KLineBar>());
            return source.FetchKLineAsync(code, market, period, 0);
        }
    }
}
