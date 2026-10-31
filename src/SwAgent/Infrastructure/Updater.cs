using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// Updates from GitHub Releases. A release carries the Windows installer (SolidWorks-Agent-Setup-x.y.z.exe) and
    /// its SHA-256 (SolidWorks-Agent-Setup-x.y.z.exe.sha256). The installer can't replace the add-in while
    /// SolidWorks has it loaded, so installing always happens after SolidWorks exits: a small PowerShell helper
    /// waits for SLDWORKS.exe to close, runs the installer silently and, when asked, starts SolidWorks again.
    /// </summary>
    internal sealed class Updater
    {
        /// <summary>owner/repo on GitHub whose releases are the update feed.</summary>
        public const string Repository = "msmith297/solidworks-agent";

        public enum State { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }

        private static readonly HttpClient Http = CreateClient();
        private readonly object _lock = new object();
        private CancellationTokenSource _downloadCts;

        public Updater()
        {
            CurrentVersion = ParseVersion(typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
                             ?? typeof(Updater).Assembly.GetName().Version;
        }

        public event Action Changed;

        public Version CurrentVersion { get; }
        public State Status { get; private set; } = State.Idle;
        public Version LatestVersion { get; private set; }
        public string Notes { get; private set; }
        public string ReleaseUrl { get; private set; }
        public string Error { get; private set; }
        public int Progress { get; private set; }
        public DateTime? LastChecked { get; private set; }

        /// <summary>The downloaded, verified installer (Status == Ready).</summary>
        public string InstallerPath { get; private set; }

        private string _assetUrl;
        private string _assetName;
        private long _assetSize;
        private string _shaUrl;

        public JObject ToJson() => new JObject
        {
            ["current"] = CurrentVersion.ToString(3),
            ["latest"] = LatestVersion?.ToString(3),
            ["status"] = Status.ToString().ToLowerInvariant(),
            ["notes"] = Notes,
            ["releaseUrl"] = ReleaseUrl,
            ["error"] = Error,
            ["progress"] = Progress,
            ["lastChecked"] = LastChecked?.ToString("o"),
        };

        /// <summary>Ask GitHub for the latest release.</summary>
        public async Task CheckAsync()
        {
            if (Status == State.Checking || Status == State.Downloading) return;
            Set(State.Checking);
            try
            {
                JObject release;
                using (var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest").ConfigureAwait(true))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        // No release published yet.
                        LastChecked = DateTime.Now;
                        Error = null;
                        Set(State.UpToDate);
                        return;
                    }
                    response.EnsureSuccessStatusCode();
                    release = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                }
                var latest = ParseVersion((string)release["tag_name"]);
                var assets = (JArray)release["assets"] ?? new JArray();
                var setup = assets.FirstOrDefault(a => ((string)a["name"] ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                       && ((string)a["name"]).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0);
                LastChecked = DateTime.Now;
                if (latest == null || setup == null)
                {
                    Error = null;
                    Set(State.UpToDate);
                    return;
                }
                LatestVersion = latest;
                Notes = (string)release["body"];
                ReleaseUrl = (string)release["html_url"];
                _assetName = (string)setup["name"];
                _assetUrl = (string)setup["browser_download_url"];
                _assetSize = (long?)setup["size"] ?? 0;
                _shaUrl = (string)assets.FirstOrDefault(a => (string)a["name"] == _assetName + ".sha256")?["browser_download_url"];
                Error = null;
                if (latest <= CurrentVersion) Set(State.UpToDate);
                else if (InstallerPath != null && File.Exists(InstallerPath) && InstallerPath.Contains(latest.ToString(3))) Set(State.Ready);
                else Set(State.Available);
            }
            catch (Exception ex)
            {
                Fail("Couldn't check for updates: " + Describe(ex));
            }
        }

        /// <summary>Download and verify the installer for the latest release.</summary>
        public async Task<bool> DownloadAsync()
        {
            lock (_lock)
            {
                if (Status == State.Ready) return true;
                if (Status != State.Available || _assetUrl == null) return false;
                _downloadCts = new CancellationTokenSource();
            }
            Set(State.Downloading);
            try
            {
                var dir = Directory.CreateDirectory(Path.Combine(Paths.LocalDir, "updates")).FullName;
                foreach (var old in Directory.GetFiles(dir)) TryDelete(old);
                var target = Path.Combine(dir, _assetName);
                var partial = target + ".part";

                using (var response = await Http.GetAsync(_assetUrl, HttpCompletionOption.ResponseHeadersRead, _downloadCts.Token).ConfigureAwait(true))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? _assetSize;
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(true))
                    using (var file = File.Create(partial))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, _downloadCts.Token).ConfigureAwait(true)) > 0)
                        {
                            await file.WriteAsync(buffer, 0, read, _downloadCts.Token).ConfigureAwait(true);
                            done += read;
                            var pct = total > 0 ? (int)(done * 100 / total) : 0;
                            if (pct != Progress)
                            {
                                Progress = pct;
                                Changed?.Invoke();
                            }
                        }
                    }
                }

                if (_shaUrl == null) throw new InvalidOperationException("the release has no SHA-256 checksum, so the download can't be verified");
                var expected = (await Http.GetStringAsync(_shaUrl).ConfigureAwait(true)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
                string actual;
                using (var sha = SHA256.Create())
                using (var file = File.OpenRead(partial))
                    actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new InvalidOperationException("the download is damaged (checksum mismatch)");

                if (File.Exists(target)) File.Delete(target);
                File.Move(partial, target);
                InstallerPath = target;
                Log.Info($"Downloaded update {LatestVersion} to {target}");
                Set(State.Ready);
                return true;
            }
            catch (Exception ex)
            {
                Fail("Download failed: " + Describe(ex));
                return false;
            }
        }

        /// <summary>
        /// Start the helper that installs the downloaded update once SolidWorks has exited (and optionally restarts
        /// it). The caller then closes SolidWorks, or lets the user close it.
        /// </summary>
        public bool LaunchInstaller(bool restartSolidWorks)
        {
            if (Status != State.Ready || InstallerPath == null || !File.Exists(InstallerPath)) return false;
            try
            {
                var script = Path.Combine(Paths.AddInDir, "update.ps1");
                if (!File.Exists(script)) throw new FileNotFoundException("update.ps1 is missing from the add-in folder", script);
                // Run a copy: the installer replaces the add-in folder, including the original.
                var copy = Path.Combine(Path.GetDirectoryName(InstallerPath), "update.ps1");
                File.Copy(script, copy, true);

                var sw = Process.GetCurrentProcess();
                var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{copy}\" " +
                           $"-Installer \"{InstallerPath}\" -WaitPid {sw.Id} -Log \"{Path.Combine(Paths.LogDir, "update.log")}\"" +
                           (restartSolidWorks ? $" -Restart \"{sw.MainModule.FileName}\"" : "");
                Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true });
                Log.Info($"Update helper started (restart: {restartSolidWorks})");
                return true;
            }
            catch (Exception ex)
            {
                Fail("Couldn't start the installer: " + Describe(ex));
                return false;
            }
        }

        private void Set(State state)
        {
            Status = state;
            if (state != State.Downloading) Progress = state == State.Ready ? 100 : 0;
            Changed?.Invoke();
        }

        private void Fail(string message)
        {
            Error = message;
            Log.Warn(message);
            Set(State.Error);
        }

        /// <summary>"v1.2.3", "1.2.3-beta+abc" -> 1.2.3</summary>
        internal static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
            if (!Version.TryParse(core, out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        private static string Describe(Exception ex) =>
            ex is HttpRequestException && ex.InnerException != null ? ex.InnerException.Message : ex.Message;

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Leftover from an earlier attempt that is still open; harmless.
            }
        }

        private static HttpClient CreateClient()
        {
            // .NET Framework defaults can exclude TLS 1.2, which GitHub requires.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SolidWorks-Agent-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }
    }
}
