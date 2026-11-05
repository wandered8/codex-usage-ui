using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

// 外部吸附浮层：只操作自己的窗口，不修改客户端窗口或启动参数。
internal sealed class CodexRingOverlay : IDisposable
{
    private readonly System.Windows.Forms.Timer tracker=new System.Windows.Forms.Timer();
    private RingWindow floating=new RingWindow();
    private RingWindow details=new RingWindow();
    private IntPtr foreground, target;
    private int? five,week;
    private long? fiveReset,weekReset;
    private int? resetCards;
    private long? resetExpiry;
    private long?[] resetExpirations=new long?[0];
    private int lastDpi;
    private bool dirty=true, disposed, scanning;
    private DateTime nextScan=DateTime.MinValue;
    private DateTime fastScanUntil=DateTime.MinValue;
    private Size geometrySize;
    private float geometryScale=1;
    private List<Rectangle> nativeControls=new List<Rectangle>();
    private Rectangle lastBounds;
    private DateTime retryIdentity=DateTime.MinValue,hoverStarted=DateTime.MinValue;
    private bool hovering,detailsDirty=true;

    public CodexRingOverlay(){tracker.Interval=100;tracker.Tick+=(sender,args)=>{try{Track();}catch{dirty=true;lastBounds=Rectangle.Empty;HideBoth();}};tracker.Start();}
    public void UpdateValues(int? fiveHour,int? weekly,long? fiveResetAt,long? weekResetAt,int? resetCardCount,long? resetCardExpiresAt,long?[] expirations){if(five!=fiveHour||week!=weekly){five=fiveHour;week=weekly;dirty=true;detailsDirty=true;}if(fiveReset!=fiveResetAt||weekReset!=weekResetAt||resetCards!=resetCardCount||resetExpiry!=resetCardExpiresAt||!resetExpirations.SequenceEqual(expirations)){fiveReset=fiveResetAt;weekReset=weekResetAt;resetCards=resetCardCount;resetExpiry=resetCardExpiresAt;resetExpirations=expirations;detailsDirty=true;}}
    private void HideBoth(){if(!floating.IsDisposed)floating.Hide();if(!details.IsDisposed)details.Hide();hovering=false;}

