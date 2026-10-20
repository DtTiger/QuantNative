using System;

namespace QuantNative.Core
{
    /// <summary>
    /// 刷新档位。频率越高，数字跳动越跟手，但对行情接口的压力越大；
    /// 默认 <see cref="Fast"/>，也就是交易时段 1 秒一次。
    /// </summary>
    public enum RefreshProfile
    {
        /// <summary>极速：交易时段 1 秒，概览 15 秒。</summary>
        Turbo,
        /// <summary>快速（默认）：交易时段 1 秒，概览 20 秒。</summary>
        Fast,
        /// <summary>均衡：交易时段 2 秒，概览 60 秒（2.3 版的行为）。</summary>
        Balanced,
        /// <summary>省流：交易时段 5 秒，概览 120 秒。</summary>
        Saver
    }

    /// <summary>
    /// A 股交易时段判定与轮询节奏（对齐 Rust 端 market_clock.rs）。
    /// 固定按 UTC+8 判断，不受用户本机时区影响。
    /// </summary>
    public static class MarketClock
    {
        private static readonly TimeSpan Cst = TimeSpan.FromHours(8);

        public static DateTime NowCst()
        {
            return DateTime.UtcNow.Add(Cst);
        }

        public static MarketSession Current()
        {
            DateTime now = NowCst();
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                return MarketSession.Closed;

            int minutes = now.Hour * 60 + now.Minute;
            if (minutes < 9 * 60 + 30) return MarketSession.PreOpen;
            if (minutes < 11 * 60 + 30) return MarketSession.MorningTrade;
            if (minutes < 13 * 60) return MarketSession.LunchBreak;
            if (minutes < 15 * 60) return MarketSession.AfternoonTrade;
            return MarketSession.Closed;
        }

        // ── 刷新档位 ──

        public static RefreshProfile ParseProfile(string key)
        {
            if (string.IsNullOrEmpty(key)) return RefreshProfile.Fast;
            switch (key.Trim().ToLowerInvariant())
            {
                case "turbo": return RefreshProfile.Turbo;
                case "balanced": return RefreshProfile.Balanced;
                case "saver": return RefreshProfile.Saver;
                default: return RefreshProfile.Fast;
            }
        }

        public static string ProfileKey(RefreshProfile profile)
        {
            switch (profile)
            {
                case RefreshProfile.Turbo: return "turbo";
                case RefreshProfile.Balanced: return "balanced";
                case RefreshProfile.Saver: return "saver";
                default: return "fast";
            }
        }

        public static string ProfileName(RefreshProfile profile)
        {
            switch (profile)
            {
                case RefreshProfile.Turbo: return "极速（1 秒）";
                case RefreshProfile.Balanced: return "均衡（2 秒）";
                case RefreshProfile.Saver: return "省流（5 秒）";
                default: return "快速（1 秒 · 推荐）";
            }
        }

        /// <summary>下拉框用的档位列表。</summary>
        public static RefreshProfile[] Profiles()
        {
            return new RefreshProfile[]
            {
                RefreshProfile.Fast, RefreshProfile.Turbo, RefreshProfile.Balanced, RefreshProfile.Saver
            };
        }

        public static string ProfileHint(RefreshProfile profile)
        {
            switch (profile)
            {
                case RefreshProfile.Turbo:
                    return "报价 1 秒 / 概览 15 秒 —— 数字最跟手，接口压力最大";
                case RefreshProfile.Balanced:
                    return "报价 2 秒 / 概览 60 秒 —— 与旧版一致，适合网络不稳时";
                case RefreshProfile.Saver:
                    return "报价 5 秒 / 概览 120 秒 —— 尽量少发请求";
                default:
                    return "报价 1 秒 / 概览 20 秒 —— 默认档位，兼顾及时与稳定";
            }
        }

        // ── 节奏 ──

        /// <summary>个股报价的建议轮询间隔（秒）。不传档位时按默认档位。</summary>
        public static int QuoteIntervalSeconds(MarketSession session)
        {
            return QuoteIntervalSeconds(session, RefreshProfile.Fast);
        }

