using System;
using System.Collections.Generic;

namespace QuantNative.Core
{
    /// <summary>
    /// 基金 → 板块的自动匹配。
    ///
    /// 三路信号，按可靠度排序（对齐主流基金 App 的做法：先看名称里的主题词，
    /// 再看跟踪指数，最后按前十大重仓股所属行业反推）：
    ///   1. 名称直命中：基金名里直接含板块名（「东方人工智能主题混合A」→ 人工智能）。
    ///   2. 主题同义词：名称里的写法与板块名不同（「医疗」→ 医疗器械 / 医疗服务）。
    ///   3. 重仓行业：由调用方把重仓股所属行业作为候选传进来，这里按行业名精确落板。
    ///
    /// 只接受「名称明确命中」的结果 —— 宽泛的行业配置不会被当成关联，
    /// 宁可少关联也不冒充精确映射。
    /// </summary>
    public static class FundBoardMatcher
    {
        /// <summary>板块名前后的通用后缀，比较时先剥掉（「人工智能概念」→「人工智能」）。</summary>
        private static readonly string[] Suffixes = new string[] { "概念", "行业", "板块", "Ⅱ", "II", "指数" };

        /// <summary>基金名里的固定后缀词，取主题干时从这里截断（顺序敏感，长词在前）。</summary>
        private static readonly string[] NameMarkers = new string[]
        {
            "交易型", "开放式", "发起式", "主题", "指数", "混合", "股票", "基金", "联接",
            "增强", "债券", "定开", "持有", "QDII", "FOF", "ETF", "LOF", "型"
        };

        /// <summary>
        /// 主题同义词表：「基金名里出现的词」→「可能的板块名」。
        /// 只写常见且指向明确的主题，避免把「医药」这类大词硬塞给某一个细分板块。
        /// 括号里是关键词，值是候选板块名（按优先级）。
        /// </summary>
        private static readonly KeyValuePair<string, string[]>[] ThemeAliases = new KeyValuePair<string, string[]>[]
        {
            Alias("人工智能", "人工智能"),
            Alias("智能制造", "人工智能", "机器人概念"),
            Alias("云计算", "云计算"),
            Alias("大数据", "大数据"),
            Alias("软件", "软件开发", "计算机设备"),
            Alias("信息安全", "软件开发"),
            Alias("通信", "通信设备"),
            Alias("5G", "通信设备"),
            Alias("芯片", "半导体"),
            Alias("集成电路", "半导体"),
            Alias("半导体", "半导体"),
            Alias("消费电子", "消费电子"),
            Alias("电子", "消费电子", "半导体"),
            Alias("算力", "算力概念", "人工智能"),
            Alias("光模块", "CPO概念", "通信设备"),
            Alias("CPO", "CPO概念", "通信设备"),
            Alias("PCB", "PCB"),
            Alias("MLCC", "MLCC", "电子元件"),
            Alias("存储", "存储芯片"),
            Alias("新能源", "电池", "光伏设备"),
            Alias("光伏", "光伏设备"),
            Alias("风电", "风能", "风电设备"),
            Alias("储能", "储能"),
            Alias("锂电", "电池"),
            Alias("固态电池", "固态电池", "电池"),
            Alias("氢能", "氢能源"),
            Alias("新能源车", "汽车整车", "汽车零部件"),
            Alias("汽车", "汽车整车", "汽车零部件"),
            Alias("智能汽车", "汽车零部件"),
            Alias("机器人", "机器人概念", "通用设备"),
            Alias("军工", "航天航空", "船舶制造"),
            Alias("国防", "航天航空"),
            Alias("医疗", "医疗器械", "医疗服务"),
            Alias("医药", "化学制药", "中药", "生物制品"),
            Alias("生物医药", "生物制品", "化学制药"),
            Alias("创新药", "化学制药", "生物制品"),
            Alias("疫苗", "生物制品"),
            Alias("中药", "中药"),
            Alias("白酒", "白酒"),
            Alias("食品饮料", "食品饮料"),
            Alias("消费", "食品饮料", "家用电器"),
            Alias("家电", "家用电器"),
            Alias("银行", "银行"),
            Alias("证券", "证券"),
            Alias("保险", "保险"),
            Alias("非银", "证券", "保险"),
            Alias("地产", "房地产开发"),
            Alias("房地产", "房地产开发"),
            Alias("基建", "工程建设", "建筑材料"),
            Alias("建筑", "工程建设", "建筑材料"),
            Alias("机械", "通用设备", "专用设备"),
            Alias("电力", "电力行业"),
            Alias("有色", "小金属", "贵金属"),
            Alias("稀土", "小金属"),
            Alias("黄金", "贵金属"),
            Alias("煤炭", "煤炭行业"),
            Alias("化工", "化学制品", "化学原料"),
            Alias("农业", "农牧饲渔"),
            Alias("养殖", "农牧饲渔"),
            Alias("环保", "环保行业"),
            Alias("传媒", "文化传媒"),
            Alias("游戏", "游戏"),
            Alias("旅游", "旅游酒店"),
            Alias("物流", "物流行业"),
            Alias("港口", "港口航运"),
            Alias("航空", "航天航空", "航空机场"),
            Alias("红利", "红利破净股"),
            Alias("沪深300", "沪深300"),
            Alias("中证500", "中证500"),
            Alias("中证1000", "中证1000"),
            Alias("科创50", "科创板"),
            Alias("创业板", "创业板综"),
            Alias("北证50", "北交所概念"),
            // ── 海外指数（对应 IndexBoards 的伪板块）──
            // QDII / 海外指数基金跟踪的是海外指数，A 股板块目录里没有这些条目，
            // 不在这里登记就永远匹配不到任何板块。
            Alias("纳斯达克", "纳斯达克100"),
            Alias("纳指", "纳斯达克100"),
            Alias("NASDAQ", "纳斯达克100"),
            Alias("标普", "标普500"),
            Alias("S&P", "标普500"),
            Alias("道琼斯", "道琼斯"),
            Alias("道指", "道琼斯"),
            Alias("恒生科技", "恒生科技指数"),
            Alias("恒生", "恒生指数"),
            Alias("恒指", "恒生指数"),
            Alias("日经", "日经225"),
            Alias("德国", "德国DAX30"),
            Alias("DAX", "德国DAX30"),
            Alias("法国", "法国CAC40"),
            Alias("CAC40", "法国CAC40"),
            Alias("富时", "英国富时100")
        };