    // 只识别前台官方 Codex 的可见窗口；切换到其他应用即隐藏，避免悬浮在其他程序上。
    private void Track()
    {
        if(disposed)return;
        if(floating.IsDisposed){floating=new RingWindow();dirty=true;lastBounds=Rectangle.Empty;}
        if(details.IsDisposed){details=new RingWindow();detailsDirty=true;}
        if(!floating.IsHandleCreated){dirty=true;lastBounds=Rectangle.Empty;}
        IntPtr active=Native.GetAncestor(Native.GetForegroundWindow(),3);
        // 前台句柄在布局/菜单切换时可能短暂为空，不把这一帧当成客户端消失。
        if(active==IntPtr.Zero&&target!=IntPtr.Zero&&Native.IsWindowVisible(target)&&!Native.IsIconic(target))return;
        if(active!=foreground||(target==IntPtr.Zero&&DateTime.UtcNow>=retryIdentity))
        {
            if(active!=foreground){nativeControls.Clear();geometrySize=Size.Empty;nextScan=DateTime.MinValue;details.Hide();hovering=false;}
            foreground=active;target=IntPtr.Zero;retryIdentity=DateTime.UtcNow.AddMilliseconds(300);
            uint processId;Native.GetWindowThreadProcessId(active,out processId);
            try{using(var process=Process.GetProcessById((int)processId)){string executable=process.MainModule.FileName;
                if(executable.IndexOf(@"\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0&&executable.EndsWith(@"\app\ChatGPT.exe",StringComparison.OrdinalIgnoreCase))target=active;}}
            catch { }
        }
        Native.Rect nativeRect;Native.Point origin=new Native.Point();
        if(target==IntPtr.Zero||!Native.IsWindowVisible(target)||Native.IsIconic(target)||!Native.GetClientRect(target,out nativeRect)||!Native.ClientToScreen(target,ref origin))
        {HideBoth();return;}
        int cloaked=0;Native.DwmGetWindowAttribute(target,14,out cloaked,4);if(cloaked!=0){HideBoth();return;}
        int dpi=(int)Native.GetDpiForWindow(target);if(dpi<=0)dpi=96;float scale=dpi/96f;
        Rectangle client=new Rectangle(origin.X,origin.Y,nativeRect.Right,nativeRect.Bottom);
        if(client.Size!=geometrySize||geometryScale!=scale){
            // 尺寸变化时先按底部锚点投影，避免旧坐标被丢弃后跳到保守回退位置。
            if(!geometrySize.IsEmpty&&nativeControls.Count>0)nativeControls=ProjectControls(nativeControls,geometrySize,client.Size,geometryScale,scale);
            geometrySize=client.Size;geometryScale=scale;nextScan=DateTime.MinValue;fastScanUntil=DateTime.UtcNow.AddSeconds(1);
            details.Hide();hovering=false;
        }
        if(DateTime.UtcNow>=nextScan&&!scanning)ScanNativeControls(target,client,scale);
        Size size=new Size((int)Math.Ceiling(34*scale),(int)Math.Ceiling(133*scale));
        Rectangle bounds=Place(client,nativeControls,size,scale);
        if(bounds.Top<client.Top+(int)(60*scale)){HideBoth();return;}
        if(lastDpi!=dpi||dirty){using(var bitmap=Render(five,week,dpi))floating.SetBitmap(bitmap);lastDpi=dpi;dirty=false;}
        floating.Attach(target);
        if(bounds!=lastBounds){floating.Bounds=bounds;lastBounds=bounds;}
        if(!floating.Visible)floating.Show();
        floating.EnsureVisible();
        TrackHover(bounds,dpi);
    }

    // 仅查询原生按钮几何，不读取名称、文本、聊天或账户；单个后台查询避免阻塞界面。
    private void ScanNativeControls(IntPtr handle,Rectangle client,float scale)
    {
        scanning=true;nextScan=DateTime.UtcNow.AddMilliseconds(DateTime.UtcNow<fastScanUntil?120:4000);
        Task.Run(()=>{
            var result=new List<Rectangle>();
            try {
                var root=AutomationElement.FromHandle(handle);
                var buttons=root.FindAll(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.IsOffscreenProperty,false)));
                foreach(AutomationElement element in buttons){var r=element.Current.BoundingRectangle;
                    if(!r.IsEmpty&&r.Width>=8*scale&&r.Height>=8*scale&&r.Height<=72*scale&&r.Top>=client.Top&&r.Bottom<=client.Bottom+2&&r.Left>=client.Left-2&&r.Right<=client.Left+58*scale)
                        result.Add(new Rectangle((int)Math.Floor(r.Left-client.Left),(int)Math.Floor(r.Top-client.Top),(int)Math.Ceiling(r.Width),(int)Math.Ceiling(r.Height)));}
            } catch { }
            // 空/异常快照不覆盖已确认的按钮几何，避免提供程序短暂失效引起跳动。
            try{floating.BeginInvoke(new Action(()=>{
                scanning=false;if(target!=handle)return;
                Native.Rect current;var point=new Native.Point();
                if(!Native.GetClientRect(handle,out current)||!Native.ClientToScreen(handle,ref point))return;
                if(point.X!=client.X||point.Y!=client.Y||current.Right!=client.Width||current.Bottom!=client.Height){nextScan=DateTime.MinValue;return;}
                // 最大化动画中可能先返回旧页坐标；保留已投影到新底边的按钮，直到读到新底部锚点。
                bool hasCachedFooter=nativeControls.Any(r=>r.Top>client.Height/2&&client.Height-r.Bottom<=80*scale);
                bool hasFreshFooter=result.Any(r=>r.Top>client.Height/2&&client.Height-r.Bottom<=80*scale);
                if(result.Count>0&&(!hasCachedFooter||hasFreshFooter)){nativeControls=result;geometrySize=client.Size;geometryScale=scale;}
                if(!disposed)Track();
            }));}
            catch { scanning=false; }
        });
    }

    // 顶部按钮保持顶部锚定，连续的底部按钮保持距窗口底边的距离；支持跨 DPI 切换。
    internal static List<Rectangle> ProjectControls(IEnumerable<Rectangle> controls,Size before,Size after,float beforeScale,float afterScale)
    {
        var items=controls.ToList();var bottom=new HashSet<Rectangle>();
        var ordered=items.Where(r=>r.Top>before.Height/2).OrderByDescending(r=>r.Bottom).ToList();
        if(ordered.Count>0&&before.Height-ordered[0].Bottom<=80*beforeScale){
            int top=ordered[0].Top;bottom.Add(ordered[0]);
            foreach(var rectangle in ordered.Skip(1)){if(top-rectangle.Bottom>20*beforeScale)break;bottom.Add(rectangle);top=Math.Min(top,rectangle.Top);}
        }
        float ratio=afterScale/beforeScale;float heightChange=after.Height-before.Height*ratio;
        return items.Select(r=>new Rectangle((int)Math.Round(r.X*ratio),(int)Math.Round(r.Y*ratio+(bottom.Contains(r)?heightChange:0)),(int)Math.Round(r.Width*ratio),(int)Math.Round(r.Height*ratio))).ToList();
    }

    // 始终在窗口内部左栏；紧贴实际原生底部按钮，不再回退到窗口外侧。
    internal static Rectangle Place(Rectangle client,IEnumerable<Rectangle> controls,Size size,float scale)
    {
        int gap=Math.Max(3,(int)Math.Round(4*scale));int left=client.Left+(int)Math.Round((52*scale-size.Width)/2);
        var local=controls.Where(r=>r.Width>=8*scale&&r.Height>=8*scale&&r.Height<=72*scale&&r.Top>=0&&r.Bottom<=client.Height+2).ToList();
        int anchor=client.Height-(int)Math.Round(44*scale);
        var lower=local.Where(r=>r.Top>client.Height/2&&r.Bottom<=client.Height+2).OrderByDescending(r=>r.Bottom).ToList();
        if(lower.Count>0){anchor=lower[0].Top;foreach(var r in lower.Skip(1)){if(anchor-r.Bottom<=20*scale)anchor=Math.Min(anchor,r.Top);else break;}}
        else anchor-=(int)Math.Round(88*scale);
        int top=anchor-size.Height-gap;
        for(int attempt=0;attempt<=local.Count;attempt++){
            var collisions=local.Where(r=>r.Left<left-client.Left+size.Width+gap&&r.Right>left-client.Left-gap&&r.Top<top+size.Height+gap&&r.Bottom>top-gap).ToList();
            if(collisions.Count==0)break;top=collisions.Min(r=>r.Top)-size.Height-gap;}
        return new Rectangle(left,client.Top+top,size.Width,size.Height);
    }

    // 浮层鼠标穿透，因此通过屏幕坐标检测悬浮，不拦截客户端点击。
    private void TrackHover(Rectangle bounds,int dpi)
    {
        Point cursor=Cursor.Position;
        if(!bounds.Contains(cursor)&&(!details.Visible||!details.Bounds.Contains(cursor))){details.Hide();hovering=false;return;}
        if(!hovering){hovering=true;hoverStarted=DateTime.UtcNow;}
        if((DateTime.UtcNow-hoverStarted).TotalMilliseconds<300)return;
        if(!details.IsHandleCreated)detailsDirty=true;
        if(detailsDirty||details.Width!=(int)Math.Ceiling(236*dpi/96f)){using(var bitmap=RenderDetails(five,week,fiveReset,weekReset,resetCards,resetExpiry,resetExpirations,dpi))details.SetBitmap(bitmap);detailsDirty=false;}
        Rectangle work=Screen.FromRectangle(bounds).WorkingArea;
        details.Bounds=DetailBounds(bounds,details.Size,work,Math.Max(4,(int)(8*dpi/96f)));
        details.Attach(target);if(!details.Visible)details.Show();details.EnsureVisible();
    }
    internal static Rectangle DetailBounds(Rectangle card,Size size,Rectangle work,int gap)
    {int x=card.Right+gap;if(x+size.Width>work.Right)x=card.Left-size.Width-gap;return new Rectangle(Math.Max(work.Left,Math.Min(x,work.Right-size.Width)),Math.Max(work.Top,Math.Min(card.Top+(card.Height-size.Height)/2,work.Bottom-size.Height)),size.Width,size.Height);}
    private static string ResetText(long? timestamp){try{return timestamp.HasValue?DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).ToLocalTime().ToString("MM/dd HH:mm")+" 重置":"重置时间暂不可用";}catch{return "重置时间暂不可用";}}
    private static string ExpiryText(long? timestamp){try{return timestamp.HasValue?"最近到期："+DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).ToLocalTime().ToString("MM/dd HH:mm"):"到期时间暂不可用";}catch{return "到期时间暂不可用";}}
    internal static Bitmap RenderDetails(int? five,int? week,long? fiveAt,long? weekAt,int? cards,long? expiry,long?[] expirations,int dpi)
    {
        int lines=cards>0?Math.Max(1,expirations.Length)+(cards>expirations.Length&&expirations.Length>0?1:0):0;
        float scale=dpi/96f;int height=178+lines*17;var bitmap=new Bitmap((int)Math.Ceiling(236*scale),(int)Math.Ceiling(height*scale),PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(bitmap)){
            g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using(var shape=Round(new RectangleF(1,1,233,height-3),12)){using(var background=new SolidBrush(Color.FromArgb(252,255,255,255)))g.FillPath(background,shape);using(var border=new Pen(Color.FromArgb(215,224,231),1))g.DrawPath(border,shape);}
            DrawText(g,"Codex 剩余额度",16,13,12,Color.FromArgb(37,49,58),true);
            DrawText(g,"5 小时额度",16,45,10,Color.FromArgb(101,115,124),false);
            DrawText(g,five.HasValue?five+"%":"—",174,41,18,Color.FromArgb(36,148,214),true);
            DrawText(g,ResetText(fiveAt),16,66,9,Color.FromArgb(101,115,124),false);
            using(var pen=new Pen(Color.FromArgb(233,238,242)))g.DrawLine(pen,16,88,220,88);
            DrawText(g,"每周额度",16,99,10,Color.FromArgb(101,115,124),false);
            DrawText(g,week.HasValue?week+"%":"—",174,95,18,Color.FromArgb(26,170,109),true);
            DrawText(g,ResetText(weekAt),16,122,9,Color.FromArgb(101,115,124),false);
            using(var pen=new Pen(Color.FromArgb(233,238,242)))g.DrawLine(pen,16,143,220,143);
            DrawText(g,cards>0?"可用重置卡："+cards.Value+" 张":cards==0?"暂无可用重置卡":"重置卡信息暂不可用",16,153,9,Color.FromArgb(101,115,124),false);
            if(cards>0){
                for(int index=0;index<expirations.Length;index++)DrawText(g,"第 "+(index+1)+" 张："+ExpiryText(expirations[index]).Replace("最近到期：","")+ (expirations[index].HasValue?" 到期":""),16,172+index*17,8.5f,Color.FromArgb(119,130,139),false);
                if(expirations.Length==0)DrawText(g,"到期信息暂不可用",16,172,8.5f,Color.FromArgb(119,130,139),false);
                else if(cards>expirations.Length)DrawText(g,"其余 "+(cards.Value-expirations.Length)+" 张到期信息未返回",16,172+expirations.Length*17,8.5f,Color.FromArgb(119,130,139),false);
            }
        }return bitmap;
    }
    private static void DrawText(Graphics g,string text,float x,float y,float size,Color color,bool bold)
    {using(var font=new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))using(var brush=new SolidBrush(color))g.DrawString(text,font,brush,x,y);}

    // 保留原来上下两个独立圆环、百分数和 5h/周额度标签，按实际 DPI 绘制透明像素。
    internal static Bitmap Render(int? five,int? week,int dpi)
    {
        float scale=dpi/96f;var bitmap=new Bitmap((int)Math.Ceiling(34*scale),(int)Math.Ceiling(133*scale),PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(bitmap)){
            g.Clear(Color.Transparent);g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using(var path=Round(new RectangleF(0.5f,0.5f,33,132),10)){
                using(var background=new SolidBrush(Color.FromArgb(220,247,250,251)))g.FillPath(background,path);
                using(var border=new Pen(Color.FromArgb(35,82,100,116),0.8f))g.DrawPath(border,path);}
            Meter(g,7,five,"5h额度");Meter(g,70,week,"周额度");
        }return bitmap;
    }
    private static void Meter(Graphics g,int top,int? value,string label)
    {
        Color color=value.HasValue?(value.Value>=75?Color.FromArgb(34,197,125):value.Value>=25?Color.FromArgb(52,153,222):Color.FromArgb(225,151,47)):Color.FromArgb(144,158,168);
        using(var track=new Pen(Color.FromArgb(219,229,233),3.3f))g.DrawEllipse(track,5,top,24,24);
        if(value.HasValue&&value.Value>0)using(var pen=new Pen(color,3.3f)){pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;g.DrawArc(pen,5,top,24,24,-90,Math.Min(100,Math.Max(0,value.Value))*3.6f);}
        using(var alignment=new StringFormat {Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center}){
            using(var font=new Font("Microsoft YaHei UI",11,FontStyle.Bold,GraphicsUnit.Pixel))using(var brush=new SolidBrush(Color.FromArgb(37,49,58)))g.DrawString(value.HasValue?value.Value+"%":"—",font,brush,new RectangleF(0,top+29,34,15),alignment);
            using(var font=new Font("Microsoft YaHei UI",7.5f,FontStyle.Regular,GraphicsUnit.Pixel))using(var brush=new SolidBrush(Color.FromArgb(101,115,124)))g.DrawString(label,font,brush,new RectangleF(0,top+44,34,11),alignment);
        }
    }
    private static GraphicsPath Round(RectangleF r,float radius){var p=new GraphicsPath();float d=radius*2;p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
    public void Dispose(){disposed=true;tracker.Stop();tracker.Dispose();floating.Dispose();details.Dispose();}

    // 自检仅创建自己的分层测试窗口，不访问或修改真实客户端。
    internal static void SelfTest(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        using(var bitmap=Render(57,93,144)){
            if(bitmap.Width!=51||bitmap.Height!=200)throw new Exception("DPI尺寸");
            bitmap.Save(System.IO.Path.Combine(directory,"overlay-style.png"));
            using(var window=new RingWindow()){
                int style=Native.GetWindowLong(window.Handle,-20);
                int expected=0x80000|0x20|0x80|0x08000000;
                if((style&expected)!=expected)throw new Exception("分层/鼠标穿透/不激活样式");
                window.SetBitmap(bitmap);
            }
        }
        var client=new Rectangle(0,0,800,700);
        var controls=new[]{new Rectangle(8,660,36,36),new Rectangle(8,616,36,36),new Rectangle(8,572,36,36)};
        Rectangle position=Place(client,controls,new Size(34,133),1);
        if(position.Bottom!=568)throw new Exception("原生更新按钮避让");
        Rectangle moved=Place(new Rectangle(150,100,800,700),controls,new Size(34,133),1);
        if(moved.X-position.X!=150||moved.Y-position.Y!=100)throw new Exception("窗口移动跟随");
        var maximized=ProjectControls(controls,new Size(800,700),new Size(1200,1000),1,1);
        var maximizedPosition=Place(new Rectangle(0,0,1200,1000),maximized,new Size(34,133),1);
        if(maximizedPosition.Bottom-position.Bottom!=300)throw new Exception("最大化首帧底部锚定");
        if(!ProjectControls(maximized,new Size(1200,1000),new Size(800,700),1,1).SequenceEqual(controls))throw new Exception("还原首帧底部锚定");
        var scaled=ProjectControls(controls,new Size(800,700),new Size(1200,1050),1,1.5f);
        var scaledPosition=Place(new Rectangle(0,0,1200,1050),scaled,new Size(51,200),1.5f);
        if(scaledPosition.Bottom!=(int)(position.Bottom*1.5))throw new Exception("跨DPI切换底部锚定");
        var withTop=controls.Concat(new[]{new Rectangle(8,60,36,36),new Rectangle(8,400,36,36)}).ToList();
        var withTopProjected=ProjectControls(withTop,new Size(800,700),new Size(1200,1000),1,1);
        if(withTopProjected[3].Top!=60||withTopProjected[4].Top!=400)throw new Exception("顶部按钮不能随底部平移");
        foreach(int dpi in new[]{96,120,144}){
            float scale=dpi/96f;var frame=new Rectangle(400,150,1000,900);var size=new Size((int)Math.Ceiling(34*scale),(int)Math.Ceiling(133*scale));
            Rectangle fallback=Place(frame,new Rectangle[0],size,scale);
            if(fallback.Left<frame.Left||fallback.Right>frame.Left+60*scale||fallback.Bottom>frame.Bottom)throw new Exception("窗口内回退");
        }
        var withoutUpdate=Place(client,new[]{new Rectangle(8,660,36,36)},new Size(34,133),1);
        if(withoutUpdate.Bottom!=656)throw new Exception("紧贴头像");
        var originalQuota=Place(client,new[]{new Rectangle(8,660,36,36),new Rectangle(8,616,36,36)},new Size(34,133),1);
        if(originalQuota.Bottom-position.Bottom!=44)throw new Exception("新增更新按钮必须按实际高度与间距上移");
        var updateGone=Place(client,new[]{new Rectangle(8,660,36,36),new Rectangle(8,616,36,36)},new Size(34,133),1);
        if(updateGone!=originalQuota)throw new Exception("更新按钮消失必须恢复位置");
        var huge=Place(client,new[]{new Rectangle(8,0,36,700)},new Size(34,133),1);
        if(huge.Top<client.Top)throw new Exception("无效几何导致闪隐");
        var tooltipBounds=DetailBounds(new Rectangle(900,600,34,133),new Size(236,156),new Rectangle(0,0,1024,768),8);
        if(tooltipBounds.Right>1024||tooltipBounds.Bottom>768||tooltipBounds.Left<0)throw new Exception("悬浮提示越界");
        using(var tooltip=RenderDetails(79,86,1793870400,1794460800,2,1795070400,new long?[]{1795070400,1795156800},144)){
            if(tooltip.Height!=(178+2*17)*1.5)throw new Exception("多张重置卡逐行展示");
            tooltip.Save(System.IO.Path.Combine(directory,"hover-style.png"));
            using(var window=new RingWindow()){
                window.SetBitmap(tooltip);window.Location=new Point(-32000,-32000);window.EnsureVisible();window.Hide();window.EnsureVisible();
                if(!Native.IsWindowVisible(window.Handle))throw new Exception("原生可见性恢复");
            }
        }
        using(var noCards=RenderDetails(null,null,null,null,0,null,new long?[0],144))if(noCards.Height!=267)throw new Exception("无卡提示收起到期信息");
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"overlay-test.txt"),"PASS: original style, native layered/click-through/no-activate, alpha upload, always-inside fallback at100/125/150, footer/update clearance, invalid bounds filtering, hover render/screen clamping, native visibility restoration. No Codex interaction.");
    }

    // 原生分层工具窗口：不激活、不进任务栏、鼠标穿透，不设置系统级 TopMost。
    private sealed class RingWindow : Form
    {
        private IntPtr owner;
        public RingWindow(){AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;var handle=Handle;}
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x80000|0x20|0x80|0x08000000;return p;}}
        public void Attach(IntPtr target){if(owner==target)return;if(IntPtr.Size==8)Native.SetWindowLongPtr(Handle,-8,target);else Native.SetWindowLong(Handle,-8,target.ToInt32());owner=target;}
        // 修复托管 Visible 与原生可见性/层级不同步，不激活窗口或改变原生 owner 层级。
        public void EnsureVisible(){if(!Native.IsWindowVisible(Handle))Native.ShowWindow(Handle,4);Native.SetWindowPos(Handle,IntPtr.Zero,0,0,0,0,0x213);}
        public void SetBitmap(Bitmap bitmap)
        {
            IntPtr screen=Native.GetDC(IntPtr.Zero),memory=Native.CreateCompatibleDC(screen),image=bitmap.GetHbitmap(Color.FromArgb(0)),old=Native.SelectObject(memory,image);
            try{var size=new Native.Size {Width=bitmap.Width,Height=bitmap.Height};var source=new Native.Point();var blend=new Native.Blend {Operation=0,Flags=0,Alpha=255,Format=1};if(!Native.UpdateLayeredWindow(Handle,screen,IntPtr.Zero,ref size,memory,ref source,0,ref blend,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
            finally{Native.SelectObject(memory,old);Native.DeleteObject(image);Native.DeleteDC(memory);Native.ReleaseDC(IntPtr.Zero,screen);}
        }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)] public struct Point {public int X,Y;}
        [StructLayout(LayoutKind.Sequential)] public struct Size {public int Width,Height;}
        [StructLayout(LayoutKind.Sequential,Pack=1)] public struct Blend {public byte Operation,Flags,Alpha,Format;}
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window,out Rect rect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window,ref Point point);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
        [DllImport("user32.dll",EntryPoint="SetWindowLongW")] public static extern int SetWindowLong(IntPtr window,int index,int value);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] public static extern int GetWindowLong(IntPtr window,int index);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll",SetLastError=true)] public static extern bool UpdateLayeredWindow(IntPtr window,IntPtr destination,IntPtr position,ref Size size,IntPtr source,ref Point sourcePosition,int color,ref Blend blend,int flags);
    }
}