        public static int QuoteIntervalSeconds(MarketSession session, RefreshProfile profile)
        {
            switch (profile)
            {
                case RefreshProfile.Turbo:
                    switch (session)
                    {
                        case MarketSession.MorningTrade:
                        case MarketSession.AfternoonTrade: return 1;
                        case MarketSession.PreOpen: return 2;
                        case MarketSession.LunchBreak: return 5;
                        default: return 30;
                    }
                case RefreshProfile.Balanced:
                    switch (session)
                    {
                        case MarketSession.MorningTrade:
                        case MarketSession.AfternoonTrade: return 2;
                        case MarketSession.PreOpen: return 5;
                        case MarketSession.LunchBreak: return 15;
                        default: return 60;
                    }
                case RefreshProfile.Saver:
                    switch (session)
                    {
                        case MarketSession.MorningTrade:
                        case MarketSession.AfternoonTrade: return 5;
                        case MarketSession.PreOpen: return 15;
                        case MarketSession.LunchBreak: return 30;
                        default: return 120;
                    }
                default:
                    switch (session)
                    {
                        case MarketSession.MorningTrade:
                        case MarketSession.AfternoonTrade: return 1;
                        case MarketSession.PreOpen: return 3;
                        case MarketSession.LunchBreak: return 10;
                        default: return 30;
                    }
            }
        }

        /// <summary>
        /// 市场概览（成交额 / 涨跌家数 / 板块榜）的建议间隔（秒）。
        /// 比报价慢：它是聚合摘要，且打的是东财接口有频控；休市时数据完全不动，
        /// 退避到几分钟避免整夜空转。默认档位下概览从 60 秒提到 20 秒，
        /// 因为「大盘」是看盘时最需要及时的一块数据。
        /// </summary>
        public static int OverviewIntervalSeconds(MarketSession session)
        {
            return OverviewIntervalSeconds(session, RefreshProfile.Fast);
        }

        public static int OverviewIntervalSeconds(MarketSession session, RefreshProfile profile)
        {
            switch (profile)
            {
                case RefreshProfile.Turbo:
                    switch (session)
                    {
                        case MarketSession.MorningTrade:
                        case MarketSession.AfternoonTrade: return 15;
                        case MarketSession.PreOpen: return 20;
                        case MarketSession.LunchBreak: return 45;
                        default: return 180;
                    }
                case RefreshProfile.Balanced:
                    switch (session)
                    {
                        case MarketSession.MorningTrade:
                        case MarketSession.AfternoonTrade: return 60;
                        case MarketSession.PreOpen: return 60;
                        case MarketSession.LunchBreak: return 120;
                        default: return 300;
                    }
                case RefreshProfile.Saver:
                    switch (session)
                    {
                        case MarketSession.MorningTrade:
                        case MarketSession.AfternoonTrade: return 120;
                        case MarketSession.PreOpen: return 120;
                        case MarketSession.LunchBreak: return 300;
                        default: return 600;
                    }
                default:
                    switch (session)
                    {
                        case MarketSession.MorningTrade:
                        case MarketSession.AfternoonTrade: return 20;
                        case MarketSession.PreOpen: return 30;
                        case MarketSession.LunchBreak: return 60;
                        default: return 180;
                    }
            }
        }

        /// <summary>进入交易时段后的「探测」节奏（秒），用于尽快抓到开盘第一笔变化。</summary>
        public static int ProbeIntervalSeconds(RefreshProfile profile)
        {
            return profile == RefreshProfile.Saver || profile == RefreshProfile.Balanced ? 2 : 1;
        }

        public static string Name(MarketSession session)
        {
            switch (session)
            {
                case MarketSession.PreOpen: return "盘前";
                case MarketSession.MorningTrade: return "早盘";
                case MarketSession.LunchBreak: return "午休";
                case MarketSession.AfternoonTrade: return "午盘";
                default: return "休市";
            }
        }

        /// <summary>是否处于连续竞价（用于决定是否值得高频刷新页面）。</summary>
        public static bool IsTrading(MarketSession session)
        {
            return session == MarketSession.MorningTrade || session == MarketSession.AfternoonTrade;
        }
    }
}
