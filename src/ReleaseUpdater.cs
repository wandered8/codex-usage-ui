using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// 从固定 GitHub 发行渠道获取并验证 Windows 安装包。
internal static class ReleaseUpdater
{
    private const string Repository = "wandered8/codex-usage-ui";
    private const string ReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string AssetBaseUrl = "https://github.com/" + Repository + "/releases/download/";
    private const long MaximumInstallerSize = 200L * 1024 * 1024;
    private static readonly string CachePath = Path.Combine(Path.GetTempPath(), "CodexUsageUI-Updates");
    private static readonly HttpClient Client = CreateClient();

    // 发布脚本将同一个版本写入主程序、安装器和发行包名。
    internal static Version CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return new Version(version.Major, version.Minor, version.Build);
        }
    }

    // 记录本次检查选定的安装包及校验信息。
    internal sealed class Update
    {
        internal Version Version;
        internal Uri DownloadUrl;
        internal long Size;
        internal string Sha256;
    }

    // 对应 GitHub 发行版和附件 JSON 字段。
    public sealed class Release
    {
        public string tag_name { get; set; }
        public bool draft { get; set; }
        public bool prerelease { get; set; }
        public Asset[] assets { get; set; }
    }

    public sealed class Asset
    {
        public string name { get; set; }
        public string browser_download_url { get; set; }
        public string state { get; set; }
        public long size { get; set; }
        public string digest { get; set; }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexUsageUI/" + CurrentVersion);
        return client;
    }

    // 仅检查最新正式发行版。返回空值表示当前版本已是最新。
    internal static async Task<Update> CheckAsync(CancellationToken cancellation)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using (var request = new HttpRequestMessage(HttpMethod.Get, ReleaseUrl))
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using (var response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                        throw new InvalidOperationException("GitHub 暂时限制了检查请求，请稍后重试。");
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return SelectUpdate(new JavaScriptSerializer().Deserialize<Release>(json), CurrentVersion);
                }
            }
        }
    }

    // 将版本、资产名和下载地址绑定到同一个发行标签，并要求 GitHub 提供 SHA-256。
    internal static Update SelectUpdate(Release release, Version current)
    {
        Version version;
        if (release == null || release.draft || release.prerelease ||
            !Regex.IsMatch(release.tag_name ?? "", @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") ||
            !Version.TryParse(release.tag_name.Substring(1), out version))
            throw new InvalidDataException("GitHub 返回的正式发行版信息无效。");

        if (version <= current) return null;

        string name = "CodexUsageUI-" + version + "-Setup-x64.exe";
        string url = AssetBaseUrl + release.tag_name + "/" + name;
        var asset = Array.Find(release.assets ?? new Asset[0], item => item != null && item.name == name);
        if (asset == null || asset.state != "uploaded" || asset.browser_download_url != url ||
            asset.size <= 0 || asset.size > MaximumInstallerSize ||
            !Regex.IsMatch(asset.digest ?? "", @"\Asha256:[a-fA-F0-9]{64}\z"))
            throw new InvalidDataException("新版缺少可校验的 Windows x64 安装包，请稍后重试或查看 GitHub 发行页。");

        return new Update { Version = version, DownloadUrl = new Uri(url), Size = asset.size,
            Sha256 = asset.digest.Substring("sha256:".Length) };
    }

    // 流式下载到独立临时目录，在长度和 SHA-256 都匹配后才交给安装器。
    internal static async Task<string> DownloadAsync(Update update, IProgress<int> progress, CancellationToken cancellation)
    {
        CleanOldDownloads();
        string directory = Path.Combine(CachePath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(CachePath);
        if ((File.GetAttributes(CachePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("临时更新目录指向了其他位置。");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "CodexUsageUI-Setup.exe");
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                using (var response = await OpenDownloadAsync(update.DownloadUrl, timeout.Token).ConfigureAwait(false))
                using (timeout.Token.Register(response.Dispose))
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int lastProgress = -1;
                    int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        total += count;
                        if (total > update.Size) throw new InvalidDataException("安装包大小与发行记录不符。");
                        await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                        int percent = (int)(total * 100 / update.Size);
                        if (percent != lastProgress && progress != null) progress.Report(percent);
                        lastProgress = percent;
                    }
                }
                timeout.Token.ThrowIfCancellationRequested();
            }

            await Task.Run(() => VerifyDownload(file, update), cancellation).ConfigureAwait(false);
            return file;
        }
        catch
        {
            DiscardDownload(file);
            throw;
        }
    }

    // 只允许 GitHub 下载域名之间的 HTTPS 重定向。
    private static async Task<HttpResponseMessage> OpenDownloadAsync(Uri url, CancellationToken cancellation)
    {
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
                (url.Host != "github.com" && url.Host != "release-assets.githubusercontent.com" &&
                 url.Host != "objects.githubusercontent.com"))
                throw new InvalidDataException("安装包下载地址超出 GitHub 发行渠道。");

            var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if ((int)response.StatusCode < 300 || (int)response.StatusCode >= 400)
                    response.EnsureSuccessStatusCode();
                if (response.Headers.Location == null) break;
                url = new Uri(url, response.Headers.Location);
            }
        }
        throw new InvalidDataException("GitHub 安装包重定向无效，请稍后重试。");
    }

    // 校验下载后的磁盘内容，拒绝截断及篡改的安装包。
    internal static void VerifyDownload(string path, Update update)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
        {
            if (stream.Length != update.Size) throw new InvalidDataException("安装包大小与发行记录不符。");
            string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!String.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包 SHA-256 校验失败，请重新下载。");
        }
    }

    // 下载校验成功后启动安装器。安装器等待当前托盘进程退出后更新原目录。
    internal static void StartInstaller(string path, string installationPath)
    {
        using (var current = Process.GetCurrentProcess())
        using (var installer = Process.Start(new ProcessStartInfo(path,
            "--update " + current.Id + " \"" + installationPath.TrimEnd(Path.DirectorySeparatorChar) + "\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path)
        }))
        {
            if (installer == null) throw new InvalidOperationException("无法启动新版安装器。");
        }
    }

    // 取消或启动安装器失败时移除本次下载。
    internal static void DiscardDownload(string file)
    {
        if (file == null) return;
        try
        {
            File.Delete(file);
            Directory.Delete(Path.GetDirectoryName(file));
        }
        catch (IOException) {}
        catch (UnauthorizedAccessException) {}
    }

    // 下次下载时清理超过一天的临时安装器。
    private static void CleanOldDownloads()
    {
        try
        {
            if (!Directory.Exists(CachePath) || (File.GetAttributes(CachePath) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string directory in Directory.EnumerateDirectories(CachePath))
            {
                Guid ignored;
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out ignored) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 &&
                    Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                    DiscardDownload(Path.Combine(directory, "CodexUsageUI-Setup.exe"));
            }
        }
        catch (IOException) {}
        catch (UnauthorizedAccessException) {}
    }

    // 离线验证发行选择、拒绝不可信的附件以及磁盘内容校验。
    internal static void SelfTest(string resultPath)
    {
        var release = new Release { tag_name = "v1.3.0", assets = new[] { new Asset
        {
            name = "CodexUsageUI-1.3.0-Setup-x64.exe",
            browser_download_url = AssetBaseUrl + "v1.3.0/CodexUsageUI-1.3.0-Setup-x64.exe",
            state = "uploaded", size = 3,
            digest = "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        } } };
        var update = SelectUpdate(release, new Version(1, 2, 9));
        if (update == null || update.Version != new Version(1, 3, 0) ||
            SelectUpdate(release, new Version(1, 3, 0)) != null ||
            SelectUpdate(release, new Version(1, 3, 1)) != null)
            throw new Exception("发行版本比较失败。");

        AssertRejected(() => SelectUpdate(new Release { tag_name = "v1.4.0" }, update.Version));
        release.prerelease = true;
        AssertRejected(() => SelectUpdate(release, new Version(1, 2, 9)));
        release.prerelease = false;
        string digest = release.assets[0].digest;
        release.assets[0].digest = null;
        AssertRejected(() => SelectUpdate(release, new Version(1, 2, 9)));
        release.assets[0].digest = digest;
        release.assets[0].browser_download_url = "https://example.org/not-a-release.exe";
        AssertRejected(() => SelectUpdate(release, new Version(1, 2, 9)));

        string temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, "abc");
            VerifyDownload(temp, update);
            File.WriteAllText(temp, "xyz");
            AssertRejected(() => VerifyDownload(temp, update));
            File.WriteAllText(temp, "ab");
            AssertRejected(() => VerifyDownload(temp, update));
        }
        finally { File.Delete(temp); }
        File.WriteAllText(resultPath, "PASS: version, prerelease, asset identity, length, SHA-256.");
    }

    private static void AssertRejected(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("应拒绝无效的更新数据。");
    }
}

