using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using Newtonsoft.Json.Linq;
using SwAgent.Voice;
using Xunit;

namespace SwAgent.Tests
{
    public class VoiceTests
    {
        [Fact]
        public void Session_puts_every_recording_on_one_clock()
        {
            var start = DateTime.Now.AddSeconds(-10);
            var s = new VoiceSession("doc", IntPtr.Zero, start);
            s.AddRecording(1, start);
            s.AddRecording(2, start.AddSeconds(5));
            s.SetFinal(2, new JArray(new JObject { ["t0"] = 100, ["t1"] = 900, ["text"] = "and this rib" }));
            s.SetFinal(1, new JArray(new JObject { ["t0"] = 200, ["t1"] = 1500, ["text"] = "make this wall thicker" }));

            Assert.Empty(s.Pending);
            Assert.Equal("make this wall thicker and this rib", s.Text);
            var segments = (JArray)s.ToJson()["segments"];
            Assert.Equal(5100, (int)segments[1]["t0"]); // the second press started 5 s in
        }

        [Fact]
        public void Partial_words_show_until_the_final_transcript()
        {
            var start = DateTime.Now;
            var s = new VoiceSession("doc", IntPtr.Zero, start);
            s.AddRecording(1, start);
            s.SetPartial(1, new JArray(new JObject { ["t0"] = 0, ["t1"] = 500, ["text"] = "make this" }));
            Assert.Equal("make this", s.Text);
            Assert.False(s.HasSpeech);
            s.SetFinal(1, new JArray(new JObject { ["t0"] = 0, ["t1"] = 900, ["text"] = "make this wall" }));
            Assert.Equal("make this wall", s.Text);
            Assert.True(s.HasSpeech);
        }

        [Fact]
        public void Images_are_capped_keeping_every_markup_and_the_first_and_last_view()
        {
            var s = new VoiceSession("doc", IntPtr.Zero, DateTime.Now);
            for (var i = 0; i < 12; i++) s.Add("view", "View " + (i + 1), new JObject { ["image"] = "x" }, i * 1000);
            s.Add("markup", "Markup 1", new JObject { ["image"] = "m" }, 3500);
            s.Add("markup", "Markup 2", new JObject { ["image"] = "m" }, 7500);
            s.Add("select", "Selected Face", null, 4000);

            var events = ((JArray)s.ToJson(maxImages: 6)["events"]).Cast<JObject>().ToList();
            var views = events.Where(e => (string)e["kind"] == "view").ToList();
            Assert.Equal(4, views.Count);
            Assert.Equal(0, (int)views.First()["t"]);
            Assert.Equal(11000, (int)views.Last()["t"]);
            Assert.Equal(2, events.Count(e => (string)e["kind"] == "markup"));
            Assert.Contains(events, e => (string)e["kind"] == "select");
            Assert.True(events.Select(e => (int)e["t"]).SequenceEqual(events.Select(e => (int)e["t"]).OrderBy(t => t)));
        }

        [Fact]
        public void Replies_are_read_without_code_or_formatting()
        {
            var text = Speaker.ForSpeech("## Done\n**Shell1** is now `2 mm` thicker. See [the docs](https://x.y).\n```csharp\nvar a = 1;\n```\n- Check [S3]");
            Assert.DoesNotContain("**", text);
            Assert.DoesNotContain("var a", text);
            Assert.DoesNotContain("https", text);
            Assert.Contains("Shell1 is now 2 mm thicker", text);
            Assert.Contains("S3", text);
        }

        /// <summary>The speech helper starts, answers in JSON lines, and reports a missing model instead of crashing.</summary>
        [Fact]
        public void Speech_helper_reports_a_missing_model()
        {
            using (var p = StartHelper())
            {
                p.StandardInput.WriteLine("{\"op\":\"load\",\"model\":\"C:\\\\nowhere\\\\ggml-none.bin\"}");
                var line = p.StandardOutput.ReadLine();
                var msg = JObject.Parse(line);
                Assert.Equal("error", (string)msg["type"]);
                Assert.Contains("not found", (string)msg["message"]);
                p.StandardInput.WriteLine("{\"op\":\"quit\"}");
                Assert.True(p.WaitForExit(5000));
            }
        }

