using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuantNative.Core;

namespace QuantNative.Data
{
    /// <summary>
    /// 场外基金「重仓股加权估算」—— 盘中估值的**主口径**，也是养基宝 / 天天基金的那套算法：
    ///   估算涨跌 = Σ(前十大持仓的占净值比例% × 该股当日涨跌%) / 100
    /// 未披露部分（现金 + 非重仓股）按持平处理，所以不会放大波动。
    ///
    /// 为什么不能用关联板块等权顶替：板块是「主题的代理」，和基金真实持仓可能反着走。
    /// 实测 017811（东方人工智能主题混合C）2026-10-20 上午：半导体材料 +2.76%、半导体设备 +0.92%
    /// → 板块等权 +1.84%，但它的十大重仓（中科飞测 −3.11%、拓荆 −1.80%…）加权只有 −0.21%，
    /// 与养基宝显示的「收益为负」一致。板块等权因此降级为兜底（QDII / 季报缺失 / 接口不通）。
    ///
    /// 取数节奏：重仓名单 6 小时一次（失败 5 分钟后重试），个股行情沿用主数据源（腾讯/新浪）
    /// 批量拉取，4 秒 TTL —— 与浮窗刷新同频，不走被区域网络限制的东财 push2。
    /// </summary>
    public sealed class HoldingsEstimator
    {
        private const double MinCoverage = 5.0;      // 重仓合计权重低于此值就不估（信息不足）
        private static readonly TimeSpan HoldingsTtl = TimeSpan.FromHours(6);
        private static readonly TimeSpan HoldingsRetry = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan QuoteTtl = TimeSpan.FromSeconds(4);

        private sealed class Snapshot
        {
            public List<FundHolding> Holdings = new List<FundHolding>();
            public DateTime At = DateTime.MinValue;
            public DateTime FailedAt = DateTime.MinValue;
        }

        private readonly Dictionary<string, Snapshot> _snapshots =
            new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, FundEstimate> _estimates =
            new Dictionary<string, FundEstimate>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private DateTime _quotesAt = DateTime.MinValue;

        /// <summary>取某只基金最近一次算好的估值（没有则 null）。</summary>
        public FundEstimate Get(string fundCode)
        {
            string six = Symbols.OpenFundCode(fundCode ?? "");
            lock (_gate)
            {
                FundEstimate estimate;
                return _estimates.TryGetValue(six, out estimate) ? estimate : null;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _snapshots.Clear();
                _estimates.Clear();
                _quotesAt = DateTime.MinValue;
            }
        }

        /// <summary>一轮刷新：补齐重仓 → 批量取个股行情 → 逐只基金加权。异常一律吞掉，不影响主行情。</summary>
        public async Task RefreshAsync(IList<string> fundCodes, EastmoneyFundSource funds, IQuoteSource stockSource)
        {
            if (fundCodes == null || fundCodes.Count == 0 || funds == null || stockSource == null) return;
            try
            {
                DateTime now = MarketClock.NowCst();
                await LoadHoldingsAsync(fundCodes, funds, now).ConfigureAwait(false);

                List<string> codes = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                lock (_gate)
                {
                    foreach (KeyValuePair<string, Snapshot> kv in _snapshots)
                        for (int i = 0; i < kv.Value.Holdings.Count; i++)
                        {
                            FundHolding holding = kv.Value.Holdings[i];
                            if (holding == null || string.IsNullOrEmpty(holding.Code)) continue;
                            if (seen.Add(Normalize(holding.Code))) codes.Add(holding.Code);
                        }
                }
                if (codes.Count == 0) return;

                lock (_gate) { if (now - _quotesAt < QuoteTtl) return; _quotesAt = now; }

                List<Quote> quotes = await stockSource.FetchRealtimeAsync(codes, "CN").ConfigureAwait(false);
                Dictionary<string, double> changePct = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int i = 0; i < quotes.Count; i++)
                {
                    Quote quote = quotes[i];
                    if (quote == null || string.IsNullOrEmpty(quote.Code) || double.IsNaN(quote.ChangePct)) continue;
                    changePct[Normalize(quote.Code)] = quote.ChangePct;
                }
                Apply(fundCodes, changePct);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("holdings-estimate", "重仓股估算失败: " + ex.Message, TimeSpan.FromMinutes(2));
            }
        }

