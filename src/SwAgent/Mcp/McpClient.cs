using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Plugins.Hooks;

namespace SwAgent.Mcp
{
    /// <summary>
    /// One MCP server over stdio: JSON-RPC 2.0, one message per line. Starts the program, runs the initialize handshake
    /// (then notifications/initialized), lists tools, calls them with a timeout and cancellation, and answers the
    /// server's own requests (ping; everything else -32601, never silence). Everything here runs off the UI thread and
    /// never touches SolidWorks.
    /// </summary>
    internal sealed class McpClient : IMcpConnection
    {
        public const string ProtocolVersion = "2025-06-18";

        /// <summary>initialize can take a while: agent-bridge connects to its hub inside it.</summary>
        public static TimeSpan InitializeTimeout = TimeSpan.FromSeconds(45);

        /// <summary>
        /// npx -y / uvx / docker fetch the server on its first start (a download, then a build): give initialize longer.
        /// </summary>
        public static TimeSpan FetchingInitializeTimeout = TimeSpan.FromMinutes(3);

        private static readonly HashSet<string> Fetchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "npx", "uvx", "bunx", "pipx", "pnpm", "dlx", "docker", "podman" };

        /// <summary>The initialize timeout for a program: longer for package runners that may download first.</summary>
        public static TimeSpan InitializeTimeoutFor(string command)
        {
            var program = (command ?? "").Trim().Trim('"');
            var name = program.Length == 0 || program.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ? "" : Path.GetFileNameWithoutExtension(program);
            return Fetchers.Contains(name) ? FetchingInitializeTimeout : InitializeTimeout;
        }

        private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _calls = new ConcurrentDictionary<long, TaskCompletionSource<JObject>>();
        private readonly object _writeLock = new object();
        private PluginProcess _process;
        private StreamWriter _stdin;
        private long _nextId;
        private int _closed;

        private readonly (string Command, IEnumerable<string> Args, IDictionary<string, string> Env, string Cwd)? _launch;

        public McpClient(string name)
        {
            Name = name;
        }

        /// <summary>A server to start later with StartAsync().</summary>
        public McpClient(string name, string command, IEnumerable<string> args, IDictionary<string, string> env, string cwd)
        {
            Name = name;
            _launch = (command, args, env, cwd);
        }

        public string Name { get; }

        /// <summary>The server's instructions from initialize (go into the system prompt, as Claude Code does).</summary>
        public string Instructions { get; private set; }

        public JObject ServerInfo { get; private set; }

        public int? ProcessId => _process?.Id;

        public string Stderr => _process?.Stderr;

        /// <summary>Raised (on a reader thread) when the server says its tool list changed.</summary>
        public event Action ToolsChanged;

        /// <summary>Raised once when the connection ends (the process exited or was stopped); the text says why.</summary>
        public event Action<string> Closed;

        /// <summary>Starts the server given to the constructor and completes the handshake.</summary>
        public Task StartAsync()
        {
            if (_launch == null) throw new InvalidOperationException("No command to start.");
            var l = _launch.Value;
            return StartAsync(l.Command, l.Args, l.Env, l.Cwd);
        }

