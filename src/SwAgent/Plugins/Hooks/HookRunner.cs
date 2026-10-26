using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins.Hooks
{
    /// <summary>
    /// Runs plugin hooks with the Claude Code hook protocol: the event as JSON on stdin, the decision in the exit code and
    /// stdout (HookOutcome.Parse). Matching hooks of an event run in parallel through one cmd.exe each; identical commands
    /// run once. Sync hooks are awaited (each up to its timeout, default 60 s, then killed with its tree). Async hooks run
    /// in the background; an asyncRewake hook that exits 2 raises Rewake with its stderr (else stdout), prefixed with its
    /// rewakeMessage. Runs off the UI thread and never touches SolidWorks. Hook stdin is never logged.
    /// </summary>
    internal sealed class HookRunner
    {
        public const int DefaultTimeoutSeconds = 60;

        private readonly Session _session;
        private readonly object _sync = new object();
        /// <summary>Running hook processes and the plugin each belongs to; background ones are flagged.</summary>
        private readonly Dictionary<PluginProcess, (PluginRuntime plugin, bool background)> _live = new Dictionary<PluginProcess, (PluginRuntime, bool)>();
        private bool _shutDown;

        public HookRunner(Session session)
        {
            _session = session;
        }

        /// <summary>An asyncRewake hook asks to wake the agent: (plugin, event that started it, text).</summary>
        public event Action<PluginRuntime, string, string> Rewake;

        /// <summary>A hook finished: (spec, exit code, stdout, stderr). For tests and diagnostics; never logged.</summary>
        internal event Action<Spec, int, string, string> Finished;

        /// <summary>One hook command to run.</summary>
        internal sealed class Spec
        {
            public PluginRuntime Plugin;
            public string Template;
            public string Command;
            public Dictionary<string, string> Env;
            public int TimeoutSeconds;
            public bool Async;
            public bool AsyncRewake;
            public string RewakeMessage;
        }

        /// <summary>The hooks of `plugins` for an event whose matcher accepts `matchValue` (tool name, SessionStart source).</summary>
        public List<Spec> Select(IEnumerable<PluginRuntime> plugins, string ev, string matchValue)
        {
            var specs = new List<Spec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in plugins ?? Enumerable.Empty<PluginRuntime>())
            {
                if (!(Groups(p?.Hooks, ev) is JArray groups)) continue;
                Dictionary<string, string> env = null;
                foreach (var group in groups.OfType<JObject>())
                {
                    if (HookEvents.UsesMatcher(ev) && !HookEvents.Matches((string)group["matcher"], matchValue)) continue;
                    foreach (var h in (group["hooks"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var type = (string)h["type"] ?? "command";
                        var template = (string)h["command"];
                        if (type != "command" || string.IsNullOrWhiteSpace(template)) continue;
                        env = env ?? _session.EnvFor(p);
                        var spec = new Spec
                        {
                            Plugin = p,
                            Template = template,
                            // bash expands $VAR and ${VAR} from the environment itself, as in Claude Code (a path put into
                            // the text would lose its backslashes outside quotes); only ${user_config.KEY} isn't a variable.
                            Command = HookShell.Bash != null ? p.ExpandOptions?.Invoke(template) ?? template : Session.Expand(p, template, env),
                            Env = env,
                            TimeoutSeconds = Math.Max(1, (int?)Num(h["timeout"]) ?? DefaultTimeoutSeconds),
                            Async = (bool?)h["async"] == true || (bool?)h["asyncRewake"] == true,
                            AsyncRewake = (bool?)h["asyncRewake"] == true,
                            RewakeMessage = (string)h["rewakeMessage"],
                        };
                        // Claude Code runs identical commands once.
                        if (seen.Add(p.Root + "|" + spec.Async + "|" + spec.AsyncRewake + "|" + spec.Command)) specs.Add(spec);
                    }
                }
            }
            return specs;
        }

        private static double? Num(JToken t)
        {
            if (t == null || (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)) return null;
            return (double)t;
        }

        /// <summary>The event's matcher groups; tolerates a hooks.json that still has its outer { "hooks": ... }.</summary>
        private static JToken Groups(JObject hooks, string ev)
        {
            if (hooks == null) return null;
            if (hooks["hooks"] is JObject inner && hooks[ev] == null) hooks = inner;
            return hooks[ev];
        }

        /// <summary>The matchers per event of these plugins' hooks (session.info's hookEvents).</summary>
        public static JObject Matchers(IEnumerable<PluginRuntime> plugins)
        {
            var result = new JObject();
            foreach (var ev in HookEvents.All)
            {
                var matchers = new List<string>();
                foreach (var p in plugins ?? Enumerable.Empty<PluginRuntime>())
                {
                    if (!(Groups(p?.Hooks, ev) is JArray groups)) continue;
                    foreach (var g in groups.OfType<JObject>())
                    {
                        if (!(g["hooks"] is JArray hs) || !hs.OfType<JObject>().Any(h => ((string)h["type"] ?? "command") == "command")) continue;
                        var m = (string)g["matcher"] ?? "";
                        if (!matchers.Contains(m)) matchers.Add(m);
                    }
                }
                if (matchers.Count > 0) result[ev] = new JArray(matchers);
            }
            return result;
        }

        /// <summary>
        /// Runs the hooks: async ones are started and left running, sync ones are awaited and their decisions combined.
        /// `input` holds the event's fields; the common ones (session_id, transcript_path, cwd, hook_event_name,
        /// permission_mode) are added here. Never turn_id: agent-bridge's hook.mjs would take Unify3D for Codex.
        /// </summary>
        public async Task<HookOutcome> RunAsync(List<Spec> specs, string ev, JObject input)
        {
            var outcome = new HookOutcome();
            if (specs.Count == 0) return outcome;
            var stdin = new JObject
            {
                ["session_id"] = _session.Id,
                ["transcript_path"] = _session.TranscriptPath,
                ["cwd"] = Session.ProjectDir,
                ["hook_event_name"] = ev,
                ["permission_mode"] = "default",
            };
            foreach (var kv in input ?? new JObject())
            {
                if (kv.Key != "turn_id" && stdin[kv.Key] == null) stdin[kv.Key] = kv.Value?.DeepClone();
            }
            var text = stdin.ToString(Formatting.None);

            var sync = new List<Task<HookOutcome>>();
            foreach (var spec in specs)
            {
                if (spec.Async) StartBackground(spec, ev, text);
                else sync.Add(RunOneAsync(spec, ev, text));
            }
            foreach (var o in await Task.WhenAll(sync).ConfigureAwait(false)) outcome.Merge(o);
            return outcome;
        }

        private PluginProcess Launch(Spec spec, string ev, string stdin, bool background)
        {
            lock (_sync)
            {
                if (_shutDown) return null;
            }
            PluginProcess proc;
            try
            {
                proc = PluginProcess.StartShell(spec.Command, spec.Env, Session.ProjectDir);
            }
            catch (Exception ex)
            {
                Log.Warn($"Hook {ev} of {spec.Plugin.Name} couldn't start ({spec.Template}): {ex.Message}");
                return null;
            }
            lock (_sync)
            {
                _live[proc] = (spec.Plugin, background);
                if (_shutDown) proc.Kill();
            }
            // On a pool thread: a hook that doesn't read its stdin must not hold up the others.
            _ = Task.Run(() => proc.WriteInputAndClose(stdin));
            return proc;
        }

        private async Task<HookOutcome> RunOneAsync(Spec spec, string ev, string stdin)
        {
            var watch = Stopwatch.StartNew();
            var proc = Launch(spec, ev, stdin, false);
            if (proc == null) return new HookOutcome();
            try
            {
                var timeout = TimeSpan.FromSeconds(Math.Min(spec.TimeoutSeconds, int.MaxValue / 1000));
                if (await Task.WhenAny(proc.Exited, Task.Delay(timeout)).ConfigureAwait(false) != proc.Exited)
                {
                    proc.Kill();
                    Log.Warn($"Hook {ev} of {spec.Plugin.Name} timed out after {spec.TimeoutSeconds} s and was stopped ({spec.Template})");
                    return new HookOutcome();
                }
                await proc.DrainAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var code = proc.ExitCode ?? -1;
                if (proc.Killed) return new HookOutcome(); // stopped by SessionEnd or a disable
                Finished?.Invoke(spec, code, proc.Stdout, proc.Stderr);
                Log.Info($"Hook {ev} of {spec.Plugin.Name}: exit {code} in {watch.ElapsedMilliseconds:N0} ms");
                if (code != 0 && code != 2)
                    Log.Warn($"Hook {ev} of {spec.Plugin.Name} failed (exit {code}): {Clip(proc.Stderr, 500)}");
                return HookOutcome.Parse(ev, code, proc.Stdout, proc.Stderr);
            }
            finally
            {
                lock (_sync) _live.Remove(proc);
                proc.Dispose();
            }
        }

        private void StartBackground(Spec spec, string ev, string stdin)
        {
            var proc = Launch(spec, ev, stdin, true);
            if (proc == null) return;
            Log.Info($"Hook {ev} of {spec.Plugin.Name}: started in the background (pid {proc.Id})");
            _ = Task.Run(async () =>
            {
                try
                {
                    var timeout = TimeSpan.FromMilliseconds(Math.Min(spec.TimeoutSeconds * 1000.0, int.MaxValue - 1));
                    if (await Task.WhenAny(proc.Exited, Task.Delay(timeout)).ConfigureAwait(false) != proc.Exited)
                    {
                        proc.Kill();
                        Log.Info($"Background hook {ev} of {spec.Plugin.Name} reached its timeout ({spec.TimeoutSeconds} s) and was stopped");
                        return;
                    }
                    await proc.DrainAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    var code = proc.ExitCode ?? -1;
                    if (proc.Killed) return;
                    Log.Info($"Background hook {ev} of {spec.Plugin.Name}: exit {code}");
                    Finished?.Invoke(spec, code, proc.Stdout, proc.Stderr);
                    if (code != 2 || !spec.AsyncRewake) return;
                    var text = (proc.Stderr ?? "").Trim();
                    if (text.Length == 0) text = (proc.Stdout ?? "").Trim();
                    if (text.Length == 0) return;
                    if (!string.IsNullOrWhiteSpace(spec.RewakeMessage)) text = spec.RewakeMessage.Trim() + " " + text;
                    Rewake?.Invoke(spec.Plugin, ev, text);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Background hook {ev} of {spec.Plugin.Name}: {ex.Message}");
                }
                finally
                {
                    lock (_sync) _live.Remove(proc);
                    proc.Dispose();
                }
            });
        }

        /// <summary>Background hooks still running (tests).</summary>
        internal int BackgroundCount
        {
            get
            {
                lock (_sync) return _live.Values.Count(v => v.background);
            }
        }

        /// <summary>Kills the running hooks of plugins that may no longer run (disabled, or their trust changed).</summary>
        /// <returns>The ids of the plugins that lost a background hook (e.g. a listener), so a plugin that still runs can start it again.</returns>
        public HashSet<string> KillWhere(Func<PluginRuntime, bool> doomed)
        {
            List<PluginProcess> kill;
            var background = new HashSet<string>(StringComparer.Ordinal);
            lock (_sync)
            {
                var hit = _live.Where(kv => doomed(kv.Value.plugin)).ToList();
                kill = hit.Select(kv => kv.Key).ToList();
                foreach (var kv in hit.Where(kv => kv.Value.background)) background.Add(kv.Value.plugin.Id);
            }
            foreach (var p in kill) p.Kill();
            return background;
        }

        /// <summary>SessionEnd: no new hooks, and every running one is killed with its tree.</summary>
        public void KillAll()
        {
            List<PluginProcess> all;
            lock (_sync)
            {
                _shutDown = true;
                all = _live.Keys.ToList();
            }
            foreach (var p in all) p.Kill();
        }

        /// <summary>Lets hooks run again after KillAll (tests).</summary>
        internal void Reopen()
        {
            lock (_sync) _shutDown = false;
        }

        private static string Clip(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
