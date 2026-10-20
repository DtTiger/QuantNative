using System;
using System.Collections.Generic;

namespace QuantNative.Core
{
    /// <summary>
    /// 海外指数「伪板块」。
    ///
    /// 为什么需要它：场外 QDII / 指数基金（「南方纳斯达克100指数发起(QDII)I 021000」这类）
    /// 跟踪的是海外指数，而东财的 A 股板块目录里没有对应条目 —— 于是这类基金永远匹配不到
    /// 关联板块，盘中就只剩「未关联 / 等待净值」一片空白，这是「基金无法关联」的主因之一。
    ///
    /// 约定：
    ///   · 代码 = <see cref="Prefix"/> + 东财 push2 的 secid，例如 ix100.NDX / ix124.HSTECH。
    ///     不含冒号 —— 落盘格式是 "code:name"，冒号会被当成分隔符。
    ///   · 名称与 A 股板块同构，直接复用 FundBoardMatcher 的匹配打分（名称直命中 + 主题同义词）。
    ///   · 行情走 push2 ulist：secid 原样传（不需要 A 股板块的 90. 前缀），批量一次拿完。
    /// </summary>
    public static class IndexBoards
    {
        /// <summary>伪板块代码前缀。</summary>
        public const string Prefix = "ix";

        private sealed class Entry
        {
            public string SecId;
            public string Name;
            public string[] Aliases;
        }

        /// <summary>
        /// 覆盖国内基民最常买的海外指数。secid 已按东财 push2 实测校验
        /// （恒生科技在 124 市场，其余全球指数在 100 市场）。
        /// </summary>
        private static readonly Entry[] Table = new Entry[]
        {
            E("100.NDX", "纳斯达克100", "纳斯达克", "纳指", "NASDAQ", "NDX"),
            E("100.SPX", "标普500", "标普", "S&P500", "SPX"),
            E("100.DJIA", "道琼斯", "道指", "DJIA"),
            E("100.HSI", "恒生指数", "恒指", "HSI"),
            E("124.HSTECH", "恒生科技指数", "恒生科技", "HSTECH"),
            E("100.N225", "日经225", "日经", "N225"),
            E("100.GDAXI", "德国DAX30", "德国DAX", "DAX", "德国"),
            E("100.FCHI", "法国CAC40", "法国CAC", "CAC40"),
            E("100.FTSE", "英国富时100", "英国富时", "富时100")
        };

        private static Entry E(string secId, string name, params string[] aliases)
        {
            return new Entry { SecId = secId, Name = name, Aliases = aliases };
        }

        /// <summary>东财 secid → 伪板块代码（100.NDX → ix100.NDX）。</summary>
        public static string CodeFromSecId(string secId)
        {
            return string.IsNullOrEmpty(secId) ? "" : Prefix + secId;
        }

        /// <summary>是否是海外指数伪板块代码。</summary>
        public static bool IsIndexCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 6) return false;
            if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

            string secId = code.Substring(Prefix.Length);
            int dot = secId.IndexOf('.');
            if (dot <= 0 || dot == secId.Length - 1) return false;
            for (int i = 0; i < dot; i++) if (!char.IsDigit(secId[i])) return false;
            for (int i = dot + 1; i < secId.Length; i++)
            {
                char c = secId[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '.' || c == '_' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>伪板块代码 → 东财 secid（ix100.NDX → 100.NDX）。</summary>
        public static string ToSecId(string code)
        {
            return IsIndexCode(code) ? code.Substring(Prefix.Length) : "";
        }

        /// <summary>伪板块的默认中文名（行情返回空名时兜底）。</summary>
        public static string NameOf(string code)
        {
            string secId = ToSecId(code);
            for (int i = 0; i < Table.Length; i++)
                if (string.Equals(Table[i].SecId, secId, StringComparison.OrdinalIgnoreCase)) return Table[i].Name;
            return code;
        }

        /// <summary>该伪板块的别名（基金名里可能出现的写法）。</summary>
        public static string[] AliasesOf(string code)
        {
            string secId = ToSecId(code);
            for (int i = 0; i < Table.Length; i++)
                if (string.Equals(Table[i].SecId, secId, StringComparison.OrdinalIgnoreCase)) return Table[i].Aliases;
            return new string[0];
        }

        /// <summary>全部伪板块，合并进板块目录供匹配与搜索使用。</summary>
        public static List<BoardLink> Catalog()
        {
            List<BoardLink> result = new List<BoardLink>(Table.Length);
            for (int i = 0; i < Table.Length; i++)
                result.Add(new BoardLink { Code = CodeFromSecId(Table[i].SecId), Name = Table[i].Name });
            return result;
        }

        public static int Count { get { return Table.Length; } }
    }
}
