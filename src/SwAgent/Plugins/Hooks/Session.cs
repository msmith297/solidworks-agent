using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins.Hooks
{
    /// <summary>
    /// The SolidWorks Agent session that hooks and MCP servers see: one id per PC, reused across SolidWorks restarts, because
    /// plugins key state on it (agent-bridge keeps the agent's name, listening mode and project per session id, so
    /// "listen always" survives a restart). A second SolidWorks running at the same time gets a temporary id.
    ///
    /// It also holds the wakes waiting for the panel, the per-hour guard on automatic turns, and the transcripts hooks read.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly object _sync = new object();
        private readonly List<Wake> _pending = new List<Wake>();
        private readonly List<Wake> _held = new List<Wake>();
        private readonly Queue<DateTime> _automatic = new Queue<DateTime>();
        private readonly Dictionary<string, JObject> _threads = new Dictionary<string, JObject>(StringComparer.Ordinal);
        private string _transcriptOf;
        private JObject _transcriptData;

        private Session(string id, bool temporary, Mutex mutex)
        {
            Id = id;
            Temporary = temporary;
            _mutex = mutex;
        }

        /// <summary>A session with a given id and no lock (tests).</summary>
        internal static Session ForTests(string id) => new Session(id, false, null);

        /// <summary>A throw-away session for plugin_try_mcp: a server being tried never sees (or binds to) the PC's id.</summary>
        internal static Session Sandbox() => new Session(Guid.NewGuid().ToString(), true, null);

        public string Id { get; }

        /// <summary>Another SolidWorks holds the PC's id: this one has a fresh id for this run only.</summary>
        public bool Temporary { get; }

        /// <summary>The working folder of every hook and MCP server, and their CLAUDE_PROJECT_DIR: fixed, never the document's folder.</summary>
        public static string ProjectDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>%APPDATA%\SwAgent\plugins (SWAGENT_DATA_DIR redirects it in tests).</summary>
        public static string PluginsDir => Paths.PluginsDir;

        public static string TranscriptDir => Paths.PluginTranscriptDir;

        public string TranscriptPath => Path.Combine(TranscriptDir, Id + ".jsonl");

        /// <summary>Automatic turns (wakes and Stop-hook continuations) allowed per hour; settings pluginWakesPerHour.</summary>
        public int AutomaticPerHour { get; set; } = 20;

        /// <summary>The PC's session id from plugins\session.json (created on first use), or a temporary one if it is in use.</summary>
        public static Session Open()
        {
            var file = Paths.PluginSessionFile;
            // Per data folder, so tests (SWAGENT_DATA_DIR) never take the id of a running SolidWorks.
            var name = @"Local\SolidWorksAgent.Session." + Hash(file.ToLowerInvariant());
            Mutex mutex = null;
            var owner = false;
            try
            {
                mutex = new Mutex(true, name, out owner);
            }
            catch (Exception ex)
            {
                Log.Warn("Session lock failed: " + ex.Message);
            }
            if (!owner)
            {
                mutex?.Dispose();
                var temp = Guid.NewGuid().ToString();
                Log.Info($"Plugin session {temp} (temporary: another SolidWorks uses this PC's session)");
                return new Session(temp, true, null);
            }

            string id = null;
            try
            {
                if (File.Exists(file)) id = (string)JObject.Parse(File.ReadAllText(file))["sessionId"];
            }
            catch (Exception ex)
            {
                Log.Warn("Reading plugins\\session.json failed: " + ex.Message);
            }
            if (!Guid.TryParse(id ?? "", out _))
            {
                id = Guid.NewGuid().ToString();
                try
                {
                    Directory.CreateDirectory(PluginsDir);
                    File.WriteAllText(file, new JObject { ["sessionId"] = id }.ToString());
                }
                catch (Exception ex)
                {
                    Log.Warn("Writing plugins\\session.json failed: " + ex.Message);
                }
            }
            Log.Info($"Plugin session {id}");
            return new Session(id, false, mutex);
        }

        private static string Hash(string s)
        {
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Take(8).Select(b => b.ToString("x2")));
            }
        }

        /// <summary>
        /// The environment of a plugin's hooks and MCP servers (plugin null: a server from mcp.json): the cleaned process
        /// environment, the plugin's own env, then the runtime's variables, which a plugin can't override. ${...} in
        /// values is expanded.
        /// </summary>
        public Dictionary<string, string> EnvFor(PluginRuntime plugin)
        {
            var env = ChildEnv.Base();
            if (plugin?.Env != null)
            {
                foreach (var kv in plugin.Env) env[kv.Key] = kv.Value;
            }
            if (plugin != null)
            {
                env["CLAUDE_PLUGIN_ROOT"] = plugin.Root;
                env["SOLIDWORKS_AGENT_PLUGIN_ROOT"] = plugin.Root;
                if (!string.IsNullOrEmpty(plugin.DataDir)) env["CLAUDE_PLUGIN_DATA"] = plugin.DataDir;
            }
            env["CLAUDE_PROJECT_DIR"] = ProjectDir;
            env["CLAUDE_CODE_SESSION_ID"] = Id;
            env["SOLIDWORKS_AGENT"] = "1";
            env["SOLIDWORKS_AGENT_PID"] = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
            env["SOLIDWORKS_AGENT_DEVICE"] = Environment.MachineName;
            foreach (var key in env.Keys.ToList()) env[key] = ChildEnv.Expand(env[key], env);
            return env;
        }

        /// <summary>A command line or argument with the plugin's ${...} placeholders, then any left from the environment, expanded.</summary>
        public static string Expand(PluginRuntime plugin, string text, IDictionary<string, string> env)
        {
            if (plugin?.Expand != null)
            {
                try
                {
                    text = plugin.Expand(text);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Expanding a command of {plugin.Name} failed: {ex.Message}");
                }
            }
            return ChildEnv.Expand(text, env);
        }

        // ---- automatic turns ----

        /// <summary>
        /// Counts one automatic turn (a wake or a Stop-hook continuation) if the hour's allowance has room; false when it
        /// is used up. agent-bridge's own resume counter is reset by every UserPromptSubmit, so this guard is what stops
        /// agents waking each other forever.
        /// </summary>
        public bool TryCountAutomatic(DateTime now)
        {
            lock (_sync)
            {
                while (_automatic.Count > 0 && now - _automatic.Peek() > TimeSpan.FromHours(1)) _automatic.Dequeue();
                if (_automatic.Count >= AutomaticPerHour) return false;
                _automatic.Enqueue(now);
                return true;
            }
        }

        /// <summary>Automatic turns left in the hour's allowance (counts nothing).</summary>
        public int AutomaticLeft(DateTime now)
        {
            lock (_sync)
            {
                return Math.Max(0, AutomaticPerHour - _automatic.Count(t => now - t <= TimeSpan.FromHours(1)));
            }
        }

        // ---- wakes ----

        /// <summary>
        /// A message that starts an agent turn without the user (an asyncRewake hook exited 2, or a message from Claude
        /// Desktop or Codex over the local link).
        /// </summary>
        internal sealed class Wake
        {
            public string Id;
            public string Text;
            public string Source;
            public string Plugin;

            /// <summary>The local link's ticket: the panel reports the turn's answer for it (localLink.answer).</summary>
            public string Ticket;

            /// <summary>Who sent it, for the chat ("Message from Claude Desktop").</summary>
            public string From;

            public JObject ToJson()
            {
                var json = new JObject { ["id"] = Id, ["text"] = Text, ["source"] = Source, ["plugin"] = Plugin };
                if (Ticket != null) json["ticket"] = Ticket;
                if (From != null) json["from"] = From;
                return json;
            }
        }

        /// <summary>
        /// Queues a wake for the panel; true when it may start a turn. Over the hour's allowance it is held instead: the
        /// hook has already leased the mail to this session, so it must not be dropped. Held wakes go to the model with
        /// the user's next prompt (TakeHeld).
        /// </summary>
        public bool AddWake(Wake wake, DateTime now)
        {
            var allowed = TryCountAutomatic(now);
            lock (_sync)
            {
                (allowed ? _pending : _held).Add(wake);
            }
            return allowed;
        }

        /// <summary>
        /// Queues a wake from the local link for the panel. The caller already counted it (TryCountAutomatic) and refused
        /// it over the limit: the sender is waiting and can try again, so it is never held.
        /// </summary>
        public void AddLinkWake(Wake wake)
        {
            lock (_sync)
            {
                _pending.Add(wake);
            }
        }

        /// <summary>The wakes the panel hasn't confirmed yet (it calls this at ready and on every session.wake nudge).</summary>
        public JArray PendingWakes()
        {
            lock (_sync)
            {
                return new JArray(_pending.Select(w => w.ToJson()));
            }
        }

        /// <summary>The panel appended the wake to a thread: it is delivered.</summary>
        public bool AckWake(string id)
        {
            lock (_sync)
            {
                return _pending.RemoveAll(w => w.Id == id) > 0;
            }
        }

        public int HeldCount
        {
            get
            {
                lock (_sync) return _held.Count;
            }
        }

        /// <summary>Wakes held back by the guard, removed: they ride along with the user's next prompt.</summary>
        public List<Wake> TakeHeld()
        {
            lock (_sync)
            {
                var held = _held.ToList();
                _held.Clear();
                return held;
            }
        }

        // ---- transcripts ----

        /// <summary>The panel saved a chat (chat.save): remember its threads, so a hook's transcript is the thread's latest state.</summary>
        public void OnChatSaved(JObject data)
        {
            if (!(data?["threads"] is JArray threads)) return;
            lock (_sync)
            {
                foreach (var t in threads.OfType<JObject>())
                {
                    var id = (string)t["id"];
                    if (!string.IsNullOrEmpty(id)) _threads[id] = t;
                }
            }
        }

        /// <summary>
        /// Writes the thread's messages to the session's transcript (JSONL, one Claude Code style line per message) for
        /// the hooks about to run. Never into the user's document folders.
        /// </summary>
        public void WriteTranscript(string threadId)
        {
            JObject thread = null;
            lock (_sync)
            {
                if (threadId != null) _threads.TryGetValue(threadId, out thread);
                if (thread != null && _transcriptOf == threadId && ReferenceEquals(_transcriptData, thread) && File.Exists(TranscriptPath)) return;
                _transcriptOf = threadId;
                _transcriptData = thread;
                try
                {
                    Directory.CreateDirectory(TranscriptDir);
                    var sb = new StringBuilder();
                    foreach (var m in (thread?["messages"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var role = (string)m["role"];
                        sb.Append(new JObject
                        {
                            ["type"] = role,
                            ["sessionId"] = Id,
                            ["message"] = new JObject { ["role"] = role, ["content"] = m["content"] },
                        }.ToString(Formatting.None)).Append('\n');
                    }
                    File.WriteAllText(TranscriptPath, sb.ToString(), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Log.Warn("Writing the hook transcript failed: " + ex.Message);
                }
            }
        }

        public void Dispose()
        {
            if (_mutex == null) return;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Released on another thread or already gone; the handle closes below.
            }
            _mutex.Dispose();
        }
    }
}