        private async Task LoadHoldingsAsync(IList<string> fundCodes, EastmoneyFundSource funds, DateTime now)
        {
            for (int i = 0; i < fundCodes.Count; i++)
            {
                string raw = fundCodes[i] ?? "";
                if (!Symbols.IsOpenFund(raw)) continue;
                string six = Symbols.OpenFundCode(raw);
                if (six.Length != 6) continue;

                Snapshot snapshot;
                lock (_gate)
                {
                    if (!_snapshots.TryGetValue(six, out snapshot))
                    {
                        snapshot = new Snapshot();
                        _snapshots[six] = snapshot;
                    }
                }

                bool need;
                lock (_gate)
                {
                    bool stale = snapshot.Holdings.Count == 0 || now - snapshot.At >= HoldingsTtl;
                    bool retryDue = snapshot.FailedAt == DateTime.MinValue || now - snapshot.FailedAt >= HoldingsRetry;
                    need = stale && retryDue;
                }
                if (!need) continue;

                try
                {
                    List<FundHolding> holdings = await funds.FetchTopHoldingsAsync(six, true).ConfigureAwait(false);
                    lock (_gate)
                    {
                        snapshot.Holdings = holdings ?? new List<FundHolding>();
                        snapshot.At = now;
                        snapshot.FailedAt = snapshot.Holdings.Count == 0 ? now : DateTime.MinValue;
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate) { snapshot.FailedAt = now; }
                    Log.WarnOnce("holdings:" + six, "重仓股获取失败 " + six + ": " + ex.Message, TimeSpan.FromMinutes(10));
                }
            }
        }

        private void Apply(IList<string> fundCodes, Dictionary<string, double> changePct)
        {
            lock (_gate)
            {
                for (int i = 0; i < fundCodes.Count; i++)
                {
                    string six = Symbols.OpenFundCode(fundCodes[i] ?? "");
                    if (six.Length != 6) continue;

                    Snapshot snapshot;
                    if (!_snapshots.TryGetValue(six, out snapshot) || snapshot.Holdings.Count == 0)
                    {
                        _estimates.Remove(six);
                        continue;
                    }

                    double pct, coverage;
                    int used;
                    if (!TryWeighted(snapshot.Holdings, changePct, out pct, out coverage, out used))
                    {
                        _estimates.Remove(six);
                        continue;
                    }

                    FundEstimate estimate = new FundEstimate();
                    estimate.HasValue = true;
                    estimate.Pct = pct;
                    estimate.Count = used;
                    estimate.Stale = snapshot.Holdings.Count - used;
                    estimate.Source = "holdings";
                    estimate.Detail = "重仓股 " + used + " 只 · 覆盖 " + coverage.ToString("F0") + "%";
                    _estimates[six] = estimate;
                }
            }
        }

        /// <summary>
        /// 前十大重仓加权：Σ(占净值比例% × 个股涨跌%) / 100。
        /// 权重口径就是「占净值比例」，未披露部分按持平，所以不除以覆盖比重（那会放大波动）。
        /// </summary>
        public static bool TryWeighted(IList<FundHolding> holdings, IDictionary<string, double> changePct,
            out double pct, out double coverage, out int used)
        {
            pct = 0;
            coverage = 0;
            used = 0;
            if (holdings == null || changePct == null || holdings.Count == 0) return false;

            double sum = 0;
            double weight = 0;
            for (int i = 0; i < holdings.Count; i++)
            {
                FundHolding holding = holdings[i];
                if (holding == null || string.IsNullOrEmpty(holding.Code)) continue;
                double change;
                if (!changePct.TryGetValue(Normalize(holding.Code), out change)) continue;
                sum += holding.Weight * change;
                weight += holding.Weight;
                used++;
            }
            if (used == 0 || weight < MinCoverage) return false;
            pct = sum / 100.0;
            coverage = weight;
            return true;
        }

        private static string Normalize(string code)
        {
            return (code ?? "").Trim().ToLowerInvariant();
        }
    }
}
