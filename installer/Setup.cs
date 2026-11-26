using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

// 当前用户安装器：内嵌完整运行文件，不联网、不要求管理员权限。
internal static class Setup
{
    private const string Product="Codex用量统计";
    private const string RegistryPath=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexUsageUI";
    private const string Marker="codex-usage-ui-v1";
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try {
            if(args.Length==2&&args[0]=="--extract"){Extract(Path.GetFullPath(args[1]));return 0;}
            if(args.Length==3&&args[0]=="--update")
            {
                int pid;
                if(!Int32.TryParse(args[1],out pid)||pid<=0)throw new ArgumentException("无效的用量统计进程。");
                return UpdateInstalled(args[2],pid);
            }
            if(args.Contains("--uninstall")||string.Equals(Path.GetFileName(Application.ExecutablePath),"Uninstall.exe",StringComparison.OrdinalIgnoreCase)){Uninstall();return 0;}
            using(var form=new Form {Text=Product+" 安装",ClientSize=new Size(540,230),StartPosition=FormStartPosition.CenterScreen,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,Font=new Font("Microsoft YaHei UI",10)}) {
                var label=new Label {Text="安装 Codex用量统计\n内置运行时，无需额外安装 Node.js 或 CLI。",AutoSize=true,Location=new Point(24,20)};
                var folder=new TextBox {Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","CodexUsageUI"),Location=new Point(24,92),Width=490};
                var desktop=new CheckBox {Text="创建桌面快捷方式",Checked=true,AutoSize=true,Location=new Point(24,132)};
                var install=new Button {Text="安装并启动",Location=new Point(370,177),Size=new Size(144,32)};
                install.Click+=(s,e)=>{install.Enabled=false;try{string destination=Path.GetFullPath(folder.Text);Install(destination,desktop.Checked);Process.Start(new ProcessStartInfo(Path.Combine(destination,Product+".exe")){WorkingDirectory=destination,UseShellExecute=true});form.Close();}catch(Exception error){MessageBox.Show(error.Message,Product,MessageBoxButtons.OK,MessageBoxIcon.Error);install.Enabled=true;}};
                form.Controls.AddRange(new Control[]{label,folder,desktop,install});Application.Run(form);
            }return 0;
        }catch(Exception error){Console.Error.WriteLine(error.Message);MessageBox.Show(error.Message,Product,MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
    }
    // destination 为用户选定目录；拒绝向非本产品的已有非空目录解压。
    private static void Extract(string destination)
    {
        if(Directory.Exists(destination)&&Directory.EnumerateFileSystemEntries(destination).Any()&&(!File.Exists(Path.Combine(destination,".install-id"))||File.ReadAllText(Path.Combine(destination,".install-id"))!=Marker))throw new IOException("请选择空文件夹，或此前安装 Codex用量统计的目录。");
        foreach(var process in Process.GetProcessesByName(Product)){using(process){try{if(string.Equals(process.MainModule.FileName,Path.Combine(destination,Product+".exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("请先从托盘退出 Codex用量统计，再安装更新；无需关闭 Codex。");}catch(System.ComponentModel.Win32Exception){}}}
        Directory.CreateDirectory(destination);string prefix=destination.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))using(var archive=new ZipArchive(stream,ZipArchiveMode.Read))foreach(var entry in archive.Entries){string file=Path.GetFullPath(Path.Combine(destination,entry.FullName));if(!file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("安装包包含非法路径。");if(string.IsNullOrEmpty(entry.Name)){Directory.CreateDirectory(file);continue;}Directory.CreateDirectory(Path.GetDirectoryName(file));entry.ExtractToFile(file,true);}
        File.WriteAllText(Path.Combine(destination,".install-id"),Marker);
    }
    // 等待旧进程退出，持有托盘互斥锁在原目录更新，然后恢复托盘驻留。
    private static int UpdateInstalled(string directory, int oldPid)
    {
        directory=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        string executable=Path.Combine(directory,Product+".exe");
        string marker=Path.Combine(directory,".install-id");
        if(!File.Exists(executable)||!File.Exists(Path.Combine(directory,"quota-worker.cjs"))||
           (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("更新目标必须是现有的 Codex用量统计目录。");
        bool registered=File.Exists(marker);
        if(registered&&File.ReadAllText(marker)!=Marker)
            throw new IOException("安装目录标记不匹配。");

        Process old=null;
        try{old=Process.GetProcessById(oldPid);}catch(ArgumentException){}
        if(old!=null)using(old)
        {
            try
            {
                if(!String.Equals(old.MainModule.FileName,executable,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("更新请求与正在运行的统计程序不匹配。");
            }
            catch(InvalidOperationException){if(!old.HasExited)throw;}
            catch(System.ComponentModel.Win32Exception){if(!old.HasExited)throw;}
            if(!old.WaitForExit(15000))throw new IOException("用量统计尚未退出，请从托盘退出后重试。");
        }

        int result=0;
        string mutexName="Local\\CodexQuotaTray-"+System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
        try
        {
            using(var mutex=new System.Threading.Mutex(false,mutexName))
            {
                bool acquired;
                try{acquired=mutex.WaitOne(5000);}catch(System.Threading.AbandonedMutexException){acquired=true;}
                if(!acquired)throw new IOException("用量统计已重新启动，请重试更新。");
                try
                {
                    Version oldVersion;
                    if(!Version.TryParse(FileVersionInfo.GetVersionInfo(executable).FileVersion,out oldVersion)||
                       Assembly.GetExecutingAssembly().GetName().Version<=oldVersion)
                        throw new IOException("当前安装目录已经是同版或更新版本，或版本无法识别。");
                    ApplyUpdateFiles(directory,registered);
                }
                finally{mutex.ReleaseMutex();}
            }
        }
        catch(Exception error)
        {
            MessageBox.Show(error.Message,Product+"更新失败",MessageBoxButtons.OK,MessageBoxIcon.Error);
            result=1;
        }
        using(var process=Process.Start(new ProcessStartInfo(executable,"--tray")
        {WorkingDirectory=directory,UseShellExecute=false}))
            if(process==null)throw new IOException("无法重新启动用量统计，请从原快捷方式打开。");
        return result;
    }

    // 逐个替换发行文件，失败时按相反顺序还原同一磁盘上的备份。
    private static void ApplyUpdateFiles(string directory, bool registered)
    {
        string stage=Path.Combine(directory,".update-"+Guid.NewGuid().ToString("N"));
        string payload=Path.Combine(stage,"payload"), backup=Path.Combine(stage,"backup");
        string runtime=Path.Combine(directory,"runtime");
        var applied=new System.Collections.Generic.List<Tuple<string,string>>();
        bool preserveBackup=false;
        if(Directory.Exists(runtime)&&(File.GetAttributes(runtime)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("运行时目录指向其他位置，无法更新。");
        try
        {
            Extract(payload);
            string[] files={"runtime\\node.exe","runtime\\LICENSE","agent.cjs","quota-worker.cjs",
                "usage-stats.cjs","usage-icon.ico",Product+".exe.config","LICENSE","THIRD_PARTY_NOTICES.md","README.md"};
            if(registered)files=files.Concat(new[]{"Uninstall.exe","Uninstall.exe.config"}).ToArray();
            files=files.Concat(new[]{Product+".exe"}).ToArray();
            foreach(string name in files)
                if(!File.Exists(Path.Combine(payload,name)))throw new IOException("安装包缺少文件："+name);
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(runtime);

            foreach(string name in files)
            {
                string source=Path.Combine(payload,name), target=Path.Combine(directory,name);
                string saved=null;
                if(File.Exists(target))
                {
                    if((File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0)
                        throw new IOException("无法替换指向其他位置的文件："+name);
                    saved=Path.Combine(backup,applied.Count.ToString());
                    File.Replace(source,target,saved);
                }
                else File.Move(source,target);
                applied.Add(Tuple.Create(target,saved));
            }

            if(registered)
                using(var key=Registry.CurrentUser.OpenSubKey(RegistryPath,true))
                    if(key!=null&&String.Equals(key.GetValue("InstallLocation") as string,directory,StringComparison.OrdinalIgnoreCase))
                        key.SetValue("DisplayVersion",Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
        }
        catch(Exception error)
        {
            for(int index=applied.Count-1;index>=0;index--)
            {
                var entry=applied[index];
                try
                {
                    if(entry.Item2==null)File.Delete(entry.Item1);
                    else File.Replace(entry.Item2,entry.Item1,null);
                }
                catch(IOException){preserveBackup=true;}
                catch(UnauthorizedAccessException){preserveBackup=true;}
            }
            if(preserveBackup)throw new IOException("更新失败且部分文件无法自动还原。备份位于 "+backup,error);
            throw;
        }
        finally
        {
            if(!preserveBackup&&Directory.Exists(stage))
            {
                try{Directory.Delete(stage,true);}
                catch(IOException){}
                catch(UnauthorizedAccessException){}
            }
        }
    }

    // 只注册本产品入口；客户端原有图标与开机自启偏好不变。
    private static void Install(string destination,bool desktop)
    {
        Extract(destination);
        string executable=Path.Combine(destination,Product+".exe");
        string startMenu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),Product+".lnk");CreateShortcut(startMenu,executable,destination);
        if(desktop)CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Product+".lnk"),executable,destination);
        using(var key=Registry.CurrentUser.CreateSubKey(RegistryPath)){key.SetValue("DisplayName",Product);key.SetValue("DisplayVersion",Assembly.GetExecutingAssembly().GetName().Version.ToString(3));key.SetValue("Publisher","wandered8");key.SetValue("InstallLocation",destination);key.SetValue("DisplayIcon",executable);key.SetValue("UninstallString","\""+Path.Combine(destination,"Uninstall.exe")+"\" --uninstall");key.SetValue("NoModify",1);key.SetValue("NoRepair",1);}
    }
    // 快捷方式目标若属于其他安装则不覆盖。
    private static void CreateShortcut(string file,string executable,string directory)
    {
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic link=shell.CreateShortcut(file);
        if(File.Exists(file)&&!string.Equals((string)link.TargetPath,executable,StringComparison.OrdinalIgnoreCase))return;
        link.TargetPath=executable;link.WorkingDirectory=directory;link.IconLocation=Path.Combine(directory,"usage-icon.ico")+",0";link.Save();
    }
    // 卸载前核对标记和进程，只删除本安装目录及属于它的快捷方式。
    private static void Uninstall()
    {
        string directory=AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if(!File.Exists(Path.Combine(directory,".install-id"))||File.ReadAllText(Path.Combine(directory,".install-id"))!=Marker)throw new IOException("安装目录标记不匹配，未删除任何文件。");
        foreach(var process in Process.GetProcessesByName(Product)){using(process){try{if(string.Equals(process.MainModule.FileName,Path.Combine(directory,Product+".exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("请先从托盘退出 Codex用量统计，再卸载。");}catch(System.ComponentModel.Win32Exception){}}}
        if(MessageBox.Show("卸载 Codex用量统计及其本地统计缓存？不会删除 Codex 客户端、登录或会话记录。",Product,MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        foreach(var folder in new[]{Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.Programs,Environment.SpecialFolder.Startup}){string file=Path.Combine(Environment.GetFolderPath(folder),Product+".lnk");if(File.Exists(file)){dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic link=shell.CreateShortcut(file);if(string.Equals((string)link.TargetPath,Path.Combine(directory,Product+".exe"),StringComparison.OrdinalIgnoreCase))File.Delete(file);}}
        Registry.CurrentUser.DeleteSubKeyTree(RegistryPath,false);
        string script=Path.Combine(Path.GetTempPath(),"codex-usage-uninstall-"+Guid.NewGuid().ToString("N")+".ps1");
        string literal=directory.Replace("'","''");
        File.WriteAllText(script,"$ErrorActionPreference='Stop'\nWait-Process -Id "+Process.GetCurrentProcess().Id+" -ErrorAction SilentlyContinue\n$target=Get-Item -LiteralPath '"+literal+"'\nif(($target.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '拒绝删除链接目录'}\nif((Get-Content -LiteralPath (Join-Path $target.FullName '.install-id') -Raw) -ne '"+Marker+"'){throw '目录标记不匹配'}\nRemove-Item -LiteralPath $target.FullName -Recurse -Force\nRemove-Item -LiteralPath $PSCommandPath -Force\n",new System.Text.UTF8Encoding(true));
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+script+"\""){UseShellExecute=false,CreateNoWindow=true});
    }
}