        /// <summary>Starts the server and completes the handshake. Throws (and stops it) on failure.</summary>
        public async Task StartAsync(string command, IEnumerable<string> args, IDictionary<string, string> env, string cwd)
        {
            _process = PluginProcess.StartProgram(command, args, env, cwd, captureStdout: false);
            _stdin = new StreamWriter(_process.Input, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var reader = _process.Output;
            new Thread(() => ReadLoop(reader), 256 * 1024) { IsBackground = true, Name = "mcp-" + Name }.Start();
            // A grandchild that inherited stdout could keep the pipe open after the server died: its exit counts too.
            var process = _process;
            _ = process.Exited.ContinueWith(async _ =>
            {
                await Task.Delay(2000).ConfigureAwait(false);
                var stderr = process.Stderr;
                Close($"it exited with code {process.ExitCode}" + (string.IsNullOrWhiteSpace(stderr) ? "" : ": " + Clip(LastLines(stderr, 5), 600)));
            }, TaskScheduler.Default);
            try
            {
                var init = await RequestAsync("initialize", new JObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JObject(),
                    // Never "claude": agent-bridge would then join the user's Claude Code app on the hub.
                    ["clientInfo"] = new JObject { ["name"] = "solidworks-agent", ["version"] = typeof(McpClient).Assembly.GetName().Version.ToString(3) },
                }, InitializeTimeoutFor(command), CancellationToken.None).ConfigureAwait(false);
                Instructions = (string)init["instructions"];
                ServerInfo = init["serverInfo"] as JObject;
                Notify("notifications/initialized", null);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>tools/list, every page.</summary>
        public async Task<List<JObject>> ListToolsAsync(CancellationToken ct)
        {
            var tools = new List<JObject>();
            string cursor = null;
            for (var page = 0; page < 50; page++)
            {
                var result = await RequestAsync("tools/list", cursor == null ? new JObject() : new JObject { ["cursor"] = cursor }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                tools.AddRange((result["tools"] as JArray ?? new JArray()).OfType<JObject>());
                cursor = (string)result["nextCursor"];
                if (string.IsNullOrEmpty(cursor)) break;
            }
            return tools;
        }

        /// <summary>tools/call. On cancel or timeout the server gets notifications/cancelled and the call throws.</summary>
        public Task<JObject> CallToolAsync(string tool, JObject arguments, TimeSpan timeout, CancellationToken ct) =>
            RequestAsync("tools/call", new JObject { ["name"] = tool, ["arguments"] = arguments ?? new JObject() }, timeout, ct);

        private async Task<JObject> RequestAsync(string method, JObject parameters, TimeSpan timeout, CancellationToken ct)
        {
            if (_closed != 0) throw new McpException($"The MCP server '{Name}' isn't running.");
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = tcs;
            try
            {
                Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
                using (var timer = new CancellationTokenSource(timeout))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token))
                {
                    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (linked.Token.Register(() => cancelled.TrySetResult(true)))
                    {
                        if (await Task.WhenAny(tcs.Task, cancelled.Task).ConfigureAwait(false) != tcs.Task)
                        {
                            var why = ct.IsCancellationRequested ? "cancelled by the user" : $"timed out after {timeout.TotalSeconds:0} s";
                            if (method != "initialize") Notify("notifications/cancelled", new JObject { ["requestId"] = id, ["reason"] = why });
                            throw ct.IsCancellationRequested ? (Exception)new OperationCanceledException(why) : new TimeoutException($"{Name}: {method} {why}.");
                        }
                    }
                }
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _calls.TryRemove(id, out _);
            }
        }

        private void Notify(string method, JObject parameters)
        {
            var msg = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (parameters != null) msg["params"] = parameters;
            try
            {
                Send(msg);
            }
            catch (Exception ex)
            {
                Log.Warn($"MCP {Name}: {method} not sent ({ex.Message})");
            }
        }

        private void Send(JObject msg)
        {
            var line = msg.ToString(Formatting.None);
            lock (_writeLock)
            {
                if (_stdin == null) throw new McpException($"The MCP server '{Name}' isn't running.");
                try
                {
                    _stdin.WriteLine(line);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    throw new McpException($"The MCP server '{Name}' stopped ({ex.Message}).");
                }
            }
        }

        private void ReadLoop(StreamReader reader)
        {
            string why = "it exited";
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    JObject msg;
                    try
                    {
                        msg = JObject.Parse(line);
                    }
                    catch (JsonException)
                    {
                        Log.Warn($"MCP {Name}: not JSON on stdout: {Clip(line, 200)}");
                        continue;
                    }
                    try
                    {
                        Handle(msg);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"MCP {Name}: handling a message failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                why = ex.Message;
            }
            var stderr = _process?.Stderr;
            var code = _process?.ExitCode;
            if (code != null) why = $"it exited with code {code}";
            if (!string.IsNullOrWhiteSpace(stderr)) why += ": " + Clip(LastLines(stderr, 5), 600);
            Close(why);
        }

        private void Handle(JObject msg)
        {
            var method = (string)msg["method"];
            var id = msg["id"];
            if (method == null)
            {
                // A response to one of our requests.
                if (id == null || id.Type != JTokenType.Integer || !_calls.TryGetValue((long)id, out var tcs)) return;
                if (msg["error"] is JObject error) tcs.TrySetException(new McpException($"{Name}: {(string)error["message"] ?? "error"} ({(int?)error["code"]})"));
                else tcs.TrySetResult(msg["result"] as JObject ?? new JObject());
                return;
            }
            if (id == null || id.Type == JTokenType.Null)
            {
                // A notification.
                if (method == "notifications/tools/list_changed") ToolsChanged?.Invoke();
                else if (method == "notifications/message") Log.Info($"MCP {Name}: {Clip(msg["params"]?.ToString(Formatting.None), 500)}");
                return;
            }
            // The server asks us something: ping is answered, nothing else is offered (no roots, sampling or elicitation).
            if (method == "ping") Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JObject() });
            else Send(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not found: " + method },
            });
        }

        private void Close(string why)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            foreach (var call in _calls.Values) call.TrySetException(new McpException($"The MCP server '{Name}' stopped: {why}."));
            Closed?.Invoke(why);
        }

        /// <summary>
        /// Stops the server: stdin closes first, so it can say goodbye (agent-bridge's mcp.mjs unregisters from its hub in
        /// up to 2 s), then the process tree is killed if it is still there after `grace`.
        /// </summary>
        public async Task StopAsync(TimeSpan grace)
        {
            var p = _process;
            lock (_writeLock)
            {
                try
                {
                    _stdin?.Dispose();
                }
                catch
                {
                    // Already closed.
                }
                _stdin = null;
            }
            if (p != null)
            {
                await Task.WhenAny(p.Exited, Task.Delay(grace)).ConfigureAwait(false);
                p.Kill();
            }
            Close("stopped");
        }

        public void Dispose()
        {
            lock (_writeLock)
            {
                try
                {
                    _stdin?.Dispose();
                }
                catch
                {
                    // Already closed.
                }
                _stdin = null;
            }
            _process?.Kill();
            Close("stopped");
            _process?.Dispose();
        }

        internal static string LastLines(string text, int n)
        {
            var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            return string.Join(" | ", lines.Skip(Math.Max(0, lines.Count - n)));
        }

        internal static string Clip(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    internal class McpException : Exception
    {
        public McpException(string message)
            : base(message)
        {
        }
    }
}
