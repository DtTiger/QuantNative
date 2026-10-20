using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using QuantNative.Core;

namespace QuantNative.UI
{
    public sealed class FundBoardsForm : Form
    {
        private readonly AppHost _host;
        private readonly WatchItem _fund;
        private readonly TextBox _search;
        private readonly ListBox _results, _selected;
        private readonly Label _status;
        private int _searchSeq;

        public FundBoardsForm(AppHost host, WatchItem fund)
        {
            _host = host; _fund = fund;
            Text = "关联板块 · " + fund.Name;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(720, 470); MinimumSize = new Size(620, 420);
            BackColor = Theme.Bg; ForeColor = Theme.TextPrimary; Font = Theme.FNormal;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            var intro = new Label { Text = "选择这只基金要跟踪的行业或概念板块，可关联多项。\r\n「自动推荐」先读该基金的前十大重仓股，按重仓股所属行业匹配板块；取不到持仓时退回名称匹配。", AutoSize = true, Margin = new Padding(0,0,0,12) };
            layout.Controls.Add(intro,0,0); layout.SetColumnSpan(intro,2);
            var searchRow = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, AutoSize=true, Margin=new Padding(0,0,8,8) };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _search=Ui.Text("");_search.Dock=DockStyle.Fill;
            var searchButton=Ui.Button("搜索",delegate { Search(); });searchButton.AutoSize=true;
            searchRow.Controls.Add(_search,0,0);searchRow.Controls.Add(searchButton,1,0);layout.Controls.Add(searchRow,0,1);
            layout.Controls.Add(new Label{Text="已关联（按此顺序显示）",AutoSize=true,Padding=new Padding(0,5,0,0)},1,1);
            _results=MakeList();_selected=MakeList();
            layout.Controls.Add(_results,0,2);layout.Controls.Add(_selected,1,2);
            foreach(var link in host.GetFundBoards(fund.Code))_selected.Items.Add(link);
            _results.DoubleClick+=delegate { AddSelected(); };
            var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};
            foreach(var button in new[]{Ui.Button("自动推荐",delegate { Suggest(); }),Ui.Button("添加关联 →",delegate { AddSelected(); }),Ui.Button("移除关联",delegate { if(_selected.SelectedIndex>=0)_selected.Items.RemoveAt(_selected.SelectedIndex); }),Ui.Button("上移",delegate { MoveLink(-1); }),Ui.Button("下移",delegate { MoveLink(1); })}) { button.AutoSize=true;actions.Controls.Add(button); }
            layout.Controls.Add(actions,0,3);layout.SetColumnSpan(actions,2);
            var footer=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=3,Padding=new Padding(0,8,0,0)};
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _status=new Label{Text="输入板块名称或 BK 代码，例如：人工智能",AutoSize=true,ForeColor=Theme.TextSecondary,Dock=DockStyle.Fill};
            var save=Ui.PrimaryButton("保存关联",delegate { var links=new List<BoardLink>();foreach(BoardLink link in _selected.Items)links.Add(link);_host.SetFundBoards(_fund.Code,links);DialogResult=DialogResult.OK;Close(); });save.AutoSize=true;
            var cancel=Ui.Button("取消",delegate { Close(); });cancel.AutoSize=true;cancel.DialogResult=DialogResult.Cancel;
            footer.Controls.Add(_status,0,0);footer.Controls.Add(save,1,0);footer.Controls.Add(cancel,2,0);
            layout.Controls.Add(footer,0,4);layout.SetColumnSpan(footer,2);CancelButton=cancel;
            _search.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;Search();}};
        }
        private ListBox MakeList()
        {
            var list=new ListBox{Dock=DockStyle.Fill,IntegralHeight=false,BackColor=Theme.Card,ForeColor=Theme.TextPrimary,BorderStyle=BorderStyle.FixedSingle,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=30,HorizontalScrollbar=true};
            list.DrawItem+=delegate(object sender,DrawItemEventArgs e){
                bool selected=(e.State&DrawItemState.Selected)!=0;
                using(var brush=new SolidBrush(selected?Theme.AccentDim:Theme.Card))e.Graphics.FillRectangle(brush,e.Bounds);
                if(selected)
                {
                    using(var bar=new SolidBrush(Theme.Accent))
                        e.Graphics.FillRectangle(bar,new Rectangle(e.Bounds.X,e.Bounds.Y,3,e.Bounds.Height));
                }
                if(e.Index>=0)TextRenderer.DrawText(e.Graphics,list.Items[e.Index].ToString(),list.Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y,e.Bounds.Width-16,e.Bounds.Height),selected?Theme.TextPrimary:Theme.TextSecondary,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            };
            return list;
        }
        private async void Search()
        {
            int seq=++_searchSeq;string keyword=_search.Text.Trim();_results.Items.Clear();if(keyword.Length==0)return;
            _status.Text="正在搜索板块…";
            try{var results=await _host.Hub.Boards.SearchAsync(keyword);if(IsDisposed||seq!=_searchSeq)return;foreach(var link in results)_results.Items.Add(link);_status.Text=results.Count==0?"没有匹配板块，请试试简称或 BK 代码":"双击结果添加，可选择多个板块";}
            catch(Exception ex){if(!IsDisposed&&seq==_searchSeq)_status.Text="搜索失败，请重试："+ex.Message;}
        }
        private void AddSelected()
        {
            var link=_results.SelectedItem as BoardLink;if(link==null)return;
            foreach(BoardLink item in _selected.Items)if(item.Code==link.Code)return;
            _selected.Items.Add(link);_selected.SelectedIndex=_selected.Items.Count-1;
        }
        private async void Suggest()
        {
            _status.Text = "正在读取前十大重仓股并按行业匹配板块…";
            try
            {
                List<BoardLink> suggestions = await _host.SuggestBoardsAsync(_fund.Code, _fund.Name);
                if (IsDisposed) return;
                int added = 0;
                foreach (var link in suggestions)
                {
                    bool exists = false;
                    foreach (BoardLink item in _selected.Items) if (item.Code == link.Code) { exists = true; break; }
                    if (!exists) { _selected.Items.Add(link); added++; }
                }
                _status.Text = added > 0
                    ? "已添加 " + added + " 个推荐（重仓股行业 + 名称匹配），可继续手动调整"
                    : "未匹配到板块，请手动搜索关联";
            }
            catch (Exception ex) { if (!IsDisposed) _status.Text = "自动推荐暂不可用：" + ex.Message; }
        }
        private void MoveLink(int delta)
        {
            int index=_selected.SelectedIndex,target=index+delta;if(index<0||target<0||target>=_selected.Items.Count)return;
            object item=_selected.Items[index];_selected.Items.RemoveAt(index);_selected.Items.Insert(target,item);_selected.SelectedIndex=target;
        }
    }
}
