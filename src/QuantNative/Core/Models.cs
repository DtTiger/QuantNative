using System;
using System.Collections.Generic;

namespace QuantNative.Core
{
    /// <summary>
    /// 行情数据模型。字段与 Tauri 版 src-tauri/src/domain/mod.rs 一一对应，
    /// 单位也保持一致：Volume=股，Turnover=元（数据源适配器负责归一化）。
    /// </summary>
    public sealed class Quote
    {
        public string Code { get; set; }          // 完整代码，带交易所前缀（sh600519 / sz000001）
        public string Market { get; set; }        // CN / HK / US
        public string Name { get; set; }
        public double Price { get; set; }
        public double Change { get; set; }
        public double ChangePct { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public long Volume { get; set; }          // 股
        public double Turnover { get; set; }      // 元
        public double? TurnoverRate { get; set; } // 换手率 %（新浪不提供时为 null）
        public long Timestamp { get; set; }       // Unix 秒

        /// <summary>
        /// 价格性质："估值" / "净值" / 空。场外基金盘中给的是估算净值（估值），
        /// 净值公布后给的是单位净值，两者口径不同，必须在界面上区分开。
        /// </summary>
        public string PriceKind { get; set; }

        /// <summary>价格对应的时间：估值时间（HH:mm）或净值日期（yyyy-MM-dd）。</summary>
        public string PriceTime { get; set; }

        public Quote Clone()
        {
            return (Quote)MemberwiseClone();
        }
    }

    public sealed class IndexQuote
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public double Price { get; set; }
        public double Change { get; set; }
        public double ChangePct { get; set; }
        public long Volume { get; set; }
        public double Turnover { get; set; }
    }

    public sealed class Level
    {
        public double Price { get; set; }
        public long Volume { get; set; }   // 股

        public Level() { }

        public Level(double price, long volume)
        {
            Price = price;
            Volume = volume;
        }
    }

    public sealed class Depth
    {
        public string Code { get; set; }
        public List<Level> Bids = new List<Level>();
        public List<Level> Asks = new List<Level>();
    }

    public sealed class MinutePoint
    {
        public string Time { get; set; }   // "09:35"
        public double Price { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public long Volume { get; set; }
        public double AvgPrice { get; set; }
    }

    public sealed class KLineBar
    {
        public string Date { get; set; }   // 日/周/月："2026-06-19"；分钟："2026-06-19 09:35"
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
        public double Turnover { get; set; }
    }

    public sealed class StockBrief
    {
        public string Code { get; set; }
        public string Market { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }   // GP-A / GP-B / ETF / LOF / ZS / OF / ""
        /// <summary>
        /// 能稳定提供该标的实时数据的「数据源 Name」。空 = 用当前激活的数据源
        /// （CSV含交易所前缀的场内代码，腾讯与新浪都能取）。场外基金必须标为
        /// eastmoney-fund，否则会退回到不支持它的场内数据源上。
        /// </summary>
        public string Source { get; set; }
        /// <summary>数据源显示名（仅用于界面提示，不参与逻辑）。</summary>
        public string SourceName { get; set; }
        /// <summary>场外基金的基金类型描述（如「混合型-偏股」），场内标的为空。</summary>
        public string TypeDesc { get; set; }
    }

    /// <summary>
    /// 基金前十大重仓股。用于「自选基金自动关联板块」的第三路信号：
    /// 重仓股 → 所属行业 → 对应板块（对齐主流基金 App 的关联口径）。
    /// </summary>
    public sealed class FundHolding
    {
        /// <summary>带交易所前缀的股票代码（sh600519 / sz300750）。</summary>
        public string Code { get; set; }
        public string Name { get; set; }
        /// <summary>占净值比例 %。</summary>
        public double Weight { get; set; }
    }

    public sealed class WatchItem
    {
        public int Id { get; set; }
        public string Code { get; set; }
        public string Market { get; set; }
        public string Name { get; set; }
        public int SortOrder { get; set; }
        public string AddedAt { get; set; }
        /// <summary>是否参与浮窗滚动播报（新增自选默认 true）。</summary>
        public bool TickerEnabled { get; set; }
        /// <summary>该标的应由哪个数据源提供实时数据（空 = 当前激活的数据源）。</summary>
        public string Source { get; set; }

        public WatchItem()
        {
            Market = "CN";
            TickerEnabled = true;
            AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    public sealed class SectorItem
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public double ChangePct { get; set; }
        /// <summary>板块内涨幅最高的成分股（涨幅榜用）。</summary>
        public string LeaderName { get; set; }
        public double? LeaderPct { get; set; }
        /// <summary>板块内跌幅最深的成分股（跌幅榜用，来自东财 f207/f222）。</summary>
        public string LaggardName { get; set; }
        public double? LaggardPct { get; set; }
    }

    public sealed class MarketOverview
    {
        public double Turnover { get; set; }                       // 两市成交额（元）
        public int Up { get; set; }
        public int Down { get; set; }
        public int Flat { get; set; }
        public List<SectorItem> Industry = new List<SectorItem>(); // 行业涨幅榜前 5
        public List<SectorItem> Concept = new List<SectorItem>();  // 概念涨幅榜前 5
    }

    /// <summary>A 股交易时段（北京时间）。</summary>
    public enum MarketSession
    {
        PreOpen,        // 09:30 之前
        MorningTrade,   // 09:30 - 11:30
        LunchBreak,     // 11:30 - 13:00
        AfternoonTrade, // 13:00 - 15:00
        Closed          // 15:00 之后或周末
    }

    /// <summary>图表周期。与 Tauri 版的 PeriodType 对齐。</summary>
    public enum ChartPeriod
    {
        Minute,   // 分时
        Min1,
        Min5,
        Min15,
        Min30,
        Min60,
        Daily,
        Weekly,
        Monthly
    }

    public static class ChartPeriods
    {
        /// <summary>转成数据源使用的周期字符串（与 Rust 端一致）。</summary>
        public static string ToKey(ChartPeriod p)
        {
            switch (p)
            {
                case ChartPeriod.Min1: return "1min";
                case ChartPeriod.Min5: return "5min";
                case ChartPeriod.Min15: return "15min";
                case ChartPeriod.Min30: return "30min";
                case ChartPeriod.Min60: return "60min";
                case ChartPeriod.Weekly: return "weekly";
                case ChartPeriod.Monthly: return "monthly";
                case ChartPeriod.Daily: return "daily";
                default: return "minute";
            }
        }

        /// <summary>分钟周期返回分钟跨度，日/周/月返回 null。</summary>
        public static int? MinuteSpan(ChartPeriod p)
        {
            switch (p)
            {
                case ChartPeriod.Min1: return 1;
                case ChartPeriod.Min5: return 5;
                case ChartPeriod.Min15: return 15;
                case ChartPeriod.Min30: return 30;
                case ChartPeriod.Min60: return 60;
                default: return null;
            }
        }

        public static string Display(ChartPeriod p)
        {
            switch (p)
            {
                case ChartPeriod.Minute: return "分时";
                case ChartPeriod.Min1: return "1分";
                case ChartPeriod.Min5: return "5分";
                case ChartPeriod.Min15: return "15分";
                case ChartPeriod.Min30: return "30分";
                case ChartPeriod.Min60: return "60分";
                case ChartPeriod.Weekly: return "周K";
                case ChartPeriod.Monthly: return "月K";
                default: return "日K";
            }
        }
    }
}
