using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.Data;
using QuantNative.UI;

/// <summary>
/// 离线 UI 预览：不联网，把所有数据直接塞进缓存，然后渲染三张截图 ——
///   01-main.png      主界面（指数条 / 自选表 / 详情 / 大盘概览）
///   02-settings.png  面板·设置页（定时隐藏 / 字体 / 刷新档位 / 一键关联）
///   03-ticker.png    浮窗（三位小数 + 涨跌幅色块 + 右下角缩放提示）
/// 用来在没有人肉点界面的情况下确认 2.4.0 的六项改动真的画出来了。
/// 用法： UiPreview.exe &lt;输出目录&gt;
/// </summary>
class UiPreview
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object o, string n) { return (T)o.GetType().GetField(n, Flags).GetValue(o); }
    static void SetField(object o, string n, object value) { o.GetType().GetField(n, Flags).SetValue(o, value); }
    static void Pump(int ms) { DateTime end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { Application.DoEvents(); System.Threading.Thread.Sleep(5); } }

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);

        var host = new AppHost();
        host.Init();
        Theme.UseScheme(Theme.DefaultScheme);   // 默认配色 = 养基宝·浅色

        // ── 自选：一只场外基金 + 一只股票 + 一个指数 ──
        host.Watchlist.Clear();
        var fund = new WatchItem { Code = "of005844", Market = "OF", Name = "东方人工智能主题混合A" };
        var stock = new WatchItem { Code = "sh600519", Market = "CN", Name = "贵州茅台" };
        var etf = new WatchItem { Code = "sz159915", Market = "CN", Name = "创业板ETF" };
        host.Watchlist.Add(fund); host.Watchlist.Add(stock); host.Watchlist.Add(etf);
        host.SetFundBoards(fund.Code, new List<BoardLink> {
            new BoardLink{Code="BK0800",Name="人工智能"}, new BoardLink{Code="BK1041",Name="医疗器械"} });

        // ── 行情缓存（绕过网络） ──
        DateTime now = MarketClock.NowCst();
        // 场外基金给正式净值 + 上一个工作日的净值日期：
        // 盘中界面显示「上一净值」，午休/盘后显示正式净值，两种口径都写明了日期。
        DateTime navDay = now.Date.AddDays(-1);
        while (navDay.DayOfWeek == DayOfWeek.Saturday || navDay.DayOfWeek == DayOfWeek.Sunday) navDay = navDay.AddDays(-1);
        var quotes = Field<Dictionary<string, Quote>>(host.Hub, "_quotes");
        quotes[QuoteHub.Key("OF", fund.Code)] = new Quote {
            Code = fund.Code, Market = "OF", Name = fund.Name, Price = 3.2037, PriceKind = "净值",
            PriceTime = navDay.ToString("yyyy-MM-dd"), ChangePct = 1.238, Change = 0.039 };
        quotes[QuoteHub.Key("CN", "sh600519")] = new Quote {
            Code = "sh600519", Market = "CN", Name = "贵州茅台", Price = 1243.88, Change = 3.25, ChangePct = 0.262,
            Open = 1240.1, High = 1250.4, Low = 1230.7, Turnover = 4.32e9, Volume = 3480000, TurnoverRate = 0.28 };
        quotes[QuoteHub.Key("CN", "sz159915")] = new Quote {
            Code = "sz159915", Market = "CN", Name = "创业板ETF", Price = 2.134, Change = -0.012, ChangePct = -0.559,
            Open = 2.15, High = 2.16, Low = 2.12, Turnover = 8.6e8, Volume = 40200000, TurnoverRate = 1.42 };

        // ── 指数条：故意用乱序灌进去，验证 2.4.0 会按固定名次重排 ──
        SetField(host.Hub, "_indices", QuoteHub.NormalizeIndices(new List<IndexQuote> {
            new IndexQuote{Code="s_sz399006",Name="创业板指",Price=2134.56,Change=-12.34,ChangePct=-0.575},
            new IndexQuote{Code="s_sz399001",Name="深证成指",Price=10548.21,Change=45.67,ChangePct=0.435},
            new IndexQuote{Code="s_sh000001",Name="上证指数",Price=3218.45,Change=12.33,ChangePct=0.385},
            new IndexQuote{Code="s_sh000688",Name="科创50",Price=968.12,Change=6.78,ChangePct=0.706},
            new IndexQuote{Code="s_sh000905",Name="中证500",Price=5620.33,Change=-8.91,ChangePct=-0.158}
        }));
        SetField(host.Hub, "_session", MarketSession.MorningTrade);
        SetField(host.Hub, "<LastUpdate>k__BackingField", now);

        // ── 板块行情 ──
        var boards = Field<Dictionary<string, BoardQuote>>(host.Hub.Boards, "_quotes");
        boards["BK0800"] = new BoardQuote { Code = "BK0800", Name = "人工智能", ChangePct = 1.352, DataTime = now, ReceivedAt = now };
        boards["BK1041"] = new BoardQuote { Code = "BK1041", Name = "医疗器械", ChangePct = -0.427, DataTime = now, ReceivedAt = now };

        // ── 大盘概览 ──
        SetField(host.Hub, "<Overview>k__BackingField", new MarketOverview {
            Turnover = 1.2345e12, Up = 2871, Down = 1932, Flat = 168,
            Industry = new List<SectorItem> {
                new SectorItem{Code="BK0800",Name="人工智能",ChangePct=3.256,LeaderName="寒武纪",LeaderPct=8.42,LaggardName="",LaggardPct=null},
                new SectorItem{Code="BK0917",Name="半导体概念",ChangePct=2.741,LeaderName="中芯国际",LeaderPct=6.13},
                new SectorItem{Code="BK0486",Name="通信设备",ChangePct=2.115,LeaderName="中兴通讯",LeaderPct=4.87},
                new SectorItem{Code="BK1036",Name="汽车零部件",ChangePct=1.632,LeaderName="拓普集团",LeaderPct=5.21},
                new SectorItem{Code="BK0459",Name="光伏设备",ChangePct=-1.284,LeaderName="隆基绿能",LeaderPct=-2.31} },
            Concept = new List<SectorItem> {
                new SectorItem{Code="BK1158",Name="CPO概念",ChangePct=4.128,LeaderName="中际旭创",LeaderPct=9.02},
                new SectorItem{Code="BK0891",Name="存储芯片",ChangePct=3.017,LeaderName="兆易创新",LeaderPct=7.44},
                new SectorItem{Code="BK1128",Name="算力概念",ChangePct=2.536,LeaderName="浪潮信息",LeaderPct=6.02},
                new SectorItem{Code="BK0999",Name="PCB",ChangePct=1.874,LeaderName="沪电股份",LeaderPct=5.13},
                new SectorItem{Code="BK1188",Name="MLCC",ChangePct=0.936,LeaderName="三环集团",LeaderPct=3.08} }
        });

        // ── 1. 主界面 ──
        using (var main = new MainForm(host))
        {
            main.Show(); Pump(200);
            var detail = Field<DetailPanel>(main, "_detail");
            var chart = Field<ChartControl>(detail, "_chart");
            // 冻结详情页的自动加载：否则 DetailPanel 会在后台去拉板块分时/净值曲线
            // （离线环境里可能失败或覆盖掉这里塞的假数据），截图就不稳定了。
            Value<Timer>(detail, "_timer").Stop();
            SetField(detail, "_loading", true);
            var points = new List<MinutePoint>();
            for (int i = 0; i <= 120; i += 5)
                points.Add(new MinutePoint { Time = new DateTime(2026, 9, 29, 9, 30, 0).AddMinutes(i).ToString("HH:mm"), Price = Math.Sin(i / 19.0) * 0.8 + i / 100.0 });
            chart.ShowPercentTrend(points, "人工智能 · 当日涨跌幅 · 模拟示例");
            Pump(200);

            var grid = Field<DataGridView>(main, "_grid");
            var buttons = new List<Button>();
            foreach (string name in new[] { "_addButton", "_refreshButton", "_sourceButton", "_themeButton", "_tickerButton", "_panelButton" })
                buttons.Add(Field<Button>(main, name));
            foreach (var b in buttons)
                if (b.Width < Ui.ButtonSize(b).Width || b.Height < Ui.ButtonSize(b).Height) throw new Exception("Clipped button: " + b.Text);

            using (var bmp = new Bitmap(main.Width, main.Height))
            {
                main.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(Path.Combine(outDir, "01-main.png"));
            }
            Console.WriteLine("index strip order = " + IndexOrderText(host));
            Console.WriteLine("watchlist rows = " + grid.Rows.Count);
            // 场外基金那一行：现价 / 涨跌 / 涨跌幅（2.5.1 起按关联板块等权估算，带 * 号）
            Console.WriteLine("fund row = " + grid.Rows[0].Cells[2].Value + " | " + grid.Rows[0].Cells[3].Value
                + " | " + grid.Rows[0].Cells[4].Value + " | " + grid.Rows[0].Cells[11].Value);
            main.AllowClose = true; main.Close();
        }

        // ── 2. 面板·设置页 ──
        using (var panel = (PanelForm)Activator.CreateInstance(typeof(PanelForm), Flags, null, new object[] { host }, null))
        {
            typeof(PanelForm).GetMethod("ShowTab", Flags).Invoke(panel, new object[] { "settings" });
            panel.Show(); Pump(300);
            var settings = Field<SettingsPage>(panel, "_settingsPage");
            settings.SyncFromHost();
            Pump(200);
            using (var bmp = new Bitmap(panel.Width, panel.Height))
            {
                panel.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(Path.Combine(outDir, "02-settings.png"));
            }
            Console.WriteLine("settings scroll height = " + settings.AutoScrollMinSize.Height);
            panel.Close();
        }

        // ── 3. 浮窗（含三位小数与缩放提示） ──
        host.TickerWidth = 430;
        host.TickerHeight = 0;
        host.TickerRows = 3;
        host.TickerFontSize = 12;
        host.TickerOpacity = 92;
        using (var ticker = new TickerBarForm(host))
        {
            ticker.Show(); Pump(200);
            SetField(ticker, "_hovered", true);     // 悬停态才画右下角缩放提示
            typeof(TickerBarForm).GetMethod("Render", Flags).Invoke(ticker, null);
            var surface = Field<Bitmap>(ticker, "_surface");
            using (var bmp = new Bitmap(surface.Width, surface.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(surface, 0, 0);
                bmp.Save(Path.Combine(outDir, "03-ticker.png"));
            }
            Console.WriteLine("ticker size = " + surface.Width + "x" + surface.Height);
            // 逐行打印浮窗内容：基金那行应当是「估值 + 等权涨跌幅」合成的一行
            var rows = (System.Collections.IEnumerable)typeof(TickerBarForm)
                .GetMethod("BuildRows", Flags).Invoke(ticker, null);
            // Row 是私有嵌套类，但字段是 public —— 取字段要把 Public 也带上
            const BindingFlags RowFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var rowText = new System.Text.StringBuilder();
            foreach (object row in rows)
            {
                Type type = row.GetType();
                object name = type.GetField("Name", RowFlags).GetValue(row);
                object price = type.GetField("Price", RowFlags).GetValue(row);
                object pct = type.GetField("ChangePct", RowFlags).GetValue(row);
                if (rowText.Length > 0) rowText.Append(" / ");
                rowText.Append(name).Append(" ")
                    .Append(price == null ? "--" : Convert.ToDouble(price).ToString("F3"))
                    .Append(" ")
                    .Append(pct == null ? "--" : Symbols.FormatPct3(Convert.ToDouble(pct)));
            }
            Console.WriteLine("ticker rows = " + rowText);
            ticker.Hide();
        }
        Console.WriteLine("FDisplay = " + (Theme.FDisplay == null ? "null" : Theme.FDisplay.Size + "pt / h=" + Theme.FDisplay.Height)
            + " | FValueLarge h=" + Theme.FValueLarge.Height + " | FSmall h=" + Theme.FSmall.Height);
        Console.WriteLine("amount text = " + Symbols.FormatAmount(1.2345e12));
        var ov = host.Hub.Overview;
        Console.WriteLine("overview = " + (ov == null ? "null" : ("turnover=" + ov.Turnover + " industry=" + ov.Industry.Count)));

        // ── 4. 「大盘」面板放大（核对成交额 / 涨跌家数两行的列位置） ──
        using (var shell = new Form())
        {
            shell.ClientSize = new Size(460, 430);
            shell.StartPosition = FormStartPosition.Manual;
            shell.Location = new Point(-4000, -4000);
            var overview = new OverviewPanel(host);
            overview.Dock = DockStyle.Fill;
            shell.Controls.Add(overview);
            shell.Show(); Pump(200);
            using (var bmp = new Bitmap(shell.ClientSize.Width, shell.ClientSize.Height))
            {
                overview.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(Path.Combine(outDir, "04-overview.png"));
            }
            shell.Close(); overview.Dispose();
        }

        return 0;
    }

    /// <summary>把指数条上实际渲染出来的顺序打出来，用于核对「大盘位置」是否与预期一致。</summary>
    static string IndexOrderText(AppHost host)
    {
        var list = new List<string>();
        foreach (IndexQuote index in host.Hub.Indices) list.Add(index.Name + "(" + Symbols.FormatPct(index.ChangePct) + ")");
        return string.Join(" | ", list.ToArray());
    }
}