        private static KeyValuePair<string, string[]> Alias(string keyword, params string[] boards)
        {
            return new KeyValuePair<string, string[]>(keyword, boards);
        }

        // ── 主入口 ──

        /// <summary>
        /// 按板块目录做名称匹配。返回最多 3 个板块，按命中程度排序。
        ///
        /// 海外指数伪板块（<see cref="IndexBoards"/>）在这里无条件并入候选集：
        /// 它们不在东财的 A 股板块目录里，但 QDII / 海外指数基金只能靠它们关联，
        /// 否则「南方纳斯达克100指数发起」这类基金会一直显示「未关联」。
        /// </summary>
        public static List<BoardLink> Match(string fundName, IEnumerable<BoardLink> catalog)
        {
            List<BoardLink> ordered = new List<BoardLink>();
            if (catalog != null) foreach (BoardLink board in catalog) ordered.Add(board);
            foreach (BoardLink index in IndexBoards.Catalog()) ordered.Add(index);

            List<Tuple<int, BoardLink>> scored = Score(fundName, ordered);
            List<BoardLink> result = new List<BoardLink>();
            HashSet<string> roots = new HashSet<string>(StringComparer.Ordinal);
            foreach (Tuple<int, BoardLink> entry in scored)
            {
                string root = TrimSuffix(Normalize(entry.Item2.Name));
                if (roots.Add(root)) result.Add(entry.Item2);
                if (result.Count == 3) break;
            }
            return result;
        }

        /// <summary>前两路信号（名称直命中 + 主题同义词）的打分。</summary>
        public static List<Tuple<int, BoardLink>> Score(string fundName, IList<BoardLink> catalog)
        {
            List<Tuple<int, BoardLink>> scored = new List<Tuple<int, BoardLink>>();
            string name = Normalize(fundName);
            if (name.Length == 0 || catalog == null) return scored;

            List<string> themes = InferThemes(fundName);

            for (int i = 0; i < catalog.Count; i++)
            {
                BoardLink board = catalog[i];
                if (board == null || !BoardLink.ValidCode(board.Code)) continue;

                string full = Normalize(board.Name);
                string root = TrimSuffix(full);
                int score = 0;

                // 1) 名称直命中：板块全称在基金名里，越长越具体，得分越高
                if (full.Length >= 2 && name.IndexOf(full, StringComparison.Ordinal) >= 0) score = 400 + full.Length;
                else if (root.Length >= 2 && name.IndexOf(root, StringComparison.Ordinal) >= 0) score = 320 + root.Length;
                else
                {
                    // 2) 主题同义词：基金名里的主题词映射到板块名
                    for (int t = 0; t < themes.Count; t++)
                    {
                        if (!BoardNameMatches(full, root, themes[t])) continue;
                        // 同义词命中比直命中弱一档，靠前的候选再高一点
                        int bonus = Math.Max(0, 120 - t * 10);
                        if (score < 200 + bonus) score = 200 + bonus;
                    }
                }

                if (score > 0) scored.Add(Tuple.Create(score, board));
            }
            Sort(scored);
            return scored;
        }

