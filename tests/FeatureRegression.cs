using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.Data;
using QuantNative.UI;

class FeatureRegression
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static int failed;
    static void Check(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); } }
    static void Expect(bool okay, string message) { if (!okay) throw new Exception(message); }
    static T Value<T>(object obj, string name) { return (T)obj.GetType().GetField(name, Private).GetValue(obj); }
    static void SetField(object obj, string name, object value) { obj.GetType().GetField(name, Private).SetValue(obj, value); }
    static void Pump(int ms) { DateTime end = DateTime.UtcNow.AddMilliseconds(ms); while(DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); } }
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e){ failed++; Console.WriteLine("UI ERROR " + e.Exception); };
        Check("selection contrast for all themes", delegate {
            foreach (Palette palette in Theme.Schemes)
            {
                Theme.UseScheme(palette.Key);
                using (var grid = new DataGridView())
                {
                    GridStyle.Apply(grid);grid.Columns.Add("name","name");grid.Rows.Add("基金");grid.CurrentCell=grid[0,0];
                    Color bg = grid[0,0].InheritedStyle.SelectionBackColor;
                    Color fg = grid[0,0].InheritedStyle.SelectionForeColor;
                    Expect(bg.A==255,"selection background transparent: " + palette.Key);
                    Expect(Contrast(bg,fg)>4.5,"selection text unreadable: "+palette.Key);
                }
            }
        });
        Check("two linked boards persist in order", delegate {
            var app = new AppHost();app.Init();app.Watchlist.Clear();
            app.Watchlist.Add(new WatchItem{Code="of005844",Market="OF",Name="测试基金"});
            app.SetFundBoards("of005844",new List<BoardLink>{new BoardLink{Code="BK0800",Name="人工智能"},new BoardLink{Code="BK0420",Name="医疗器械"}});
            var again=new AppHost();again.Init();var links=again.GetFundBoards("of005844");
            Expect(links.Count==2 && links[0].Code=="BK0800" && links[1].Name=="医疗器械","links lost or reordered");
            Expect(again.GetSetting("fund_boards_mode.of005844","")=="manual","manual correction can be overwritten by auto match");
        });
        Check("fund switches only after actual NAV is published", delegate {
            var quote=new Quote { Code="of005844",Market="OF",Price=3.25,PriceKind="净值",PriceTime="2026-09-28",ChangePct=1.2 };
            Expect(!FundPresentation.UseNav(quote,new DateTime(2026,9,29,10,0,0)),"old NAV displayed during trading");
            Expect(!FundPresentation.UseNav(quote,new DateTime(2026,9,29,20,0,0)),"old NAV displayed after market close");
            quote.PriceTime="2026-09-29";
            Expect(!FundPresentation.UseNav(quote,new DateTime(2026,9,29,14,0,0)),"NAV displayed while market open");
            Expect(FundPresentation.UseNav(quote,new DateTime(2026,9,29,20,0,0)),"published NAV hidden");
            Expect(FundPresentation.UseNav(quote,new DateTime(2026,9,30,8,0,0)),"last published NAV hidden overnight");
        });
        Check("stale board data has a visible warning", delegate {
            var now=new DateTime(2026,9,29,10,0,0);var link=new BoardLink{Code="BK0800",Name="人工智能"};
            var old=new BoardQuote{Code="BK0800",Name="人工智能",ChangePct=2,DataTime=now.AddDays(-1),ReceivedAt=now};
            Expect(FundPresentation.BoardText(link,old,now).Contains("待更新"),"prior day shown as current");
            old.DataTime=now;Expect(FundPresentation.BoardText(link,old,now).Contains("+2.00%"),"current board omitted");
        });
        Check("sector search keeps only BK instruments", delegate {
            var parsed=BoardClient.ParseQuote("{\"data\":{\"f58\":\"人工智能\",\"f170\":-2.46,\"f86\":1790581170}}","BK0800",new DateTime(2026,9,28,15,30,0));
            Expect(parsed.Code=="BK0800" && parsed.ChangePct==-2.46 && parsed.DataTime.Date==new DateTime(2026,9,28),"sector quote parsed incorrectly");
            Expect(!BoardLink.ValidCode("931071"),"index admitted as board");
        });
        Check("offline board catalog and name matching", delegate {
            var catalog=BoardCatalogSnapshot.Load();
            Expect(catalog.Count>=500,"board catalog incomplete");
            var matched=FundBoardMatcher.Match("东方人工智能主题混合A",catalog);
            Expect(matched.Count>0 && matched[0].Code=="BK0800","name-based auto association failed");
            Expect(FundBoardMatcher.Match("东方稳健债券A",catalog).Count==0,"unrelated fund auto-associated");
            Expect(FundBoardMatcher.SearchTerms("东方人工智能主题混合A").Contains("人工智能"),"search term missing theme");
        });
        Check("batch board quote keeps code percentage and date", delegate {
            var now=new DateTime(2026,9,29,10,0,0);
            var parsed=BoardClient.ParseBatchQuotes("{\"data\":{\"diff\":[{\"f12\":\"BK0800\",\"f14\":\"人工智能\",\"f3\":1.23,\"f124\":1790632800},{\"f12\":\"600519\",\"f3\":2,\"f124\":1790632800}]}}",now);
            Expect(parsed.Count==1 && parsed[0].Code=="BK0800" && Math.Abs(parsed[0].ChangePct-1.23)<0.001,"batch quote fields shifted");
            Expect(parsed[0].DataTime.Date==now.Date,"board batch date wrong");
        });
        Check("third stock quote source normalizes exchange and volume", delegate {
            var quotes=EastmoneyStockQuoteClient.Parse("{\"data\":{\"diff\":[{\"f12\":\"600519\",\"f13\":1,\"f14\":\"贵州茅台\",\"f2\":1234.5,\"f3\":1.2,\"f4\":14.6,\"f5\":100,\"f6\":1234500,\"f8\":0.1,\"f15\":1240,\"f16\":1220,\"f17\":1230,\"f124\":1790632800}]}}");
            Expect(quotes.Count==1 && quotes[0].Code=="sh600519" && quotes[0].Volume==10000 && quotes[0].Turnover==1234500,"third source mapped fields incorrectly");
        });
        Check("ticker width and text-only background", delegate {
            var app=new AppHost();app.Init();app.TickerWidth=500;app.TickerOpacity=0;
            using(var ticker=new TickerBarForm(app)) {
                ticker.Relayout();Expect(ticker.Width==500,"ticker width setting ignored");
                using(var bmp=new Bitmap(500,100))using(var graphics=Graphics.FromImage(bmp)) {
                    graphics.Clear(Color.Transparent);
                    typeof(TickerBarForm).GetMethod("DrawPlate",Private).Invoke(ticker,new object[]{graphics,500,100});
                    Expect(bmp.GetPixel(0,0).A==0 && bmp.GetPixel(250,50).A==0,"text-only mode draws a plate");
                }
            }
        });
        Check("sector trend uses its latest trading date", delegate {
            string payload="{\"data\":{\"name\":\"人工智能\",\"preClose\":100,\"trends\":[\"2026-09-28 09:30,100,101\",\"2026-09-29 09:30,100,99\"]}}";
            var trend=BoardClient.ParseTrend(payload);
            Expect(trend.Date==new DateTime(2026,9,29) && trend.Points.Count==1 && Math.Abs(trend.Points[0].Price+1)<0.001,"stale trend mixed with today");
        });
        Check("main buttons fit their measured text", delegate {
            var app=new AppHost();app.Init();app.Watchlist.Clear();
            using(var main=new MainForm(app)) {
                foreach (string field in new[]{"_addButton","_sourceButton","_themeButton","_tickerButton","_panelButton"}) {
                    Button b=Value<Button>(main,field);Size size=Ui.ButtonSize(b);Expect(b.Width>=size.Width && b.Height>=size.Height,field+" clips text");
                    Expect(b.Left>=0 && b.Right<=main.ClientSize.Width,field+" outside form");
                }
            }
        });
        Check("fund period buttons stay below title", delegate {
            var app=new AppHost();app.Init();app.Watchlist.Clear();app.Watchlist.Add(new WatchItem{Code="of005844",Market="OF",Name="测试基金"});
            using(var main=new MainForm(app)) {
                var detail=Value<DetailPanel>(main,"_detail");
                var title=Value<Label>(detail,"_title");
                var buttons=Value<List<Button>>(detail,"_periodButtons");
                foreach(var button in buttons) if(button.Text=="板块分时" || button.Text=="日K" || button.Text=="周K" || button.Text=="月K")
                    Expect(button.Top>=title.Bottom,button.Text+" overlaps title");
                Expect(Value<ChartControl>(detail,"_chart").Top>=Value<ComboBox>(detail,"_boardPicker").Bottom,"chart overlaps selector");
                var setPeriod=typeof(DetailPanel).GetMethod("SetPeriod",Private);
                setPeriod.Invoke(detail,new object[]{ChartPeriod.Daily});
                Expect(Value<ChartPeriod>(detail,"_period")==ChartPeriod.Minute,"daytime fund left board minute trend");
            }
        });
        Check("panel buttons fit their measured text", delegate {
            var app=new AppHost();app.Init();
            using(var panel=(PanelForm)Activator.CreateInstance(typeof(PanelForm),Private,null,new object[]{app},null)) {
                foreach(string field in new[]{"_tabWatch","_tabSettings","_collapse","_checkUpdate","_openMain","_quit"}) {
                    Button button=Value<Button>(panel,field);
                    Size size=Ui.ButtonSize(button);
                    Expect(button.Width>=size.Width && button.Height>=size.Height,field+" clips text");
                    Expect(button.Left>=0 && button.Right<=button.Parent.ClientSize.Width,field+" outside panel");
                }
                var settings=Value<SettingsPage>(panel,"_settingsPage");
                var width=Value<TrackBar>(settings,"_width");var opacity=Value<TrackBar>(settings,"_opacity");
                Expect(width.Minimum<=240 && width.Maximum>=720,"ticker width range unavailable");
                Expect(opacity.Minimum==0,"transparent background unavailable");
                Expect(Value<Label>(settings,"_lOpacity").Right<=opacity.Left,"opacity label overlaps slider");
            }
        });
        Check("association dialog renders without clipping", delegate {
            var app=new AppHost();app.Init();var fund=new WatchItem{Code="of005844",Market="OF",Name="东方人工智能主题混合A"};
            using(var dialog=new FundBoardsForm(app,fund)) {
                dialog.Show();Pump(50);
                using(var bitmap=new Bitmap(dialog.Width,dialog.Height)) { dialog.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));
                    if(args.Length>0)bitmap.Save(args[0]); }
                Expect(dialog.Controls.Count>0 && dialog.ClientSize.Width>=620,"dialog size insufficient");
                dialog.Close();
            }
        });
        // ── 2.4.0：定时隐藏 / 三位小数 / 大盘顺序 / 刷新档位 / 字体 / 自由缩放 ──

        Check("ticker schedule windows cover midnight", delegate {
            Expect(TickerSchedule.Normalize("9:5","")=="09:05","time normalization failed");
            Expect(TickerSchedule.Normalize("bogus",TickerSchedule.DefaultTo)==TickerSchedule.DefaultTo,"bad input must fall back to default");
            Expect(TickerSchedule.IsWithin("09:15","23:30",new DateTime(2026,9,29,10,0,0)),"daytime window should show");
            Expect(!TickerSchedule.IsWithin("09:15","23:30",new DateTime(2026,9,29,23,45,0)),"past end of window should hide");
            Expect(TickerSchedule.IsWithin("22:00","02:00",new DateTime(2026,9,29,23,30,0)),"cross-midnight window (before midnight) should show");
            Expect(TickerSchedule.IsWithin("22:00","02:00",new DateTime(2026,9,29,1,0,0)),"cross-midnight window (after midnight) should show");
            Expect(!TickerSchedule.IsWithin("22:00","02:00",new DateTime(2026,9,29,12,0,0)),"cross-midnight window midday should hide");
            Expect(TickerSchedule.IsWithin("08:00","08:00",new DateTime(2026,9,29,3,0,0)),"equal endpoints must mean all day");
            Expect(TickerSchedule.SecondsUntilChange("09:15","23:30",new DateTime(2026,9,29,23,0,0))==1800,"seconds until hide wrong");
            Expect(TickerSchedule.Describe("22:00","02:00").Contains("次日"),"cross-midnight description missing");
            Expect(!TickerSchedule.Describe("09:15","23:30").Contains("次日"),"normal window mislabelled as cross-midnight");
        });

        Check("ticker numbers use three decimals", delegate {
            Expect(Symbols.TickerDecimals==3,"ticker decimal count changed");
            Expect(Symbols.FormatPrice3(3.2)=="3.200","price not 3dp: "+Symbols.FormatPrice3(3.2));
            Expect(Symbols.FormatPrice3(1.23456)=="1.235","price not rounded to 3dp: "+Symbols.FormatPrice3(1.23456));
            Expect(Symbols.FormatChange3(-1.5)=="-1.500","negative change not 3dp: "+Symbols.FormatChange3(-1.5));
            Expect(Symbols.FormatChange3(2)=="+2.000","positive change missing sign");
            Expect(Symbols.FormatPct3(2.3456)=="+2.346%","pct not 3dp: "+Symbols.FormatPct3(2.3456));
            Expect(Symbols.FormatPct3(-0.4)=="-0.400%","negative pct not 3dp");
        });

        Check("indices keep the fixed display order", delegate {
            var scrambled=new List<IndexQuote>{
                new IndexQuote{Code="s_sz399006",Name="创业板指",Price=2100,Change=-12.3,ChangePct=-0.58},
                new IndexQuote{Code="s_sh000001",Name="上证指数",Price=3200,Change=12.3,ChangePct=0.39},
                new IndexQuote{Code="s_sz399001",Name="",Price=10500,Change=45,ChangePct=0.43}
            };
            var ordered=QuoteHub.NormalizeIndices(scrambled);
            Expect(ordered.Count==3 && ordered[0].Code=="s_sh000001" && ordered[1].Code=="s_sz399001" && ordered[2].Code=="s_sz399006","index order drifted: "+ordered[0].Code+"/"+ordered[1].Code+"/"+ordered[2].Code);
            Expect(ordered[1].Name=="深证成指","missing index name not backfilled: "+ordered[1].Name);
            Expect(Symbols.IndexRank("sh000001")==0 && Symbols.IndexRank("s_sh000001")==0,"rank normalization failed");
            Expect(Symbols.IndexRank("s_bk0800")==Symbols.IndexOrder.Length,"unknown index must sort last");
            Expect(Symbols.IndexOrder.Length>=7,"index order list truncated");
        });

        Check("refresh profiles widen the polling cadence", delegate {
            MarketSession session=MarketSession.MorningTrade;
            int turbo=MarketClock.QuoteIntervalSeconds(session,RefreshProfile.Turbo);
            int fast=MarketClock.QuoteIntervalSeconds(session,RefreshProfile.Fast);
            int balanced=MarketClock.QuoteIntervalSeconds(session,RefreshProfile.Balanced);
            int saver=MarketClock.QuoteIntervalSeconds(session,RefreshProfile.Saver);
            Expect(turbo<=fast && fast<balanced && balanced<saver,"cadence not monotonic: "+turbo+"/"+fast+"/"+balanced+"/"+saver);
            Expect(fast==1,"default profile should poll every second while trading");
            Expect(MarketClock.ParseProfile("nonsense")==RefreshProfile.Fast,"unknown profile must fall back to fast");
            Expect(MarketClock.ProfileKey(RefreshProfile.Saver)=="saver" && MarketClock.ProfileKey(RefreshProfile.Turbo)=="turbo","profile key round trip failed");
            Expect(MarketClock.OverviewIntervalSeconds(session,RefreshProfile.Fast)<MarketClock.OverviewIntervalSeconds(session,RefreshProfile.Balanced),"overview cadence not accelerated");
            Expect(MarketClock.Profiles().Length==4,"profile list incomplete");
        });

        Check("font overrides fall back to a real family", delegate {
            string[] families=Theme.InstalledFamilies();
            Expect(families.Length>0,"no installed font families detected");
            bool found=false;
            foreach(string family in families)
                if(family.Equals(Theme.DefaultMonoFamily,StringComparison.OrdinalIgnoreCase) || family.Equals(Theme.DefaultUiFamily,StringComparison.OrdinalIgnoreCase)) found=true;
            Expect(found,"neither default font family is installed on this machine");
            // GDI+ 对未知族名不报错而是静默换族，所以设置页必须用 FamilyAvailable 判断而不是 try/catch
            Expect(!Theme.FamilyAvailable("NoSuchFontFamily-XYZ"),"bogus family reported as installed");
            Expect(Theme.FamilyAvailable(families[0]),"installed family wrongly rejected");
            using(Font named=Theme.Create("NoSuchFontFamily-XYZ",9f,FontStyle.Regular,null))
                Expect(named==null || !named.FontFamily.Name.Equals("NoSuchFontFamily-XYZ",StringComparison.OrdinalIgnoreCase),
                    "bogus family resolved as itself");
            using(Font empty=Theme.Create("",9f,FontStyle.Regular,null)) Expect(empty==null,"empty family must not create a font");
            using(Font fallback=Theme.Create("NoSuchFontFamily-XYZ",9f,FontStyle.Regular,FontFamily.GenericSansSerif))
                Expect(fallback!=null && fallback.FontFamily.Name.Length>0,"fallback family not applied");
            string previous=Theme.UiFamily;
            Theme.UiFamily="NoSuchFontFamily-XYZ";
            using(Font resolved=Theme.Ui(9f,FontStyle.Regular)) Expect(resolved!=null && resolved.FontFamily.Name.Length>0,"Ui() must never throw on a missing family");
            Theme.UiFamily=previous;
            Expect(Theme.FamilyAvailable(families[0]) && Theme.UiFamily.Length>0,"font override did not restore the default");
        });

        Check("ticker height drives how many rows fit", delegate {
            Expect(AppHost.TickerAutoHeight(6,11)>AppHost.TickerAutoHeight(3,11),"auto height must grow with row count");
            Expect(AppHost.RowsForHeight(10,11,AppHost.MaxTickerRows)==AppHost.MinTickerRows,"tiny height must clamp to the minimum rows");
            int tight=AppHost.RowsForHeight(60,11,AppHost.MaxTickerRows);
            int roomy=AppHost.RowsForHeight(160,11,AppHost.MaxTickerRows);
            Expect(tight>=AppHost.MinTickerRows && roomy>tight,"taller window must fit more rows: "+tight+"/"+roomy);
            Expect(AppHost.RowsForHeight(100000,11,AppHost.MaxTickerRows)==AppHost.MaxTickerRows,"row count not capped");
            Expect(AppHost.MinTickerHeight>0 && AppHost.MaxTickerHeight>AppHost.MinTickerHeight,"ticker height bounds broken");
        });

        Check("free resize persists width and height", delegate {
            var app=new AppHost();app.Init();
            app.SetTickerUserSize(430,168);
            Expect(app.TickerWidth==430,"user width not persisted");
            Expect(app.TickerHeight==168,"user height not persisted");
            Expect(app.TickerRows==AppHost.RowsForHeight(168,app.TickerFontSize,AppHost.MaxTickerRows),"row count not derived from dragged height");
            // 改字号必须放弃用户拖出来的固定高度，否则字变大后会溢出
            app.TickerFontSize=app.TickerFontSize;
            Expect(app.TickerHeight==0,"fixed height must reset when the font size changes");
            app.SetTickerUserSize(430,168);
            using(var ticker=new TickerBarForm(app)) {
                ticker.Relayout();
                float scale=Theme.Scale(ticker);
                Expect(ticker.Width==(int)Math.Round(430*scale),"ticker width not applied: "+ticker.Width);
                Expect(ticker.Height==(int)Math.Round(168*scale),"ticker height not applied: "+ticker.Height);
                Expect(!ticker.TopMost==!app.TickerAlwaysOnTop,"topmost flag out of sync");
            }
            // 窗口销毁后改设置不能再回调到已释放的浮窗（历史上会抛 ObjectDisposedException）
            app.TickerOpacity=88;
            app.TickerFontSize=12;
            Expect(app.TickerOpacity==88,"settings write failed after the ticker was disposed");
        });

        Check("settings page exposes schedule and font controls", delegate {
            var app=new AppHost();app.Init();
            using(var form=new Form()) {
                form.ClientSize=new Size(660,620);
                var page=new SettingsPage(app);
                page.Dock=DockStyle.Fill;
                form.Controls.Add(page);
                form.Show();Pump(60);
                page.SyncFromHost();
                var schedule=Value<CheckBox>(page,"_schedule");
                var from=Value<DateTimePicker>(page,"_from");
                var to=Value<DateTimePicker>(page,"_to");
                var uiFont=Value<ComboBox>(page,"_uiFont");
                var numberFont=Value<ComboBox>(page,"_numberFont");
                var profile=Value<ComboBox>(page,"_profile");
                var height=Value<TrackBar>(page,"_height");
                var associate=Value<Button>(page,"_associate");
                Expect(uiFont.Items.Count>=2 && numberFont.Items.Count>=2,"font pickers not populated");
                Expect(Convert.ToString(uiFont.Items[0])==Convert.ToString(numberFont.Items[0]),"font pickers must share the same default entry");
                Expect(profile.Items.Count==MarketClock.Profiles().Length,"refresh profile list incomplete");
                Expect(from.CustomFormat=="HH:mm" && to.ShowUpDown,"time pickers not configured");
                Expect(height.Minimum==0,"auto height not selectable");
                Expect(from.Enabled==schedule.Checked,"time pickers must follow the schedule toggle");
                Expect(associate.Width>=Ui.ButtonSize(associate).Width,"associate button clips its text");
                Expect(page.AutoScrollMinSize.Height>0,"settings content height not reported");
                foreach(Control control in page.Controls)
                    if(control.Visible) Expect(control.Left>=-2 && control.Right<=page.ClientSize.Width+2,
                        "control outside settings page: "+control.GetType().Name+" right="+control.Right+" width="+page.ClientSize.Width);
                form.Close();page.Dispose();
            }
        });

        Check("fund association uses holdings before name", delegate {
            var catalog=BoardCatalogSnapshot.Load();
            var byName=FundBoardMatcher.Match("东方人工智能主题混合A",catalog);
            Expect(byName.Count>0 && byName[0].Code=="BK0800","name matching regressed");
            var byHolding=FundBoardMatcher.MatchIndustries(new List<string>{"半导体","医疗器械"},catalog);
            Expect(byHolding.Count>0,"holding-industry matching produced nothing");
            Expect(byHolding.Count<=2,"holding-industry matching must stay focused");
            Expect(FundBoardMatcher.MatchIndustries(new List<string>(),catalog).Count==0,"empty industries must not match");
            Expect(FundBoardMatcher.InferThemes("国泰中证半导体ETF联接").Contains("半导体"),"theme inference lost semiconductor");
            var app=new AppHost();app.Init();app.Watchlist.Clear();
            app.Watchlist.Add(new WatchItem{Code="of005844",Market="OF",Name="测试基金"});
            app.SetFundBoards("of005844",new List<BoardLink>{new BoardLink{Code="BK0800",Name="人工智能"}});
            Expect(app.FundBoardMode("of005844")=="manual","manual mode not recorded");
            Expect(app.FundBoardSourceText("of005844")=="手动关联","manual label wrong: "+app.FundBoardSourceText("of005844"));
            Expect(app.FundBoardSourceText("of000000")=="","unassociated fund must have no mode label");
        });

        Check("large value rows are tall enough for their font", delegate {
            Font[] tokens=new Font[]{Theme.FSmall,Theme.FNormal,Theme.FMono,Theme.FHeader,Theme.FTag,Theme.FValue,Theme.FValueLarge,Theme.FDisplay};
            foreach(Font font in tokens) {
                if(font==null) throw new Exception("font token is null");
                RectangleF box=Theme.TextRow(new Rectangle(0,0,200,24),font,0);
                Expect(box.Height>=font.Height,"TextRow shorter than its font at "+font.Size+"pt ("+box.Height+" < "+font.Height+")");
            }
            RectangleF grown=Theme.TextRow(new Rectangle(0,10,100,10),Theme.FDisplay,0);
            Expect(grown.Height>=Theme.FDisplay.Height && grown.Y<10,"TextRow must grow around the row centre");
        });

        Check("overview shows the market turnover number", delegate {
            var app=new AppHost();app.Init();
            SetField(app.Hub,"<Overview>k__BackingField",new MarketOverview{
                Turnover=1.2e12,Up=2800,Down=1900,Flat=150,
                Industry=new List<SectorItem>{new SectorItem{Code="BK0800",Name="人工智能",ChangePct=3.2}},
                Concept=new List<SectorItem>{new SectorItem{Code="BK1158",Name="CPO概念",ChangePct=4.1}}});
            using(var shell=new Form()) {
                shell.ClientSize=new Size(460,430);
                shell.StartPosition=FormStartPosition.Manual;
                shell.Location=new Point(-4000,-4000);
                var overview=new OverviewPanel(app);
                overview.Dock=DockStyle.Fill;
                shell.Controls.Add(overview);
                shell.Show();Pump(120);
                using(var bmp=new Bitmap(shell.ClientSize.Width,shell.ClientSize.Height)) {
                    overview.DrawToBitmap(bmp,new Rectangle(0,0,bmp.Width,bmp.Height));
                    // 成交额那一行在卡片右上（x 靠右、y 在标题下方），必须真的有深色像素
                    int dark=0;
                    for(int x=bmp.Width-150;x<bmp.Width-12;x++)
                        for(int y=40;y<78;y++) {
                            Color c=bmp.GetPixel(x,y);
                            if(c.R<120 && c.G<120 && c.B<120) dark++;
                        }
                    Expect(dark>40,"market turnover number is missing from the overview card (dark pixels="+dark+")");
                }
                shell.Close();overview.Dispose();
            }
        });

        // ── 2.5.0：午休显示最新净值 / 海外指数关联 / 浮窗显隐可靠性 ──

        Check("published NAV stays visible during the lunch break", delegate {
            var quote=new Quote{Code="of017811",Market="OF",Price=3.216,PriceKind="净值",PriceTime="2026-10-16",ChangePct=1.2};
            Expect(!FundPresentation.UseNav(quote,new DateTime(2026,10,19,11,29,0)),"morning session must stay in board mode");
            Expect(FundPresentation.UseNav(quote,new DateTime(2026,10,19,12,0,0)),"lunch break must show the latest NAV");
            Expect(FundPresentation.UseNav(quote,new DateTime(2026,10,19,12,59,0)),"lunch break must show the latest NAV");
            Expect(!FundPresentation.UseNav(quote,new DateTime(2026,10,19,13,0,0)),"afternoon session must return to board mode");
            Expect(FundPresentation.IsLunchBreak(new TimeSpan(11,30,0)) && !FundPresentation.IsLunchBreak(new TimeSpan(13,0,0)),"lunch window bounds wrong");
            string hint=FundPresentation.BoardModeHint(quote);
            Expect(hint.Contains("2026-10-16") && hint.Contains("上一净值"),"board mode hint must carry the NAV date: "+hint);
            Expect(FundPresentation.LastNavText(quote).Contains("3.216"),"last NAV text lost the value: "+FundPresentation.LastNavText(quote));
            Expect(FundPresentation.NavDateShort(quote)=="10-16","short NAV date wrong: "+FundPresentation.NavDateShort(quote));
        });

        Check("overseas index funds can be associated", delegate {
            var catalog=BoardCatalogSnapshot.Load();
            var nasdaq=FundBoardMatcher.Match("南方纳斯达克100指数发起(QDII)I",catalog);
            Expect(nasdaq.Count>0 && nasdaq[0].Code=="ix100.NDX","nasdaq QDII fund not linked: "+(nasdaq.Count>0?nasdaq[0].Code+" "+nasdaq[0].Name:"none"));
            var hstech=FundBoardMatcher.Match("华夏恒生科技ETF联接A",catalog);
            Expect(hstech.Count>0 && hstech[0].Code=="ix124.HSTECH","hang seng tech fund not linked: "+(hstech.Count>0?hstech[0].Code+" "+hstech[0].Name:"none"));
            Expect(FundBoardMatcher.Match("东方稳健债券A",catalog).Count==0,"bond fund auto-associated to an index");
            Expect(BoardLink.ValidCode("ix100.NDX") && BoardLink.ValidCode("BK0800") && !BoardLink.ValidCode("931071"),"board code validation broken");
            var roundTrip=BoardLink.Decode(BoardLink.Encode(new List<BoardLink>{new BoardLink{Code="ix100.NDX",Name="纳斯达克100"}}));
            Expect(roundTrip.Count==1 && roundTrip[0].Code=="ix100.NDX" && roundTrip[0].Name=="纳斯达克100","index board round trip failed");
            var quotes=BoardClient.ParseIndexQuotes("{\"data\":{\"diff\":[{\"f12\":\"NDX\",\"f13\":100,\"f14\":\"纳斯达克\",\"f3\":-2.15,\"f124\":1792180800}]}}",new DateTime(2026,10,19,10,0,0));
            Expect(quotes.Count==1 && quotes[0].Code=="ix100.NDX" && Math.Abs(quotes[0].ChangePct+2.15)<0.001 && quotes[0].Overseas,"overseas quote parsed incorrectly");
            // 海外指数跨隔夜仍算「最新一期」，不能被判成「数据待更新」
            var board=new BoardQuote{Code="ix100.NDX",Name="纳斯达克100",ChangePct=-2.15,DataTime=new DateTime(2026,10,16,21,30,0),ReceivedAt=new DateTime(2026,10,19,10,0,0),Overseas=true};
            Expect(board.IsCurrent(new DateTime(2026,10,19,10,0,30)),"overnight index quote flagged stale");
            Expect(!board.IsCurrent(new DateTime(2026,10,19,10,10,0)),"expired index quote stays current");
            Expect(IndexBoards.Count>=8,"overseas index table truncated");
        });

        Check("overnight index trend keeps the whole session", delegate {
            // 美股时段跨零点（北京时间 21:30 → 次日 04:00）：不能按日期切成两半
            string payload="{\"data\":{\"name\":\"纳斯达克\",\"preClose\":100,\"time\":1792180800,\"trends\":[\"2026-10-16 21:30,100,101\",\"2026-10-16 23:59,101,102\",\"2026-10-17 04:00,102,99\"]}}";
            var trend=BoardClient.ParseTrend(payload);
            Expect(trend.Date==new DateTime(2026,10,16),"overnight session dated wrong: "+trend.Date.ToString("yyyy-MM-dd"));
            Expect(trend.Points.Count==3,"overnight session split across midnight: "+trend.Points.Count);
        });

        Check("ticker shows and hides on demand", delegate {
            var app=new AppHost();app.Init();
            app.TickerScheduleEnabled=true;      // 定时调度开着也不能抢用户的显隐操作
            using(var ticker=new TickerBarForm(app)) {
                app.Ticker=ticker;
                var apply=typeof(TickerBarForm).GetMethod("ApplySchedule",Private);
                app.HideTicker();
                Expect(!ticker.Visible,"manual hide did not take effect");
                apply.Invoke(ticker,new object[]{DateTime.Now});
                Expect(!ticker.Visible,"scheduler pulled the ticker back after a manual hide");
                app.ShowTicker();
                Expect(ticker.Visible,"manual show did not take effect");
                app.ToggleTicker();
                Expect(!ticker.Visible,"toggle did not hide a visible ticker");
                app.ToggleTicker();
                Expect(ticker.Visible,"toggle did not show a hidden ticker");
                // 面板接管期间，调度器不得把浮窗拉回面板上面
                ticker.HoldForPanel();
                apply.Invoke(ticker,new object[]{DateTime.Now});
                Expect(!ticker.Visible,"panel hold was overridden by the scheduler");
                ticker.ReleaseFromPanel(true);
                Expect(ticker.Visible,"panel release did not restore the ticker");
                // 上次退出时是隐藏状态：启动后不能被调度器立刻拉出来
                ticker.HideByUser();
                using(var again=new TickerBarForm(app)) {
                    app.Ticker=again;
                    again.StartHiddenByIntent();
                    typeof(TickerBarForm).GetMethod("ApplySchedule",Private).Invoke(again,new object[]{DateTime.Now});
                    Expect(!again.Visible,"startup intent ignored: hidden ticker came back");
                }
                app.Ticker=null;      // 表单马上释放，别让设置回调打到已销毁的窗口
            }
            app.TickerScheduleEnabled=false;
        });

        // ── 2.5.1：基金涨跌跟着关联板块实时走 ──

        Check("fund estimate follows the linked boards", delegate {
            var now=new DateTime(2026,10,19,10,30,0);
            var quotes=new List<BoardQuote>{
                new BoardQuote{Code="BK0800",Name="人工智能",ChangePct=2.0,DataTime=now,ReceivedAt=now},
                new BoardQuote{Code="BK1041",Name="医疗器械",ChangePct=-1.0,DataTime=now,ReceivedAt=now},
                new BoardQuote{Code="BK9999",Name="过期板块",ChangePct=9.9,DataTime=now.AddDays(-1),ReceivedAt=now.AddMinutes(-10)}
            };
            var estimate=FundPresentation.Estimate(quotes,now);
            Expect(estimate.HasValue && estimate.Count==2,"board count wrong: "+estimate.Count);
            Expect(Math.Abs(estimate.Pct-0.5)<1e-9,"equal-weight average wrong: "+estimate.Pct);
            Expect(estimate.Stale==1,"stale board must not join the average: "+estimate.Stale);
            var quote=new Quote{Code="of017811",Market="OF",Price=3.0,PriceKind="净值",PriceTime="2026-10-16"};
            Expect(Math.Abs(FundPresentation.EstimateNav(quote,estimate)-3.015)<1e-9,"estimated NAV wrong");
            Expect(FundPresentation.EstimateHint(quote,estimate,now).Contains("2 个等权"),"estimate hint lost the board count");
            Expect(!FundPresentation.Estimate(new List<BoardQuote>(),now).HasValue,"empty board list must not estimate");
            // 三档口径：盘中/午休走估值，当天净值公布后走正式净值，没有板块数据退回上一净值
            var trading=FundPresentation.Describe(quote,estimate,new DateTime(2026,10,19,10,30,0));
            Expect(trading!=null && trading.Estimated && !trading.Official && trading.PriceText.EndsWith("*"),"trading hours must follow the boards");
            Expect(Math.Abs(trading.Pct-0.5)<1e-9,"trading estimate pct wrong: "+trading.Pct);
            var lunch=FundPresentation.Describe(quote,estimate,new DateTime(2026,10,19,12,5,0));
            Expect(lunch!=null && lunch.Estimated,"lunch break must still follow the boards");
            var published=new Quote{Code="of017811",Market="OF",Price=3.05,PriceKind="净值",PriceTime="2026-10-19",ChangePct=1.65};
            var afterNav=FundPresentation.Describe(published,estimate,new DateTime(2026,10,19,20,0,0));
            Expect(afterNav!=null && afterNav.Official && !afterNav.Estimated && !afterNav.PriceText.Contains("*"),"published NAV must win over the estimate");
            var fallback=FundPresentation.Describe(quote,new FundEstimate(),new DateTime(2026,10,19,12,5,0));
            Expect(fallback!=null && fallback.Fallback && !fallback.HasLivePct && !fallback.PriceText.Contains("*"),"no boards must not expose the previous day's return");
            Expect(double.IsNaN(fallback.Pct),"fallback pct must stay empty");
            Expect(fallback.PriceText==Symbols.FormatPrice(quote.Price),"fallback must show the last NAV");
            var waiting=FundPresentation.Describe(quote,new FundEstimate(),new DateTime(2026,10,19,10,30,0));
            Expect(waiting!=null && waiting.Fallback && !waiting.HasLivePct,"trading without boards must stay neutral");
            // 昨晚收盘后、今天净值还没公布：同样不能用昨天的涨跌幅冒充今天
            var stale=FundPresentation.Describe(quote,new FundEstimate(),new DateTime(2026,10,19,20,0,0));
            Expect(stale!=null && stale.Fallback,"yesterday's NAV must not be shown as today's return");
            Expect(FundPresentation.IsTodayNav(published,new DateTime(2026,10,19,20,0,0)) && !FundPresentation.IsTodayNav(quote,new DateTime(2026,10,19,20,0,0)),"today NAV detection broken");
            // 真实场景（2026-10-19 午休 017811）：关联 半导体材料 −1.00% / 半导体设备 −3.35%，
            // 基金的官方日涨跌 −0.08% 是 10-16 的，绝不能被当成今天
            var realQuotes=new List<BoardQuote>{
                new BoardQuote{Code="BK1325",Name="半导体材料",ChangePct=-1.00,DataTime=now,ReceivedAt=now},
                new BoardQuote{Code="BK1326",Name="半导体设备",ChangePct=-3.35,DataTime=now,ReceivedAt=now}};
            var realNav=new Quote{Code="of017811",Market="OF",Price=2.9203,PriceKind="净值",PriceTime="2026-10-16",ChangePct=-0.08};
            var realDisplay=FundPresentation.Describe(realNav,FundPresentation.Estimate(realQuotes,now),new DateTime(2026,10,19,12,5,0));
            Expect(realDisplay.Estimated && realDisplay.PriceText=="2.8568*","real scenario price wrong: "+realDisplay.PriceText);
            Expect(Math.Abs(realDisplay.Pct+2.175)<1e-9,"real scenario pct wrong: "+realDisplay.Pct);
            Expect(Math.Abs(realDisplay.Change+0.063517)<1e-5,"real scenario change wrong: "+realDisplay.Change);
            Expect(FundPresentation.DetailText(quote,estimate,trading,"BK0800 +1.00%").Contains("2 个等权"),"detail text lost the estimate label");
            // AppHost 这一层要把关联板块 -> 实时报价串起来（自选表/浮窗/详情都走它）
            var app=new AppHost();app.Init();app.Watchlist.Clear();
            app.Watchlist.Add(new WatchItem{Code="of017811",Market="OF",Name="测试基金"});
            app.SetFundBoards("of017811",new List<BoardLink>{new BoardLink{Code="BK0800",Name="人工智能"}});
            var boardQuotes=Value<Dictionary<string,BoardQuote>>(app.Hub.Boards,"_quotes");
            boardQuotes["BK0800"]=new BoardQuote{Code="BK0800",Name="人工智能",ChangePct=1.5,DataTime=MarketClock.NowCst(),ReceivedAt=MarketClock.NowCst()};
            var live=app.EstimateFund("of017811");
            Expect(live.HasValue && Math.Abs(live.Pct-1.5)<1e-9,"AppHost.EstimateFund ignored the board quote");
            app.SetFundBoards("of017811",new List<BoardLink>());
            Expect(!app.EstimateFund("of017811").HasValue,"fund without boards must not estimate");
        });

        // ── 2.6.0：盘中估值改用前十大重仓加权 ──

        Check("holdings weighted estimate beats board average", delegate {
            var now=new DateTime(2026,10,20,10,15,0);
            // 017811 东方人工智能主题混合C，2026-10-20 上午的真实数据：
            // 板块等权 +1.84%，但重仓股加权是负的 —— 养基宝显示的也是负的
            var holdings=new List<FundHolding>{
                new FundHolding{Code="sh688361",Name="中科飞测",Weight=9.55},
                new FundHolding{Code="sh688037",Name="芯源微",Weight=9.19},
                new FundHolding{Code="sh688012",Name="中微公司",Weight=9.12},
                new FundHolding{Code="sh688120",Name="华海清科",Weight=9.08},
                new FundHolding{Code="sz002371",Name="北方华创",Weight=8.92},
                new FundHolding{Code="sz300567",Name="精测电子",Weight=8.69},
                new FundHolding{Code="sh688409",Name="富创精密",Weight=7.57},
                new FundHolding{Code="sh688072",Name="拓荆科技",Weight=7.44},
                new FundHolding{Code="sh688256",Name="寒武纪",Weight=7.04},
                new FundHolding{Code="sh688082",Name="盛美上海",Weight=6.87}};
            var moves=new Dictionary<string,double>{
                {"sh688361",-3.11},{"sh688037",0.78},{"sh688012",-0.83},{"sh688120",-1.19},{"sz002371",0.05},
                {"sz300567",2.40},{"sh688409",2.40},{"sh688072",-1.80},{"sh688256",-0.38},{"sh688082",0.0}};
            double pct,coverage;int used;
            Expect(HoldingsEstimator.TryWeighted(holdings,moves,out pct,out coverage,out used),"weighted estimate failed");
            Expect(used==10 && Math.Abs(coverage-83.47)<0.01,"coverage wrong: "+coverage);
            Expect(Math.Abs(pct+0.175043)<0.001,"weighted pct wrong: "+pct);
            // 同一套行情下，板块等权会给出相反的符号 —— 这就是「浮窗是正、养基宝是负」的原因
            var boardQuotes=new List<BoardQuote>{
                new BoardQuote{Code="BK1325",Name="半导体材料",ChangePct=2.76,DataTime=now,ReceivedAt=now},
                new BoardQuote{Code="BK1326",Name="半导体设备",ChangePct=0.92,DataTime=now,ReceivedAt=now}};
            var boards=FundPresentation.Estimate(boardQuotes,now);
            Expect(boards.HasValue && boards.Pct>0 && boards.Source=="boards","board fallback changed");
            // 覆盖不足 / 缺行情时不能瞎估
            Expect(!HoldingsEstimator.TryWeighted(holdings,new Dictionary<string,double>(),out pct,out coverage,out used),"no quotes must not estimate");
            var thin=new List<FundHolding>{new FundHolding{Code="sh688361",Weight=3.0}};
            Expect(!HoldingsEstimator.TryWeighted(thin,new Dictionary<string,double>{{"sh688361",-1.0}},out pct,out coverage,out used),"thin coverage must not estimate");
        });

        Console.WriteLine("Failures: "+failed);return failed==0?0:1;    }
    static double Contrast(Color a,Color b) { double x=Luminance(a), y=Luminance(b);return (Math.Max(x,y)+0.05)/(Math.Min(x,y)+0.05); }
    static double Luminance(Color c) { return 0.2126*Channel(c.R)+0.7152*Channel(c.G)+0.0722*Channel(c.B); }
    static double Channel(byte raw) { double x=raw/255.0;return x<=0.03928?x/12.92:Math.Pow((x+0.055)/1.055,2.4); }
}
