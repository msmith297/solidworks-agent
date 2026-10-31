using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Voice
{
    /// <summary>
    /// The Whisper models push-to-talk can use (English-only ggml builds from the whisper.cpp project). They are too big
    /// to ship in the installer, so the user downloads one from Settings; it lives in %LOCALAPPDATA%\SwAgent\speech.
    /// </summary>
    internal static class SpeechModels
    {
        public sealed class Model
        {
            public string Id;
            public string Label;
            public long Bytes;
            public string Note;
        }

        public static readonly Model[] All =
        {
            new Model { Id = "tiny.en", Label = "Fastest", Bytes = 77_704_715, Note = "Quick on any PC; misses more technical words." },
            new Model { Id = "base.en", Label = "Balanced", Bytes = 147_964_211, Note = "Recommended: fast, and good with CAD terms." },
            new Model { Id = "small.en", Label = "Most accurate", Bytes = 487_614_201, Note = "Best accuracy; words appear more slowly on older CPUs." },
        };

        public const string DefaultId = "base.en";

        public static string Dir => Directory.CreateDirectory(Path.Combine(Paths.LocalDir, "speech")).FullName;

        public static Model Find(string id) => All.FirstOrDefault(m => m.Id == id) ?? All.First(m => m.Id == DefaultId);

        public static string PathOf(string id) => Path.Combine(Dir, $"ggml-{Find(id).Id}.bin");

        public static bool Installed(string id)
        {
            var f = new FileInfo(PathOf(id));
            return f.Exists && f.Length > 10_000_000; // only a finished download is renamed from .part
        }

        private static string Url(string id) => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{Find(id).Id}.bin";

        public static JArray ToJson() => new JArray(All.Select(m => new JObject
        {
            ["id"] = m.Id,
            ["label"] = m.Label,
            ["mb"] = (int)Math.Round(m.Bytes / 1048576.0),
            ["note"] = m.Note,
            ["installed"] = Installed(m.Id),
        }));

        private static readonly HttpClient Http = CreateClient();

        /// <summary>Download a model (to a .part file first, so a cancelled download never looks installed).</summary>
        public static async Task DownloadAsync(string id, Action<double> progress, CancellationToken ct)
        {
            var target = PathOf(id);
            var part = target + ".part";
            using (var response = await Http.GetAsync(Url(id), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? Find(id).Bytes;
                using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var file = File.Create(part))
                {
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    var lastReport = DateTime.MinValue;
                    int n;
                    while ((n = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                        done += n;
                        if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                        {
                            lastReport = DateTime.UtcNow;
                            progress?.Invoke(Math.Min(1, done / (double)total));
                        }
                    }
                    if (done < total * 9 / 10) throw new IOException("The download was cut off. Try again.");
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            progress?.Invoke(1);
            Log.Info($"Speech model {id} downloaded to {target}");
        }

        public static void Delete(string id)
        {
            var path = PathOf(id);
            if (File.Exists(path)) File.Delete(path);
        }

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SolidWorks-Agent");
            return client;
        }
    }
}