        /// <summary>
        /// 第三路信号：重仓股行业名 → 板块。行业名与板块名基本同名，
        /// 所以这里要求较严（先全等，再包含），且只取前 2 个不同板块。
        /// </summary>
        public static List<BoardLink> MatchIndustries(IList<string> industries, IEnumerable<BoardLink> catalog)
        {
            List<BoardLink> result = new List<BoardLink>();
            if (industries == null || catalog == null) return result;

            List<BoardLink> ordered = new List<BoardLink>();
            foreach (BoardLink board in catalog) ordered.Add(board);

            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            List<Tuple<int, BoardLink>> scored = new List<Tuple<int, BoardLink>>();

            for (int i = 0; i < industries.Count; i++)
            {
                string industry = Normalize(industries[i]);
                if (industry.Length == 0) continue;
                string industryRoot = TrimSuffix(industry);

                for (int j = 0; j < ordered.Count; j++)
                {
                    BoardLink board = ordered[j];
                    if (board == null || !BoardLink.ValidCode(board.Code)) continue;
                    string full = Normalize(board.Name);
                    string root = TrimSuffix(full);
                    if (full != industry && root != industryRoot && full != industryRoot && root != industry) continue;
                    // 越靠前的重仓行业权重越高
                    scored.Add(Tuple.Create(1000 - i * 20, board));
                }
            }

            Sort(scored);
            foreach (Tuple<int, BoardLink> entry in scored)
            {
                if (!used.Add(entry.Item2.Code)) continue;
                result.Add(entry.Item2);
                if (result.Count == 2) break;
            }
            return result;
        }

        /// <summary>
        /// 从基金名推断主题词（同义词表的键）。返回值即候选板块名列表，
        /// 顺序 = 主题表顺序，供 <see cref="Score"/> 打分使用。
        /// </summary>
        public static List<string> InferThemes(string fundName)
        {
            List<string> themes = new List<string>();
            string name = Normalize(fundName);
            if (name.Length == 0) return themes;

            for (int i = 0; i < ThemeAliases.Length; i++)
            {
                string keyword = ThemeAliases[i].Key;
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string[] boards = ThemeAliases[i].Value;
                for (int b = 0; b < boards.Length; b++)
                    if (!themes.Contains(boards[b])) themes.Add(boards[b]);
            }
            return themes;
        }

        /// <summary>把候选板块名收敛成搜索关键词（避开目录里没收录的情况）。</summary>
        public static List<string> SearchTerms(string fundName)
        {
            string stem = Normalize(fundName);
            foreach (string marker in NameMarkers)
            {
                int at = stem.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 2) stem = stem.Substring(0, at);
            }
            List<string> terms = new List<string>();
            for (int length = Math.Min(6, stem.Length); length >= 2 && terms.Count < 5; length--)
            {
                string term = stem.Substring(stem.Length - length);
                if (term.Length >= 2 && !terms.Contains(term)) terms.Add(term);
            }
            // 同义词表推断出来的板块名也一并作为搜索词，覆盖面更广
            foreach (string theme in InferThemes(fundName))
                if (terms.Count < 8 && !terms.Contains(theme)) terms.Add(theme);
            return terms;
        }

        public static string RootName(string boardName) { return TrimSuffix(Normalize(boardName)); }

        // ── 工具 ──

        private static bool BoardNameMatches(string full, string root, string candidate)
        {
            string target = Normalize(candidate);
            if (target.Length == 0) return false;
            return full == target || root == target
                || full.IndexOf(target, StringComparison.Ordinal) >= 0
                || target.IndexOf(full, StringComparison.Ordinal) >= 0;
        }

        private static void Sort(List<Tuple<int, BoardLink>> scored)
        {
            scored.Sort(delegate(Tuple<int, BoardLink> a, Tuple<int, BoardLink> b)
            {
                int c = b.Item1.CompareTo(a.Item1);
                return c != 0 ? c : string.CompareOrdinal(a.Item2.Code, b.Item2.Code);
            });
        }

        private static string Normalize(string text)
        {
            return (text ?? "").Replace(" ", "").Replace("·", "").Replace("（", "").Replace("）", "")
                .Replace("(", "").Replace(")", "").Trim();
        }

        private static string TrimSuffix(string text)
        {
            foreach (string suffix in Suffixes)
                if (text.EndsWith(suffix, StringComparison.Ordinal) && text.Length > suffix.Length)
                    return text.Substring(0, text.Length - suffix.Length);
            return text;
        }
    }
}