        /// <summary>
        /// End to end with the real model, when it has been downloaded (Settings > Voice): Windows reads a sentence aloud
        /// into a WAV, the helper streams it through the push-to-talk pipeline, and the words come back.
        /// </summary>
        [Fact]
        public void Speech_helper_transcribes_speech_when_a_model_is_installed()
        {
            var model = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwAgent", "speech", "ggml-base.en.bin");
            if (!File.Exists(model)) return; // nothing to test against on this machine

            var wav = Path.Combine(Path.GetTempPath(), "solidworks-agent-voice-test.wav");
            // SAPI wants an STA thread (on the test runner's pool threads it can take the test host down).
            var speak = new System.Threading.Thread(() =>
            {
                using (var synth = new SpeechSynthesizer())
                {
                    synth.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                    synth.Speak("Make this wall two millimeters thicker. Then add a fillet to the selected edge.");
                    synth.SetOutputToNull();
                }
            });
            speak.SetApartmentState(System.Threading.ApartmentState.STA);
            speak.Start();
            Assert.True(speak.Join(30000), "text-to-speech didn't finish");
            using (var p = StartHelper())
            {
                p.StandardInput.WriteLine(new JObject { ["op"] = "load", ["model"] = model }.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal("loaded", (string)Next(p, "loaded", "error")["type"]);
                p.StandardInput.WriteLine(new JObject { ["op"] = "file", ["id"] = 1, ["path"] = wav, ["prompt"] = "SolidWorks CAD. Fillet, chamfer." }.ToString(Newtonsoft.Json.Formatting.None));
                var partials = 0;
                JObject final = null;
                var watch = Stopwatch.StartNew();
                while (final == null && watch.Elapsed < TimeSpan.FromSeconds(60))
                {
                    var msg = Next(p, "partial", "final", "error");
                    Assert.NotEqual("error", (string)msg["type"]);
                    if ((string)msg["type"] == "partial") partials++;
                    else final = msg;
                }
                Assert.NotNull(final);
                var text = ((string)final["text"]).ToLowerInvariant();
                Assert.True(text.Contains("wall") && text.Contains("thicker"), text);
                Assert.True(System.Text.RegularExpressions.Regex.IsMatch(text, @"fill?et"), text); // "fillet" or "filet"
                Assert.True(partials > 0, "words should appear while the audio is still coming in");
                p.StandardInput.WriteLine("{\"op\":\"quit\"}");
                if (!p.WaitForExit(5000)) p.Kill();
            }
        }

        /// <summary>The next message of one of these types; fails (never hangs) if the helper goes quiet for 30 s.</summary>
        private static JObject Next(Process p, params string[] types)
        {
            while (true)
            {
                var read = p.StandardOutput.ReadLineAsync();
                if (!read.Wait(30000))
                {
                    p.Kill();
                    throw new TimeoutException("The speech helper went quiet.");
                }
                if (read.Result == null) throw new InvalidOperationException("The speech helper exited.");
                var msg = JObject.Parse(read.Result);
                if (types.Contains((string)msg["type"])) return msg;
            }
        }

        private static Process StartHelper()
        {
            var exe = FindHelper();
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            var p = Process.Start(psi);
            p.StandardInput.AutoFlush = true;
            return p;
        }

        /// <summary>The helper is built next to the add-in (src/SwAgent/bin/<config>/net48/speech).</summary>
        private static string FindHelper()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SwAgent"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var candidates = new[] { "Debug", "Release" }
                .Select(c => Path.Combine(dir.FullName, "src", "SwAgent", "bin", c, "net48", "speech", "SolidWorksAgent.Speech.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            Assert.NotEmpty(candidates);
            return candidates[0];
        }
    }
}
