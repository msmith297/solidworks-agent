using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Voice
{
    /// <summary>
    /// The local speech-to-text helper (speech\SolidWorksAgent.Speech.exe, Whisper). It runs as a child process so native audio
    /// or inference code can never crash SolidWorks; it exits on its own when SolidWorks does (its stdin closes).
    /// All events are raised on the SolidWorks UI thread.
    /// </summary>
    internal sealed class SpeechEngine : IDisposable
    {
        public enum State
        {
            Off,
            Loading,
            Ready,
            Error,
        }

        private readonly Control _ui;
        private Process _process;
        private StreamWriter _stdin;
        private string _loadedModel;
        private bool _disposed;

        public SpeechEngine()
        {
            _ui = new Control();
            _ui.CreateControl(); // a window handle on the UI thread, to post helper output back to it
            var _ = _ui.Handle;
        }

        public State Status { get; private set; } = State.Off;
        public string Error { get; private set; }

        public event Action Changed;
        public event Action<int, double> Level;
        public event Action<int, JArray> Partial;
        public event Action<int, JArray> Final;
        public event Action<int, string> Failed;

        public static string ExePath => Path.Combine(Paths.AddInDir, "speech", "SolidWorksAgent.Speech.exe");

        /// <summary>Start the helper (if needed) and load the model; the model is kept loaded between recordings.</summary>
        public void Ensure(string modelId)
        {
            if (_disposed) return;
            var model = SpeechModels.PathOf(modelId);
            if (_process != null && !_process.HasExited && _loadedModel == model && Status != State.Error) return;
            if (!File.Exists(ExePath))
            {
                SetState(State.Error, "The speech component is missing - reinstall SolidWorks Agent.");
                return;
            }
            if (!SpeechModels.Installed(modelId))
            {
                SetState(State.Error, "Download a speech model in Settings first.");
                return;
            }
            try
            {
                if (_process == null || _process.HasExited) StartProcess();
                _loadedModel = model;
                SetState(State.Loading, null);
                Send(new JObject { ["op"] = "load", ["model"] = model });
            }
            catch (Exception ex)
            {
                Log.Error("Starting the speech helper failed", ex);
                SetState(State.Error, "Couldn't start speech recognition: " + ex.Message);
            }
        }

        public void Start(int id, string prompt) => Send(new JObject { ["op"] = "start", ["id"] = id, ["prompt"] = prompt });

        public void Stop(int id) => Send(new JObject { ["op"] = "stop", ["id"] = id });

        public void Cancel(int id) => Send(new JObject { ["op"] = "cancel", ["id"] = id });

        private void StartProcess()
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(ExePath),
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Start();
            try
            {
                p.PriorityClass = ProcessPriorityClass.AboveNormal; // words should keep up even while SolidWorks rebuilds
            }
            catch
            {
                // Not important.
            }
            _process = p;
            _stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            new Thread(() => Read(p)) { IsBackground = true, Name = "speech-out" }.Start();
            new Thread(() =>
            {
                string line;
                while ((line = p.StandardError.ReadLine()) != null) Log.Warn("Speech helper: " + line);
            }) { IsBackground = true, Name = "speech-err" }.Start();
            p.Exited += (s, e) => Post(() =>
            {
                if (_disposed || _process != p) return;
                Log.Warn($"Speech helper exited ({SafeExitCode(p)})");
                _process = null;
                _loadedModel = null;
                SetState(State.Error, "Speech recognition stopped unexpectedly. It restarts the next time you talk.");
            });
            Log.Info("Speech helper started");
        }

        private static string SafeExitCode(Process p)
        {
            try
            {
                return "exit code " + p.ExitCode;
            }
            catch
            {
                return "exit code unknown";
            }
        }

        private void Read(Process p)
        {
            try
            {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    JObject msg;
                    try
                    {
                        msg = JObject.Parse(line);
                    }
                    catch
                    {
                        continue;
                    }
                    Post(() => Handle(msg));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Reading the speech helper failed: " + ex.Message);
            }
        }

        private void Handle(JObject msg)
        {
            var id = (int?)msg["id"] ?? 0;
            switch ((string)msg["type"])
            {
                case "loaded":
                    SetState(State.Ready, null);
                    break;
                case "level":
                    Level?.Invoke(id, (double?)msg["v"] ?? 0);
                    break;
                case "partial":
                    Partial?.Invoke(id, msg["segments"] as JArray ?? new JArray());
                    break;
                case "final":
                    Final?.Invoke(id, msg["segments"] as JArray ?? new JArray());
                    break;
                case "error":
                    var message = (string)msg["message"];
                    Log.Warn("Speech helper error: " + message);
                    if (Status == State.Loading) SetState(State.Error, message);
                    Failed?.Invoke(id, message);
                    break;
                case "log":
                    Log.Info("Speech helper: " + (string)msg["message"]);
                    break;
            }
        }

        private void Send(JObject o)
        {
            try
            {
                _stdin?.WriteLine(o.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                Log.Warn("Writing to the speech helper failed: " + ex.Message);
                SetState(State.Error, "Speech recognition isn't running. It restarts the next time you talk.");
                _process = null;
            }
        }

        private void SetState(State s, string error)
        {
            Status = s;
            Error = error;
            Changed?.Invoke();
        }

        private void Post(Action a)
        {
            if (_disposed || _ui.IsDisposed) return;
            try
            {
                _ui.BeginInvoke((MethodInvoker)(() => Safe.Run("Speech event", a)));
            }
            catch (InvalidOperationException)
            {
                // Shutting down.
            }
        }

        /// <summary>Stop the helper (e.g. to delete its model); the next Ensure starts it again.</summary>
        public void Shutdown()
        {
            var p = _process;
            _process = null;
            _loadedModel = null;
            try
            {
                _stdin?.WriteLine("{\"op\":\"quit\"}");
                _stdin?.Dispose();
                _stdin = null;
                if (p != null && !p.WaitForExit(800)) p.Kill();
            }
            catch
            {
                // Best effort.
            }
            SetState(State.Off, null);
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                Send(new JObject { ["op"] = "quit" });
                _stdin?.Dispose();
                if (_process != null && !_process.WaitForExit(800)) _process.Kill();
            }
            catch
            {
                // Best effort: the helper also exits when its stdin closes.
            }
            _ui.Dispose();
        }
    }
}
