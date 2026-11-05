using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

// 常驻托盘程序：额度采集独立于客户端启动入口，设置窗口可隐藏和重新打开。
internal sealed partial class UsageCardManager : Form
{
    private static readonly string DirectoryPath = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string SettingsPath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly string StartupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Codex用量统计.lnk");
    private static readonly string PowerShellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
    private static readonly string LegacyArguments = "-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -WindowStyle Hidden -File \"" + Path.Combine(DirectoryPath,"launch-hidden.ps1") + "\"";
    private static readonly Color Cyan = Color.FromArgb(35,225,242), Pink = Color.FromArgb(247,78,190), Background = Color.FromArgb(10,15,27);
    private readonly CheckBox sidebarBox = new CheckBox(), startupBox = new CheckBox();
    private readonly Label status = new Label();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly ToolStripMenuItem fiveItem = new ToolStripMenuItem(), weekItem = new ToolStripMenuItem(), autoItem = new ToolStripMenuItem("开机自启"), sidebarItem = new ToolStripMenuItem("侧栏用量卡");
    private readonly EventWaitHandle showEvent;
    private Process worker;
    private CodexRingOverlay ringOverlay;
    private DateTime nextWorkerStart=DateTime.MinValue;
    private bool? lastClientState;
    private int clientCheckTicks;
    private bool waitingForClient=true;
    private Icon currentIcon;
    private int? five, week;
    private long? fiveReset, weekReset;
    private int? resetCardCount;private long? resetCardExpiresAt;private long?[] resetCardExpirations=new long?[0];
    private long updatedAt;
    private bool exiting, preview;
    private static bool smokeTest;
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle,int attribute,ref int value,int size);

    // trayOnly 用于登录启动，仅驻留托盘，不自动打开客户端或设置窗口。
    public UsageCardManager(bool trayOnly, bool previewOnly, EventWaitHandle signal)
    {
        preview=previewOnly;showEvent=signal;
        InitializeStatistics();
        FormClosing+=(s,e)=>{if(!exiting){e.Cancel=true;Hide();}};
        HandleCreated+=(s,e)=>{try{int dark=0;DwmSetWindowAttribute(Handle,20,ref dark,4);}catch{}};
        if(preview){UpdateDisplay();LoadPreviewStatistics();return;}
        var menu=new ContextMenuStrip {RenderMode=ToolStripRenderMode.System,BackColor=SystemColors.Menu,ForeColor=SystemColors.MenuText,ShowImageMargin=false,ShowCheckMargin=true,Font=new Font("Microsoft YaHei UI",10)};
        menu.Items.Add("显示统计",null,(sender,args)=>ShowDetails());menu.Items.Add(new ToolStripSeparator());
        autoItem.Checked=HasOwnedStartup();autoItem.Click+=(sender,args)=>{try{SetStartup(!autoItem.Checked);autoItem.Checked=HasOwnedStartup();}catch(Exception error){MessageBox.Show(error.Message,"无法保存自启设置");}};
        menu.Items.Add(autoItem);menu.Items.Add(new ToolStripSeparator());menu.Items.Add("退出",null,(sender,args)=>ExitTray());
        tray.ContextMenuStrip=menu;menu.Opening+=(sender,args)=>{try{autoItem.Checked=HasOwnedStartup();}catch{}};
        tray.MouseClick+=(s,e)=>{if(e.Button==MouseButtons.Left)ShowDetails();};
        UpdateDisplay();tray.Visible=true;if(!smokeTest)ringOverlay=new CodexRingOverlay();StartWorker();
        timer.Interval=1000;timer.Tick+=(s,e)=>{if(showEvent!=null&&showEvent.WaitOne(0))ShowDetails();if(++clientCheckTicks%5==0)SignalClientState();ReadSnapshot();ReadStatistics();if((worker==null||worker.HasExited)&&DateTime.UtcNow>=nextWorkerStart)StartWorker();};timer.Start();
        if(!trayOnly && lastClientState==true)ShowDetails();
    }

    // 百分比仅接受 0..100，未知值保留为空，不伪装为额度耗尽。
    private static int? Percent(Dictionary<string,object> data,string key)
    {
        object raw;if(!data.TryGetValue(key,out raw)||raw==null||raw is bool||raw is string)return null;
        double number=Convert.ToDouble(raw);return Double.IsNaN(number)||number<0||number>100?(int?)null:(int)Math.Round(number);
    }
    private static long? Timestamp(Dictionary<string,object> data,string key)
    {object raw;return data.TryGetValue(key,out raw)&&raw!=null?Convert.ToInt64(raw):(long?)null;}
    private static string Value(int? number){return number.HasValue?number.Value+"%":"—";}
    private static string Reset(long? timestamp)
    {try{return timestamp.HasValue?DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).ToLocalTime().ToString("MM/dd HH:mm")+" 重置":"重置时间不可用";}catch{return "重置时间不可用";}}

    // 读取仅含额度的本地快照；超过 150 秒的数据不作为当前额度展示。
    private void ReadSnapshot()
    {
        try
        {
            Dictionary<string,object> data; using(var stream=new FileStream(Path.Combine(DirectoryPath,"quota-state.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) using(var reader=new StreamReader(stream,Encoding.UTF8)) data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
            updatedAt=Timestamp(data,"updatedAt")??0;waitingForClient=data.ContainsKey("status")&&(string)data["status"]=="WAITING_FOR_CODEX";
            if(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-updatedAt>150000)throw new InvalidDataException();
            five=Percent(data,"fiveHour");week=Percent(data,"weekly");fiveReset=Timestamp(data,"fiveResetAt");weekReset=Timestamp(data,"weekResetAt");object raw;resetCardCount=data.TryGetValue("resetCardCount",out raw)&&raw is int&&(int)raw>=0?(int?)raw:null;resetCardExpiresAt=Timestamp(data,"resetCardExpiresAt");object list;resetCardExpirations=data.TryGetValue("resetCardExpirations",out list)&&list is System.Collections.IEnumerable?((System.Collections.IEnumerable)list).Cast<object>().Select(value=>value==null?(long?)null:Convert.ToInt64(value)).ToArray():new long?[0];
        }
        catch{if(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-updatedAt>150000){five=null;week=null;fiveReset=null;weekReset=null;resetCardCount=null;resetCardExpiresAt=null;resetCardExpirations=new long?[0];}}
        UpdateDisplay();if(ringOverlay!=null)ringOverlay.UpdateValues(five,week,fiveReset,weekReset,resetCardCount,resetCardExpiresAt,resetCardExpirations);
    }

    // 图标用透明底的青色外环与品红内环，分别表示 5 小时和每周剩余额度。
    private static Bitmap RingBitmap(int? unusedFive,int? unusedWeek)
    {
        var bitmap=new Bitmap(64,64);using(var g=Graphics.FromImage(bitmap))
        {g.SmoothingMode=SmoothingMode.AntiAlias;using(var background=new SolidBrush(Color.FromArgb(12,22,38)))g.FillRectangle(background,2,2,60,60);
         using(var border=new Pen(Color.FromArgb(48,212,229),2))g.DrawLines(border,new Point[]{new Point(8,24),new Point(8,8),new Point(30,8)});
         using(var cyan=new SolidBrush(Color.FromArgb(40,214,231))){g.FillRectangle(cyan,15,36,8,16);g.FillRectangle(cyan,29,26,8,26);g.FillRectangle(cyan,43,15,8,37);}
         using(var pink=new SolidBrush(Color.FromArgb(244,89,180)))g.FillRectangle(pink,43,11,8,4);}
        return bitmap;
    }
    private static void DrawRing(Graphics graphics,Rectangle bounds,float width,Color color,int? percent)
    {
        using(var track=new Pen(Color.FromArgb(76,105,122),width))graphics.DrawEllipse(track,bounds);
        if(percent.HasValue&&percent.Value>0)using(var pen=new Pen(color,width)){pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;graphics.DrawArc(pen,bounds,-90,percent.Value*3.6f);}
    }
    private void UpdateDisplay()
    {
        string description=waitingForClient?"等待 Codex 启动 · Token 统计可查看":"5 小时剩余 "+Value(five)+" / 每周剩余 "+Value(week);
        if(currentIcon==null)using(var bitmap=RingBitmap(null,null))
        {IntPtr native=bitmap.GetHicon();try{currentIcon=(Icon)Icon.FromHandle(native).Clone();}finally{DestroyIcon(native);}tray.Icon=currentIcon;Icon=currentIcon;}
        tray.Text=description;
    }

    // 左键只显示统计，不再提供冗余的侧栏设置面板。
    private void ShowDetails(){ReadStatistics();Show();WindowState=FormWindowState.Normal;Activate();}

    private static bool ReadEnabled(string filePath)
    {var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(filePath,Encoding.UTF8));if(data==null||!data.ContainsKey("enabled")||!(data["enabled"] is bool))throw new InvalidDataException("设置格式无效。");return (bool)data["enabled"];}
    private static void WriteEnabled(string filePath,bool enabled)
    {string temporary=filePath+".tmp";File.WriteAllText(temporary,enabled?"{\"enabled\":true}":"{\"enabled\":false}",new UTF8Encoding(false));if(File.Exists(filePath))File.Replace(temporary,filePath,null);else File.Move(temporary,filePath);}

    // 兼容旧版创建的启动项，只将属于本安装目录的项迁移成托盘启动。
    private static bool HasOwnedStartup()
    {if(smokeTest)return false;if(!File.Exists(StartupPath))return false;dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic link=shell.CreateShortcut(StartupPath);
     bool current=String.Equals((string)link.TargetPath,Application.ExecutablePath,StringComparison.OrdinalIgnoreCase)&&(string)link.Arguments=="--tray";
     bool legacy=String.Equals((string)link.TargetPath,PowerShellPath,StringComparison.OrdinalIgnoreCase)&&(string)link.Arguments==LegacyArguments;
     if(!current&&!legacy)throw new SecurityException("同名启动项不属于本程序，未修改。");return true;}
    private static void SetStartup(bool enabled)
    {if(smokeTest)throw new InvalidOperationException("自检不修改启动项。");bool exists=HasOwnedStartup();if(!enabled){if(exists)File.Delete(StartupPath);return;}
     dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic link=shell.CreateShortcut(StartupPath);link.TargetPath=Application.ExecutablePath;link.Arguments="--tray";link.WorkingDirectory=DirectoryPath;link.Description="Codex用量统计";link.Save();}
    // 只创建本程序持有的采集进程；标准输入 stop 保证内置组件正常退出。
    // 通知后台客户端状态；只观察进程，不启动或结束 Codex。
    private void SignalClientState()
    {
        if(worker==null||worker.HasExited)return;bool running=false;string clientPath=null;
        foreach(var process in Process.GetProcessesByName("ChatGPT"))
        {try{string file=process.MainModule.FileName;running=file.IndexOf(@"\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0&&file.EndsWith(@"\app\ChatGPT.exe",StringComparison.OrdinalIgnoreCase);if(running){clientPath=file;break;}}catch{}finally{process.Dispose();}}
        if(lastClientState==running)return;try{worker.StandardInput.WriteLine(running?"client-running\t"+clientPath:"client-stopped");worker.StandardInput.Flush();lastClientState=running;}catch{}
    }
    private void StartWorker()
    {if(smokeTest)return;nextWorkerStart=DateTime.UtcNow.AddSeconds(30);if(worker!=null){worker.Dispose();worker=null;}try{worker=Process.Start(new ProcessStartInfo(File.Exists(Path.Combine(DirectoryPath,"runtime","node.exe"))?Path.Combine(DirectoryPath,"runtime","node.exe"):"node.exe","\""+Path.Combine(DirectoryPath,"quota-worker.cjs")+"\""){WorkingDirectory=DirectoryPath,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true});lastClientState=null;SignalClientState();}
     catch{status.Text="未能启动额度采集，请检查 Node.js 24+。";}}
    private void ExitTray()
    {exiting=true;timer.Stop();if(ringOverlay!=null)ringOverlay.Dispose();tray.Visible=false;tray.Dispose();if(worker!=null){try{if(!worker.HasExited){worker.StandardInput.WriteLine("stop");worker.StandardInput.Flush();if(!worker.WaitForExit(1500))worker.Kill();}}catch{}worker.Dispose();}
     if(currentIcon!=null)currentIcon.Dispose();Dispose();Application.ExitThread();}

    [STAThread]
    private static int Main(string[] arguments)
    {
        try
        {
            if(arguments.Length==2&&arguments[0]=="--self-test")
            {Directory.CreateDirectory(arguments[1]);string file=Path.Combine(arguments[1],"settings-test.json");WriteEnabled(file,true);if(!ReadEnabled(file))throw new Exception("enable");WriteEnabled(file,false);if(ReadEnabled(file))throw new Exception("disable");
             var data=new Dictionary<string,object>{{"value",0}};if(Percent(data,"value")!=0)throw new Exception("zero");data["value"]=101;if(Percent(data,"value")!=null)throw new Exception("bounds");data["value"]=null;if(Percent(data,"value")!=null)throw new Exception("unknown");
             using(var image=RingBitmap(null,null))image.Save(Path.Combine(arguments[1],"app-icon.png"));File.WriteAllText(Path.Combine(arguments[1],"manager-self-test.txt"),"PASS: settings, zero/unknown/bounds, static icon rendering. No startup registration.");return 0;}
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            if(arguments.Length==2&&arguments[0]=="--pricing-test"){PricingSelfTest(arguments[1]);return 0;}
            if(arguments.Length==2&&arguments[0]=="--overlay-test")
            {CodexRingOverlay.SelfTest(arguments[1]);return 0;}
            if(arguments.Length==2&&arguments[0]=="--dpi-test")
            {
                using(var form=new UsageCardManager(true,true,null))
                {
                    foreach(int dpi in new[]{96,120,144})
                    {
                        form.ApplyStatisticsDpi(dpi);
                        if(form.ClientSize.Width!=(int)Math.Round(940*dpi/96d))throw new Exception("窗口缩放");
                        var button=form.Controls.OfType<StatsTabButton>().First();
                        if(button.Width!=(int)Math.Round(76*dpi/96d))throw new Exception("按钮缩放");
                        form.ShowChartTooltip(form,new MouseEventArgs(MouseButtons.None,0,(int)(130*dpi/96d),(int)(350*dpi/96d),0));
                        if(form.hoveredDay!=0)throw new Exception("柱状图高DPI命中");
                        form.hoveredDay=-1;
                    }
                }
                File.WriteAllText(arguments[1],"PASS: 100/125/150 percent vector layout, segmented control bounds, hover hit testing.");return 0;
            }
            if(arguments.Length==2&&arguments[0]=="--statistics-test")
            {
                using(var form=new UsageCardManager(true,true,null))
                {
                    form.periodBox.SelectedIndex=0;if(form.selectedRows.Count!=1)throw new Exception("今日筛选");
                    form.periodBox.SelectedIndex=1;if(form.selectedRows.Count!=7||form.table.Rows.Count!=7)throw new Exception("7天筛选");
                    form.periodBox.SelectedIndex=2;if(form.selectedRows.Count!=7)throw new Exception("30天筛选");
                    form.metricBox.SelectedIndex=1;form.metricBox.SelectedIndex=2;
                    if(form.Controls.OfType<StatsTabButton>().Count()!=7)throw new Exception("分段按钮");
                }
                File.WriteAllText(arguments[1],"PASS: today/7/30 filtering, table aggregation, metric selection, segmented controls.");return 0;
            }
            if(arguments.Length==2&&arguments[0]=="--smoke")
            {
                smokeTest=true;
                using(var form=new UsageCardManager(true,false,null))
                {
                    if(!form.tray.Visible||form.tray.Icon==null||form.tray.ContextMenuStrip.Items.Count!=5)throw new Exception("托盘或菜单未创建");
                    var close=new FormClosingEventArgs(CloseReason.UserClosing,false);form.OnFormClosing(close);
                    if(!close.Cancel||!form.tray.Visible)throw new Exception("关闭窗口错误地退出托盘");
                    if(form.worker!=null)throw new Exception("自检不应启动采集");
                    form.ExitTray();
                }
                File.WriteAllText(arguments[1],"PASS: NotifyIcon, dual icon, menu, close-to-hide, clean exit. No worker or startup changes.");return 0;
            }
            if(arguments.Length==2&&arguments[0]=="--export-icon")
            {using(var bitmap=RingBitmap(null,null)){IntPtr native=bitmap.GetHicon();try{using(var icon=(Icon)Icon.FromHandle(native).Clone())using(var stream=File.Create(arguments[1]))icon.Save(stream);}finally{DestroyIcon(native);}}return 0;}
            if(arguments.Length==2&&(arguments[0]=="--preview"||arguments[0]=="--preview-150"))
            {using(var form=new UsageCardManager(false,true,null)){if(arguments[0]=="--preview-150")form.ApplyStatisticsDpi(144);using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));
             using(var g=Graphics.FromImage(image))foreach(Control control in form.Controls)using(var child=new Bitmap(control.Width,control.Height)){if(control is StatsTabButton){using(var cg=Graphics.FromImage(child)){cg.Clear(control.BackColor);((StatsTabButton)control).RenderButton(cg);}}else control.DrawToBitmap(child,new Rectangle(Point.Empty,control.Size));int border=(form.Width-form.ClientSize.Width)/2;g.DrawImageUnscaled(child,control.Left+border,control.Top+form.Height-form.ClientSize.Height-border);}
             image.Save(arguments[1]);}}return 0;}
            string suffix=WindowsIdentity.GetCurrent().User.Value;bool created;
            using(var mutex=new Mutex(true,"Local\\CodexQuotaTray-"+suffix,out created))
            using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\CodexQuotaShow-"+suffix))
            {if(!created){if(Array.IndexOf(arguments,"--tray")<0)signal.Set();return 0;}
             using(var form=new UsageCardManager(Array.IndexOf(arguments,"--tray")>=0,false,signal))Application.Run();mutex.ReleaseMutex();}
            return 0;
        }
        catch(Exception error){if(arguments.Length==2&&arguments[0]=="--self-test")File.WriteAllText(Path.Combine(arguments[1],"manager-self-test.txt"),"FAIL: "+error.Message);else if(arguments.Length==2&&arguments[0]=="--overlay-test")File.WriteAllText(Path.Combine(arguments[1],"overlay-test.txt"),"FAIL: "+error.Message);else MessageBox.Show(error.Message,"Codex用量统计",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
    }
}
