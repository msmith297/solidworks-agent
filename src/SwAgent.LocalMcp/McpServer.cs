using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unify3D.LocalLink;

namespace SwAgent.LocalMcp
{
    /// <summary>A JSON-RPC error for the MCP client (unknown method, bad arguments, unsupported version).</summary>
    internal sealed class McpError : Exception
    {
        public readonly int Code;
        public readonly JToken Payload;

        public McpError(int code, string message, JToken data = null) : base(message)
        {
            Code = code;
            Payload = data;
        }
    }

    /// <summary>
    /// A dual-era MCP server (docs/plugins/local-mcp.md 6.3): a request whose params carry
    /// _meta["io.modelcontextprotocol/protocolVersion"] is served the 2026-07-28 way (stateless, resultType "complete",
    /// server/discover); initialize, ping and the rest are served the 2025-11-25-and-earlier way. tools/list and tools/call work
    /// in both. Each request runs as its own task, so a status call answers while an ask waits. Failures the user should hear
    /// about come back as isError tool results, never protocol errors.
    /// </summary>
    internal sealed class McpServer
    {
        public const string Modern = "2026-07-28";
        public static readonly string[] Legacy = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
        public static readonly string[] Supported = new[] { Modern }.Concat(Legacy).ToArray();

        private const string MetaVersion = "io.modelcontextprotocol/protocolVersion";
        private const string MetaClientInfo = "io.modelcontextprotocol/clientInfo";
        private const string MetaServerInfo = "io.modelcontextprotocol/serverInfo";

        private readonly string _client;
        private readonly LinkClient _link;
        private readonly Action<JObject> _send;
        private readonly Action<string> _log;
        private readonly string _version;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new ConcurrentDictionary<string, CancellationTokenSource>();

        public McpServer(string client, LinkClient link, string version, Action<JObject> send, Action<string> log)
        {
            _client = client;
            _link = link;
            _version = version;
            _send = send;
            _log = log;
        }

        /// <summary>One stdin line. Requests run in the background; the returned task ends when this one is answered.</summary>
        public Task HandleLine(string line)
        {
            line = line?.TrimStart((char)0xFEFF); // a client that writes a BOM first
            if (string.IsNullOrWhiteSpace(line)) return Task.CompletedTask;
            JToken token;
            try
            {
                token = JToken.Parse(line);
            }
            catch (JsonException)
            {
                _send(Error(JValue.CreateNull(), -32700, "Parse error"));
                return Task.CompletedTask;
            }
            if (!(token is JObject msg))
            {
                _send(Error(JValue.CreateNull(), -32600, "Invalid Request: send one JSON-RPC object per line."));
                return Task.CompletedTask;
            }
            var id = msg["id"];
            var hasId = id != null && id.Type != JTokenType.Null;
            if (msg["method"] == null) return Task.CompletedTask; // a response: this server sends no requests
            if (msg["method"].Type != JTokenType.String)
            {
                if (hasId) _send(Error(id, -32600, "Invalid Request: \"method\" must be a string."));
                return Task.CompletedTask;
            }
            var method = (string)msg["method"];
            if (!hasId)
            {
                Notification(method, msg["params"] as JObject);
                return Task.CompletedTask;
            }
            // Registered before it runs, so a cancellation that follows at once still finds it.
            var cts = new CancellationTokenSource();
            _running[id.ToString(Formatting.None)] = cts;
            return Task.Run(() => RequestAsync(id, method, msg["params"] as JObject ?? new JObject(), cts));
        }

        private void Notification(string method, JObject p)
        {
            if (method != "notifications/cancelled") return; // notifications/initialized and others need nothing
            var key = p?["requestId"]?.ToString(Formatting.None);
            if (key != null && _running.TryGetValue(key, out var cts))
            {
                _log($"cancelled request {key}");
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already answered.
                }
            }
        }

        private async Task RequestAsync(JToken id, string method, JObject p, CancellationTokenSource cts)
        {
            var key = id.ToString(Formatting.None);
            JObject response;
            try
            {
                var result = await DispatchAsync(method, p, cts.Token).ConfigureAwait(false);
                response = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                response = null; // a cancelled request gets no answer
            }
            catch (McpError e)
            {
                response = Error(id, e.Code, e.Message, e.Payload);
            }
            catch (Exception e)
            {
                _log($"{method} failed: {e.GetType().Name}: {e.Message}");
                response = Error(id, -32603, "Internal error: " + e.Message);
            }
            finally
            {
                _running.TryRemove(key, out _);
                cts.Dispose();
            }
            if (response != null) _send(response);
        }

