using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.Data;
using QuantNative.UI;

class Regression
{
    static int failures;
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static object Field(object o, string n) { return o.GetType().GetField(n, Flags).GetValue(o); }
    static object Call(object o, string n, params object[] args) { return o.GetType().GetMethod(n, Flags).Invoke(o, args); }
    static void Check(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); } catch(Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); } }
    static void Require(bool b, string m) { if (!b) throw new Exception(m); }
    static void Pump(int ms) { var end = DateTime.UtcNow.AddMilliseconds(ms); while(DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); } }
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { failures++; Console.WriteLine("UI ERROR " + e.Exception); };
        Console.WriteLine("Assembly: " + typeof(AppHost).Assembly.Location);
        if((args.Length > 0 && args[0] == "ui") || Application.ExecutablePath.EndsWith("UiHarness.exe", StringComparison.OrdinalIgnoreCase)) {
            AppHost app = new AppHost(); app.Init(); app.MainWindow = new MainForm(app); app.Ticker = new TickerBarForm(app);
            app.Tray = new TrayIconHost(app); app.Context = new ApplicationContext(); app.Ticker.Show(); app.ShowMain(); app.Start();
            Application.Run(app.Context); return 0;
        }
        Check("Search preserves unrelated same-number fund", delegate {
            var merged = new List<StockBrief>(); var seen = new HashSet<string>(); var exchange = new HashSet<string>();
            var append = typeof(SearchService).GetMethod("Append", Flags);
            append.Invoke(null, new object[]{ merged,seen,exchange,new List<StockBrief>{new StockBrief{Code="sz000001",Category="GP-A"}},"sina","Sina" });
            append.Invoke(null, new object[]{ merged,seen,exchange,new List<StockBrief>{new StockBrief{Code="of000001",Category="OF"}},"eastmoney-fund","Fund" });
            Require(merged.Count==2, "fund 000001 was incorrectly dropped for stock 000001");
        });
        Check("ETF duplicate suppressed", delegate {
            var merged=new List<StockBrief>(); var seen=new HashSet<string>(); var exchange=new HashSet<string>(); var append=typeof(SearchService).GetMethod("Append",Flags);
            append.Invoke(null,new object[]{merged,seen,exchange,new List<StockBrief>{new StockBrief{Code="sh510300",Category="ETF"}},"sina","Sina"});
            append.Invoke(null,new object[]{merged,seen,exchange,new List<StockBrief>{new StockBrief{Code="of510300",Category="OF"}},"eastmoney-fund","Fund"});
            Require(merged.Count==1,"duplicate ETF");
        });
        Check("Holdings URL contains code parameter", delegate { var url=(string)typeof(EastmoneyFundSource).GetField("HoldingsUrl",Flags).GetRawConstantValue(); Require(url.EndsWith("&code="),"fund code is appended to rt instead of code"); });
        Check("Holdings exclude older reporting periods",delegate {
            var source=new EastmoneyFundSource();
            string row="<tr><td><a href='//quote.eastmoney.com/unify/r/0.300750'>Stock</a></td><td>8.00%</td></tr>";
            var result=(IList)Call(source,"ParseHoldings","<table>"+row+"</table><table>"+row+"</table>");
            Require(result.Count==1,"multiple quarters counted together");
        });
        Check("Unpublished NAV refreshes within five minutes",delegate {
            var source=new EastmoneyFundSource();var meta=Call(source,"GetMeta","005844");
            var historyField=meta.GetType().GetField("History",Flags);historyField.SetValue(meta,Activator.CreateInstance(historyField.FieldType));
            meta.GetType().GetField("HistoryAt",Flags).SetValue(meta,MarketClock.NowCst().AddMinutes(-6));
            meta.GetType().GetField("NavDate",Flags).SetValue(meta,MarketClock.NowCst().AddDays(-1).ToString("yyyy-MM-dd"));
            Require((bool)Call(source,"NeedsHistory",meta),"stale NAV remains cached for twelve hours");
        });
        Check("Official fund return survives dividend NAV drop",delegate {
            var source=new EastmoneyFundSource();var meta=Call(source,"GetMeta","005844");
            meta.GetType().GetField("Nav",Flags).SetValue(meta,0.9);meta.GetType().GetField("PrevNav",Flags).SetValue(meta,1.0);
            meta.GetType().GetField("NavPct",Flags).SetValue(meta,0.0);
            var q=(Quote)Call(source,"BuildQuote",meta,new Dictionary<string,Quote>(),"2026-09-28",true);
            Require(q.ChangePct==0,"dividend appears as -10% loss instead of official 0%");
        });
        Check("Index failure preserves successful stock quote", delegate {
            var hub=new QuoteHub(); var fake=new Fake{FailIndices=true}; hub.Register(fake); hub.SetWatchlist(new List<WatchItem>{new WatchItem{Code="sh600519"}});
            Call(hub,"PollQuotesAsync"); Pump(100); Require(hub.GetQuote("sh600519","CN")!=null,"successful quote lost due to index failure");
            Require(!string.IsNullOrEmpty(hub.LastError),"partial failure not reported"); hub.Stop();
        });
        Check("Fund fallback routes by instrument", delegate {
            var hub=new QuoteHub(); var fake=new Fake(); var fund=new EastmoneyFundSource(); hub.Register(fake); hub.Register(fund);
            Require(ReferenceEquals(Call(hub,"ResolveSource",new WatchItem{Code="of005844",Market="OF"}),fund),"legacy fund sent to stock provider");
            hub.SetActiveSource(fund.Name);
            Require(ReferenceEquals(Call(hub,"ResolveSource",new WatchItem{Code="sh600519",Market="CN"}),fake),"stock sent to fund provider");
        });
        Check("Old detail response cannot repaint new selection", delegate {
            var host=new AppHost();host.Init();var fake=new Fake{DelayMinute=true};host.Hub.Register(fake);host.Hub.SetActiveSource(fake.Name);
            using(var panel=new DetailPanel(host)) {
                panel.SelectItem(new WatchItem{Code="A",Name="A"}); panel.SelectItem(new WatchItem{Code="B",Name="B"});
                fake.Pending.SetResult(new List<MinutePoint>{new MinutePoint{Price=111,Open=111}});Pump(100);
                var chart=Field(panel,"_chart");var points=(List<MinutePoint>)Field(chart,"_minute");
                Require(points.Count==1 && points[0].Price==222,"A data shown under B label or B load suppressed");
                panel.SelectItem(null);Pump(20);
            }
        });
        Check("Window construction, six themes, ticker preferences", delegate {
            var host=new AppHost();host.Init();
            using(var main=new MainForm(host)) using(var ticker=new TickerBarForm(host)) using(var settings=new SettingsPage(host)) using(var watch=new WatchlistPage(host)) using(var add=new AddStockForm(host)) {
                host.MainWindow=main;host.Ticker=ticker;
                main.Show();ticker.Show();Pump(80);
                foreach(var scheme in Theme.Schemes) host.ApplyTheme(scheme.Key);
                host.TickerRows=1;host.TickerRows=12;host.TickerFontSize=18;host.TickerOpacity=10;host.ResetTickerPrefs();Pump(80);
                Require(main.ClientSize.Width>=980 && ticker.Visible,"window startup/layout failed");
                main.AllowClose=true;main.Close();ticker.ForceClose();
            }
        });
        Check("Watchlist source persists across reload",delegate {
            var store=new Store();store.SaveWatchlist(new List<WatchItem>{new WatchItem{Code="of005844",Market="OF",Name="Test",Source="eastmoney-fund"}});
            var rows=new Store().LoadWatchlist();Require(rows.Count==1 && rows[0].Source=="eastmoney-fund","source lost");
        });
        Check("Saved watchlist appears on initial main window",delegate {
            var host=new AppHost();host.Init();host.Watchlist.Clear();host.Watchlist.Add(new WatchItem{Code="A",Name="Saved",Source="test"});host.Hub.Register(new Fake());
            using(var main=new MainForm(host)) {
                var grid=(DataGridView)Field(main,"_grid");Require(grid.Rows.Count==1,"startup grid empty until watchlist edited");
                Require(Field(Field(main,"_detail"),"_item")!=null,"initial detail selection missing");
            }
        });
        Check("Empty fund depth can be rendered",delegate {
            var host=new AppHost();host.Init();
            using(var panel=new DetailPanel(host)) using(var bitmap=new System.Drawing.Bitmap(200,400)) using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) {
                panel.GetType().GetField("_item",Flags).SetValue(panel,new WatchItem{Code="of005844",Market="OF"});
                panel.GetType().GetField("_depthData",Flags).SetValue(panel,new Depth{Code="of005844"});
                ((Control)Field(panel,"_depth")).Size=new System.Drawing.Size(190,400);
                Call(panel,"OnDepthPaint",panel,new PaintEventArgs(graphics,new System.Drawing.Rectangle(0,0,200,400)));
            }
        });
        Console.WriteLine("Failures: " + failures);return failures==0?0:1;
    }
    class Fake:IQuoteSource {
        public bool FailIndices,DelayMinute;public TaskCompletionSource<List<MinutePoint>> Pending=new TaskCompletionSource<List<MinutePoint>>();
        public string Name {get{return "test";}}public string DisplayName{get{return "test";}}
        public Task<List<Quote>> FetchRealtimeAsync(IList<string> codes,string market){return Task.FromResult(new List<Quote>{new Quote{Code=codes[0],Market=market,Price=42}});}
        public Task<List<IndexQuote>> FetchIndicesAsync(){if(FailIndices){var t=new TaskCompletionSource<List<IndexQuote>>();t.SetException(new Exception("simulated index failure"));return t.Task;}return Task.FromResult(new List<IndexQuote>());}
        public Task<List<StockBrief>> SearchAsync(string k,string m){return Task.FromResult(new List<StockBrief>());}
        public Task<Depth> FetchDepthAsync(string c,string m){return Task.FromResult(new Depth{Code=c});}
        public Task<List<MinutePoint>> FetchMinuteAsync(string c,string m){return DelayMinute && c=="A"?Pending.Task:Task.FromResult(new List<MinutePoint>{new MinutePoint{Price=222,Open=222}});}
        public Task<List<KLineBar>> FetchKLineAsync(string c,string m,ChartPeriod p,int n){return Task.FromResult(new List<KLineBar>());}
        public Task<bool> HealthCheckAsync(){return Task.FromResult(true);}
    }
}