// 管理托盘中的更新检查、确认和下载状态。
internal sealed partial class UsageCardManager
{
    private readonly System.Windows.Forms.ToolStripMenuItem updateItem =
        new System.Windows.Forms.ToolStripMenuItem("检查更新…");
    private readonly LinkLabel updateLink = new LinkLabel
    {
        Text = "检查更新…",
        TextAlign = System.Drawing.ContentAlignment.MiddleRight,
        LinkColor = System.Drawing.Color.FromArgb(15, 118, 110),
        Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5f),
        Enabled = false
    };
    private readonly CancellationTokenSource updateCancellation = new CancellationTokenSource();
    private readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
    private ReleaseUpdater.Update availableUpdate;
    private Version notifiedUpdate;

    // 启动后稍候检查一次，之后每六小时检查。安装始终由用户确认。
    private void InitializeUpdateMenu(System.Windows.Forms.ContextMenuStrip menu)
    {
        menu.Items.Add(updateItem);
        updateItem.Click += async (sender, args) => await RequestUpdateAsync();
        updateLink.LinkClicked += async (sender, args) => await RequestUpdateAsync();
        updateLink.Enabled = true;

        if (smokeTest) return;
        updateTimer.Interval = 5000;
        updateTimer.Tick += async (sender, args) =>
        {
            updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
            if (updateItem.Enabled) await CheckForUpdateAsync(false);
        };
        updateTimer.Start();
    }

    // 托盘和统计窗口进入同一个更新流程。
    private Task RequestUpdateAsync()
    {
        return availableUpdate == null ? CheckForUpdateAsync(true) : OfferUpdateAsync();
    }

    // 在后台检查正式发行版，托盘保持可用，手动检查时显示详细结果。
    private async Task CheckForUpdateAsync(bool userRequested)
    {
        SetUpdateControls("正在检查更新…", false);
        try
        {
            availableUpdate = await ReleaseUpdater.CheckAsync(updateCancellation.Token);
            if (exiting) return;
            if (availableUpdate == null)
            {
                if (userRequested) MessageBox.Show(Visible ? this : null, "当前 v" + ReleaseUpdater.CurrentVersion + " 已是最新版本。",
                    "Codex用量统计更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (userRequested) await OfferUpdateAsync();
            else if (notifiedUpdate != availableUpdate.Version)
            {
                notifiedUpdate = availableUpdate.Version;
                tray.ShowBalloonTip(5000, "Codex用量统计有新版本",
                    "右键托盘图标即可更新至 v" + availableUpdate.Version + "。", ToolTipIcon.Info);
            }
        }
        catch (Exception error)
        {
            if (!exiting && userRequested) ShowUpdateError("检查更新失败", error);
        }
        finally
        {
            if (!exiting) ResetUpdateMenu();
        }
    }

    // 确认后才下载。下载和校验完成前，当前托盘程序继续运行。
    private async Task OfferUpdateAsync()
    {
        SetUpdateControls(updateItem.Text, false);
        string installer = null;
        try
        {
            var update = availableUpdate;
            if (MessageBox.Show(Visible ? this : null, "将 v" + ReleaseUpdater.CurrentVersion + " 更新到 v" + update.Version +
                "？\n\n安装时会重启用量统计工具，Codex 可以继续使用。",
                "Codex用量统计更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            SetUpdateControls("正在下载更新…", false);
            var progress = new Progress<int>(percent =>
            {
                if (!exiting && !updateItem.Enabled)
                    SetUpdateControls(percent == 100 ? "正在校验安装包…" : "正在下载更新 · " + percent + "%", false);
            });
            installer = await ReleaseUpdater.DownloadAsync(update, progress, updateCancellation.Token);
            if (exiting) return;
            ReleaseUpdater.StartInstaller(installer, DirectoryPath);
            installer = null;
            ExitTray();
        }
        catch (Exception error)
        {
            if (!exiting)
            {
                availableUpdate = null;
                ShowUpdateError("更新失败", error);
            }
        }
        finally
        {
            ReleaseUpdater.DiscardDownload(installer);
            if (!exiting) ResetUpdateMenu();
        }
    }

    private void ResetUpdateMenu()
    {
        SetUpdateControls(availableUpdate == null ? "检查更新…" : "更新到 v" + availableUpdate.Version + "…", true);
    }

    private void SetUpdateControls(string text, bool enabled)
    {
        updateItem.Text = updateLink.Text = text;
        updateItem.Enabled = updateLink.Enabled = enabled;
    }

    // 给手动操作提供可重试的错误反馈，不显示临时下载地址。
    private void ShowUpdateError(string title, Exception error)
    {
        string message = error is TaskCanceledException ? "连接或下载超时，请稍后重试。" :
            error is HttpRequestException ? "无法连接 GitHub 或下载安装包，请检查网络后重试。" : error.Message;
        MessageBox.Show(Visible ? this : null, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void StopUpdateChecks()
    {
        updateTimer.Dispose();
        updateCancellation.Cancel();
        updateCancellation.Dispose();
    }
}