        private static JObject Error(JToken id, int code, string message, JToken data = null)
        {
            var e = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) e["data"] = data;
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = e };
        }

        private JObject ServerInfo() => new JObject { ["name"] = "unify3d", ["title"] = "Unify3D Agent", ["version"] = _version };

        private async Task<JObject> DispatchAsync(string method, JObject p, CancellationToken cancel)
        {
            var meta = p["_meta"] as JObject;
            var modern = meta?[MetaVersion] != null;
            if (modern)
            {
                var requested = (string)meta[MetaVersion];
                if (requested != Modern)
                    throw new McpError(-32022, "Unsupported protocol version", new JObject { ["supported"] = new JArray(Supported.Cast<object>().ToArray()), ["requested"] = requested });
                if (meta[MetaClientInfo] is JObject ci) SetClientInfo(ci);
            }

            JObject result;
            switch (method)
            {
                case "server/discover":
                    result = new JObject
                    {
                        ["supportedVersions"] = new JArray(Supported.Cast<object>().ToArray()),
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["instructions"] = ToolCatalog.Instructions,
                        ["_meta"] = new JObject { [MetaServerInfo] = ServerInfo() },
                    };
                    modern = true;
                    break;
                case "initialize":
                {
                    var asked = (string)p["protocolVersion"];
                    var version = Legacy.Contains(asked) ? asked : Legacy[0];
                    if (p["clientInfo"] is JObject ci) SetClientInfo(ci);
                    _log($"initialize: {asked} -> {version} (legacy era)");
                    result = new JObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = ServerInfo(),
                        ["instructions"] = ToolCatalog.Instructions,
                    };
                    break;
                }
                case "ping":
                    result = new JObject();
                    break;
                case "tools/list":
                    result = new JObject { ["tools"] = ToolCatalog.List() };
                    break;
                case "tools/call":
                    result = await CallToolAsync(p["name"]?.Type == JTokenType.String ? (string)p["name"] : null, p["arguments"] as JObject ?? new JObject(), cancel).ConfigureAwait(false);
                    break;
                default:
                    throw new McpError(-32601, $"Method not found: {method}");
            }
            if (modern) result["resultType"] = "complete";
            return result;
        }

        private void SetClientInfo(JObject ci)
        {
            var name = (string)ci["name"];
            var version = (string)ci["version"];
            if (name != null) _link.ClientInfo = version == null ? name : name + " " + version;
        }

        private async Task<JObject> CallToolAsync(string name, JObject args, CancellationToken cancel)
        {
            if (name == null) throw new McpError(-32602, "tools/call needs a tool name.");
            if (!ToolCatalog.Exists(name)) throw new McpError(-32602, $"Unknown tool \"{name}\".");
            // Bad arguments to a known tool are a tool error, so the model can correct the call.
            var problem = ToolCatalog.CheckArguments(name, args);
            if (problem != null) return Text(problem, true);
            var watch = Stopwatch.StartNew();
            _log($"tool {name}");
            try
            {
                switch (name)
                {
                    case ToolCatalog.Ask:
                    {
                        var waitMs = WaitMs(args);
                        var reply = await _link.CallAsync(LinkProtocol.Ask, new JObject { ["message"] = (string)args["message"], ["waitMs"] = waitMs },
                            waitMs + 15000, ToolCatalog.SolidWorksSlow, cancel).ConfigureAwait(false);
                        return TicketResult(reply);
                    }
                    case ToolCatalog.GetAnswer:
                    {
                        var waitMs = WaitMs(args);
                        var reply = await _link.CallAsync(LinkProtocol.Answer, new JObject { ["ticket"] = (string)args["ticket"], ["waitMs"] = waitMs },
                            waitMs + 15000, ToolCatalog.SolidWorksSlow, cancel).ConfigureAwait(false);
                        return TicketResult(reply);
                    }
                    case ToolCatalog.Status:
                    {
                        var reply = await _link.CallAsync(LinkProtocol.Status, null, 45000, ToolCatalog.SolidWorksSlow, cancel).ConfigureAwait(false);
                        if (reply.Error != null) return Text(reply.Error, true);
                        var r = Text(ToolCatalog.StatusText(reply.Result, _client), false);
                        r["structuredContent"] = reply.Result;
                        return r;
                    }
                    default:
                    {
                        var reply = await _link.CallAsync(LinkProtocol.Tool, new JObject { ["name"] = name, ["input"] = args },
                            45000, ToolCatalog.SolidWorksSlow, cancel).ConfigureAwait(false);
                        if (reply.Error != null) return Text(reply.Error, true);
                        // Text only, as the add-in sends it.
                        var text = (reply.Result["content"] as JArray ?? new JArray()).OfType<JObject>()
                            .Where(c => (string)c["type"] == "text").Select(c => new JObject { ["type"] = "text", ["text"] = (string)c["text"] ?? "" });
                        var content = new JArray(text.Cast<object>().ToArray());
                        if (content.Count == 0) content.Add(new JObject { ["type"] = "text", ["text"] = "(no output)" });
                        return new JObject { ["content"] = content, ["isError"] = (bool?)reply.Result["isError"] == true };
                    }
                }
            }
            finally
            {
                _log($"tool {name} took {watch.ElapsedMilliseconds} ms");
            }
        }

        private static int WaitMs(JObject args)
        {
            var s = args["wait_seconds"] == null || args["wait_seconds"].Type == JTokenType.Null ? ToolCatalog.DefaultWaitSeconds : (int)(double)args["wait_seconds"];
            return Math.Max(0, Math.Min(ToolCatalog.MaxWaitSeconds, s)) * 1000;
        }

        /// <summary>ask / answer: the answer when done, else the note with the ticket; structuredContent { ticket, state, answer? }.</summary>
        private JObject TicketResult(LinkReply reply)
        {
            if (reply.Error != null) return Text(reply.Error, true);
            var r = reply.Result;
            var ticket = (string)r["ticket"];
            var state = (string)r["state"] ?? "failed";
            var answer = (string)r["answer"];
            _log($"ticket {ticket}: {state}");
            var text = state == "done" ? answer ?? "" : (string)r["note"] ?? ToolCatalog.Note(state, ticket, answer);
            var result = Text(text, state == "failed");
            var structured = new JObject { ["ticket"] = ticket, ["state"] = state };
            if (answer != null) structured["answer"] = answer;
            result["structuredContent"] = structured;
            return result;
        }

        private static JObject Text(string text, bool isError) => new JObject
        {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };
    }
}
