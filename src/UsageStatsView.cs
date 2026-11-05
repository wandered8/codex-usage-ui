using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed partial class UsageCardManager
{
    public sealed class TokenRow { public string day; public string model; public long input; public long output; public long cached; public long total; public int records; }
    public sealed class TokenSnapshot { public long updatedAt; public int unreadableFiles; public int sessionCount; public TokenRow[] rows; }
    private readonly ComboBox periodBox=new ComboBox(), metricBox=new ComboBox();
    private readonly DataGridView table=new DataGridView();
    private TokenSnapshot statistics;
    private DateTime statisticsFileTime=DateTime.MinValue;
    private List<TokenRow> selectedRows=new List<TokenRow>();
    private float statisticsScale=1;
    private Dictionary<Control,Rectangle> logicalBounds;
    private readonly ToolTip chartTooltip=new ToolTip {InitialDelay=120,ReshowDelay=60,AutoPopDelay=15000,ShowAlways=true};
    private int hoveredDay=-1;
    private readonly Color ink=Color.FromArgb(30,36,46), muted=Color.FromArgb(107,114,128), accent=Color.FromArgb(8,145,178);

    // 窗口只展示本机 Token 统计，价格和侧栏设置均不在此页面。
    private void InitializeStatistics()
    {
        AutoScaleMode=AutoScaleMode.None;
        Text="Codex用量统计";ClientSize=new Size(940,710);BackColor=Color.FromArgb(246,248,251);ForeColor=ink;
        StartPosition=FormStartPosition.CenterScreen;FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;DoubleBuffered=true;
        Font=new Font("Microsoft YaHei UI",10);
        periodBox.Items.AddRange(new object[]{"今天","最近 7 天","最近 30 天"});periodBox.SelectedIndex=1;periodBox.Visible=false;
        periodBox.SelectedIndexChanged+=(sender,args)=>{RenderStatistics();RefreshTabs();};
        metricBox.Items.AddRange(new object[]{"Token 总量","输入 Token","输出 Token"});metricBox.SelectedIndex=0;metricBox.Visible=false;
        metricBox.SelectedIndexChanged+=(sender,args)=>{Invalidate();RefreshTabs();};
        for(int i=0;i<3;i++)
        {
            int index=i;var period=new StatsTabButton {Text=new[]{"今天","7 天","30 天"}[i],Selected=()=>periodBox.SelectedIndex==index};period.SetBounds(551+i*78,32,76,32);period.Click+=(sender,args)=>periodBox.SelectedIndex=index;Controls.Add(period);
            var metric=new StatsTabButton {Text=new[]{"总量","输入","输出"}[i],Selected=()=>metricBox.SelectedIndex==index};metric.SetBounds(678+i*66,248,64,30);metric.Click+=(sender,args)=>metricBox.SelectedIndex=index;Controls.Add(metric);
        }
        var sync=new StatsTabButton {Text="立即同步",Selected=()=>true};sync.SetBounds(804,30,108,36);
        sync.Click+=(sender,args)=>{try{if(worker!=null&&!worker.HasExited){worker.StandardInput.WriteLine("scan");worker.StandardInput.Flush();status.Text="正在同步本机记录…";}else status.Text="采集进程尚未就绪，正在重试。";}catch{status.Text="未能请求同步，请重新打开托盘程序。";}};
        table.SetBounds(40,523,860,136);table.ReadOnly=true;table.AllowUserToAddRows=false;table.AllowUserToDeleteRows=false;table.AllowUserToResizeRows=false;table.RowHeadersVisible=false;
        table.BackgroundColor=Color.White;table.BorderStyle=BorderStyle.None;table.EnableHeadersVisualStyles=false;table.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(248,250,252);table.ColumnHeadersDefaultCellStyle.ForeColor=muted;
        table.ColumnHeadersDefaultCellStyle.Font=new Font("Microsoft YaHei UI",9);table.ColumnHeadersDefaultCellStyle.Padding=new Padding(8,0,8,0);table.ColumnHeadersHeight=32;table.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        table.DefaultCellStyle.Font=new Font("Microsoft YaHei UI",9);table.DefaultCellStyle.Padding=new Padding(8,0,8,0);table.DefaultCellStyle.SelectionBackColor=Color.FromArgb(234,247,244);table.DefaultCellStyle.SelectionForeColor=ink;
        table.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(252,253,254);table.GridColor=Color.FromArgb(239,242,246);table.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;table.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;
        table.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;table.RowTemplate.Height=34;table.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
        foreach(string name in new[]{"日期","模型","输入","输出","缓存命中","Token 合计"})table.Columns.Add(name,name);
        table.Columns[0].FillWeight=90;table.Columns[1].FillWeight=160;
        for(int i=2;i<table.Columns.Count;i++){table.Columns[i].DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleRight;table.Columns[i].HeaderCell.Style.Alignment=DataGridViewContentAlignment.MiddleRight;table.Columns[i].DefaultCellStyle.Font=new Font("Consolas",10);}
        status.SetBounds(38,681,864,22);status.BackColor=BackColor;status.Font=new Font("Microsoft YaHei UI",8.5f);status.ForeColor=muted;status.Text="本机近 30 天会话记录 · 正在建立统计";
        Controls.AddRange(new Control[]{sync,table,status});
        logicalBounds=Controls.Cast<Control>().ToDictionary(control=>control,control=>control.Bounds);
        using(var graphics=Graphics.FromHwnd(IntPtr.Zero))ApplyStatisticsDpi((int)graphics.DpiX);
        DpiChanged+=(sender,args)=>ApplyStatisticsDpi(args.DeviceDpiNew);
        MouseMove+=ShowChartTooltip;
        MouseLeave+=(sender,args)=>{hoveredDay=-1;chartTooltip.Hide(this);};
        FormClosed+=(sender,args)=>chartTooltip.Dispose();
    }
    // 所有位置从 96 DPI 基线计算，避免重复缩放和 Windows 位图拉伸。
    private void ApplyStatisticsDpi(int dpi)
    {
        statisticsScale=dpi/96f;
        if(logicalBounds==null)return;
        SuspendLayout();ClientSize=new Size((int)Math.Round(940*statisticsScale),(int)Math.Round(710*statisticsScale));
        foreach(var item in logicalBounds){var r=item.Value;item.Key.Bounds=new Rectangle((int)Math.Round(r.X*statisticsScale),(int)Math.Round(r.Y*statisticsScale),(int)Math.Round(r.Width*statisticsScale),(int)Math.Round(r.Height*statisticsScale));}
        table.RowTemplate.Height=(int)Math.Round(34*statisticsScale);foreach(DataGridViewRow row in table.Rows)row.Height=table.RowTemplate.Height;
        table.ColumnHeadersHeight=(int)Math.Round(32*statisticsScale);ResumeLayout();Invalidate(true);
    }
    // 分段按钮使用原生 Button 的键盘事件，自绘选中和焦点样式。
    private sealed class StatsTabButton : Button
    {
        public Func<bool> Selected;
        public StatsTabButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Opaque,true);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;BackColor=Color.FromArgb(246,248,251);}
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e){RenderButton(e.Graphics);}
        public void RenderButton(Graphics graphics)
        {
            graphics.Clear(BackColor);
            bool active=Selected!=null&&Selected();graphics.SmoothingMode=SmoothingMode.AntiAlias;graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using(var path=Rounded(new RectangleF(1,1,Width-3,Height-3),8))using(var brush=new SolidBrush(active?Color.White:BackColor))graphics.FillPath(brush,path);
            using(var font=new Font("Microsoft YaHei UI",9,active?FontStyle.Bold:FontStyle.Regular))using(var brush=new SolidBrush(active?Color.FromArgb(15,118,110):Color.FromArgb(99,113,134)))using(var format=new StringFormat {Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})graphics.DrawString(Text,font,brush,ClientRectangle,format);
            if(Focused)ControlPaint.DrawFocusRectangle(graphics,new Rectangle(3,3,Width-7,Height-7));
        }
    }
    private void RefreshTabs(){foreach(Control control in Controls)if(control is StatsTabButton)control.Invalidate();}

    // 只读取后台聚合快照，不在 UI 线程扫描日志。
    private void ReadStatistics()
    {
        try
        {
            string file=Path.Combine(DirectoryPath,"token-stats.json");DateTime modified=File.GetLastWriteTimeUtc(file);if(modified==statisticsFileTime)return;
            TokenSnapshot next;using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var reader=new StreamReader(stream))next=new JavaScriptSerializer {MaxJsonLength=8*1024*1024}.Deserialize<TokenSnapshot>(reader.ReadToEnd());
            if(next==null||next.rows==null)throw new InvalidDataException();statistics=next;statisticsFileTime=modified;RenderStatistics();
        }
        catch { if(statistics==null)status.Text="尚未读取到统计记录 · 首次扫描可能需要一点时间"; }
    }
    private int Days {get{return periodBox.SelectedIndex==0?1:periodBox.SelectedIndex==2?30:7;}}
    private static string Compact(long value){return value>=1000000?(value/1000000d).ToString("0.##")+"M":value>=1000?(value/1000d).ToString("0.##")+"K":value.ToString("N0");}
    private void RenderStatistics()
    {
        hoveredDay=-1;chartTooltip.Hide(this);
        string first=DateTime.Today.AddDays(1-Days).ToString("yyyy-MM-dd");
        selectedRows=(statistics==null?new TokenRow[0]:statistics.rows).Where(row=>String.CompareOrdinal(row.day,first)>=0&&String.CompareOrdinal(row.day,DateTime.Today.ToString("yyyy-MM-dd"))<=0).ToList();
        table.Rows.Clear();foreach(var row in selectedRows.OrderByDescending(row=>row.day).ThenBy(row=>row.model))table.Rows.Add(row.day,row.model,row.input.ToString("N0"),row.output.ToString("N0"),row.cached.ToString("N0"),row.total.ToString("N0"));
        table.ClearSelection();table.CurrentCell=null;
        if(statistics!=null)status.Text="本机记录，非跨设备账户总量 · "+DateTimeOffset.FromUnixTimeMilliseconds(statistics.updatedAt).ToLocalTime().ToString("MM/dd HH:mm:ss")+" 更新"+(statistics.unreadableFiles>0?" · 部分文件无法读取":"");
        Invalidate();
    }

    // 浅色工作台：统一圆角、留白和色阶，图表只突出有效数据。
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;g.ScaleTransform(statisticsScale,statisticsScale);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using(var icon=RingBitmap(null,null))g.DrawImage(icon,new Rectangle(29,27,34,34));
        DrawLabel(g,"用量统计",76,24,20,ink,true);DrawLabel(g,"TOKEN ANALYTICS  /  本机消耗概览",78,61,8.5f,muted,false);
        Surface(g,new RectangleF(547,28,242,40),10,Color.FromArgb(235,239,245),false);
        long input=selectedRows.Sum(row=>row.input),output=selectedRows.Sum(row=>row.output),cached=selectedRows.Sum(row=>row.cached),total=selectedRows.Sum(row=>row.total);
        string[] values={Compact(total),Compact(input),Compact(output),input>0?(100d*cached/input).ToString("0.0")+"%":"—"};
        string[] labels={"Token 总量","输入 Token","输出 Token","缓存命中率"};
        string[] captions={"所选时段已记录用量","包含缓存命中 Token","模型生成的 Token","缓存命中 / 输入 Token"};
        Color[] colors={Color.FromArgb(13,148,136),Color.FromArgb(71,116,224),Color.FromArgb(145,91,210),Color.FromArgb(217,140,46)};
        for(int i=0;i<4;i++)
        {
            int x=28+i*225;Surface(g,new RectangleF(x,112,209,104),14,i==0?Color.FromArgb(235,248,245):Color.White,true);
            using(var brush=new SolidBrush(colors[i]))g.FillEllipse(brush,x+18,130,6,6);
            DrawLabel(g,labels[i],x+32,123,9.5f,muted,false);DrawLabel(g,statistics==null?"—":values[i],x+17,144,25,ink,true);
            DrawLabel(g,captions[i],x+19,190,8,muted,false);
        }
        Surface(g,new RectangleF(28,234,884,230),14,Color.White,true);
        DrawLabel(g,"每日用量趋势",48,249,11,ink,true);
        DrawLabel(g,DateTime.Today.AddDays(1-Days).ToString("MM.dd")+" — "+DateTime.Today.ToString("MM.dd")+"  ·  本机时区",48,274,8.5f,muted,false);
        Surface(g,new RectangleF(674,244,204,38),9,Color.FromArgb(244,247,250),false);
        var amounts=new List<long>();var days=new List<string>();
        for(int i=0;i<Days;i++){string day=DateTime.Today.AddDays(i+1-Days).ToString("yyyy-MM-dd");days.Add(day);amounts.Add(selectedRows.Where(row=>row.day==day).Sum(row=>metricBox.SelectedIndex==1?row.input:metricBox.SelectedIndex==2?row.output:row.total));}
        long maximum=Math.Max(1,amounts.Max());float left=86,top=314,width=802,height=105,slot=width/Days;
        for(int line=0;line<3;line++){float y=top+line*height/2;using(var pen=new Pen(Color.FromArgb(237,241,245))){if(line<2)pen.DashStyle=DashStyle.Dash;g.DrawLine(pen,left,y,left+width,y);}DrawLabel(g,Compact(maximum*(2-line)/2),45,y-7,8,muted,false);}
        for(int i=0;i<amounts.Count;i++)
        {
            float h=(float)((double)amounts[i]/maximum*height);float barWidth=Math.Min(44,slot*0.55f);float x=left+i*slot+(slot-barWidth)/2;
            if(h>0)using(var bar=Rounded(new RectangleF(x,top+height-h,barWidth,Math.Max(h,1)),Math.Min(5,h/2)))using(var brush=new LinearGradientBrush(new PointF(0,top),new PointF(0,top+height),Color.FromArgb(45,190,175),Color.FromArgb(13,148,136)))g.FillPath(brush,bar);
            if(Days<=7||i%5==0||i==Days-1)DrawLabel(g,DateTime.ParseExact(days[i],"yyyy-MM-dd",CultureInfo.InvariantCulture).ToString("MM/dd"),left+i*slot+(slot-30)/2,432,8,muted,false);
        }
        DrawLabel(g,"每日明细",32,482,11,ink,true);DrawLabel(g,"按日期与模型归并",111,485,8.5f,muted,false);
        DrawLabel(g,selectedRows.Count+" 条汇总记录",815,485,8,muted,false);
        Surface(g,new RectangleF(28,514,884,154),12,Color.White,true);
    }
    private static GraphicsPath Rounded(RectangleF bounds,float radius)
    {
        var path=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(bounds.Width,bounds.Height));
        if(d<1){path.AddRectangle(bounds);return path;}
        path.AddArc(bounds.Left,bounds.Top,d,d,180,90);path.AddArc(bounds.Right-d,bounds.Top,d,d,270,90);path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);path.AddArc(bounds.Left,bounds.Bottom-d,d,d,90,90);path.CloseFigure();return path;
    }
    private static void Surface(Graphics g,RectangleF bounds,float radius,Color color,bool shadow)
    {
        if(shadow)using(var path=Rounded(new RectangleF(bounds.X,bounds.Y+2,bounds.Width,bounds.Height),radius))using(var brush=new SolidBrush(Color.FromArgb(12,29,52,79)))g.FillPath(brush,path);
        using(var path=Rounded(bounds,radius))using(var brush=new SolidBrush(color))g.FillPath(brush,path);
    }
    private static void DrawLabel(Graphics graphics,string text,float x,float y,float size,Color color,bool bold)
    {using(var font=new Font("Microsoft YaHei UI",size*96f/72f,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))using(var brush=new SolidBrush(color))graphics.DrawString(text,font,brush,x,y);}

    // 按柱所在日期命中，展示精确值；不会将费用或请求数混入 Token 统计。
    private void ShowChartTooltip(object sender,MouseEventArgs args)
    {
        float x=args.X/statisticsScale,y=args.Y/statisticsScale;
        int day=x>=86&&x<888&&y>=305&&y<=426?(int)((x-86)/(802f/Days)):-1;
        if(day==hoveredDay)return;hoveredDay=day;chartTooltip.Hide(this);
        if(day<0||day>=Days||statistics==null)return;
        string date=DateTime.Today.AddDays(day+1-Days).ToString("yyyy-MM-dd");var rows=selectedRows.Where(row=>row.day==date).ToList();
        long total=rows.Sum(row=>row.total),input=rows.Sum(row=>row.input),output=rows.Sum(row=>row.output),cached=rows.Sum(row=>row.cached);
        string text=date+"\nToken 总量："+total.ToString("N0")+"\n输入："+input.ToString("N0")+"\n输出："+output.ToString("N0")+"\n缓存命中："+cached.ToString("N0");
        chartTooltip.Show(text,this,Math.Min(args.X+16,ClientSize.Width-(int)(210*statisticsScale)),Math.Max(0,args.Y-(int)(120*statisticsScale)),15000);
    }

    // 预览数据仅用于离屏布局检查，不会写入真实统计文件。
    private void LoadPreviewStatistics()
    {
        var rows=new List<TokenRow>();for(int i=0;i<7;i++)rows.Add(new TokenRow {day=DateTime.Today.AddDays(i-6).ToString("yyyy-MM-dd"),model="示例模型",input=1000000+i*230000,output=100000+i*10000,cached=800000+i*180000,total=1100000+i*240000});
        statistics=new TokenSnapshot {rows=rows.ToArray(),updatedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};RenderStatistics();status.Text="界面预览 · 示例数据，非实际账户统计";
    }
}
