using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using SwAgent.Tests.Plugins;
using SolidWorksAgent.LocalLink;
using Xunit;

namespace SwAgent.Tests.LocalLink
{
    /// <summary>
    /// A stand-in for the add-in's side of the pipe (docs/plugins/local-mcp.md 5.3): newline-delimited JSON, hello first, then
    /// requests. Each connection runs on its own task, as the real server serves up to 8 at once. The handler answers a request
    /// with a whole response object ({ id, result } or { id, error }).
    /// </summary>
    internal sealed class FakeLinkServer : IDisposable
    {
        public readonly string Name = "SolidWorks Agent.Test." + Guid.NewGuid().ToString("N");
        public readonly ConcurrentQueue<JObject> Received = new ConcurrentQueue<JObject>();
        public Func<JObject, Task<JObject>> Handle;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly List<IDisposable> _open = new List<IDisposable>();

        public FakeLinkServer(Func<JObject, Task<JObject>> handle)
        {
            Handle = handle;
            Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                // Owned by the current user, as the add-in's pipe is (the exe checks).
                var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    0, 0, SwAgent.Plugins.LocalLink.LinkServer.Security());
                lock (_open) _open.Add(pipe);
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    pipe.Dispose();
                    return;
                }
                _ = Task.Run(() => Serve(pipe));
            }
        }

        private async Task Serve(NamedPipeServerStream pipe)
        {
            var utf8 = new UTF8Encoding(false);
            try
            {
                using (var reader = new StreamReader(pipe, utf8, false, 4096, true))
                using (var writer = new StreamWriter(pipe, utf8, 4096, true) { NewLine = "\n", AutoFlush = true })
                {
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        var request = JObject.Parse(line);
                        Received.Enqueue(request);
                        var response = await Handle(request).ConfigureAwait(false);
                        if (response == null) return; // hang up
                        response["id"] = request["id"];
                        await writer.WriteLineAsync(response.ToString(Formatting.None)).ConfigureAwait(false);
                    }
                }
            }
            catch (IOException)
            {
                // The exe hung up (a cancelled call).
            }
            catch (ObjectDisposedException)
            {
                // Test over.
            }
            finally
            {
                pipe.Dispose();
            }
        }

        public static JObject Result(JObject result) => new JObject { ["result"] = result };

        public static JObject Error(string code, string message) => new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        public static JObject HelloOk() => Result(new JObject { ["protocol"] = LinkProtocol.Version, ["addIn"] = "0.2.0+test", ["solidWorks"] = "2026 SP1.0", ["client"] = "claude-desktop" });

        public void Dispose()
        {
            _stop.Cancel();
            lock (_open)
            {
                foreach (var d in _open) d.Dispose();
            }
        }
    }

    /// <summary>The built SolidWorksAgent.LocalMcp.exe over stdio, as Claude Desktop or Codex runs it.</summary>
    internal sealed class McpProcess : IDisposable
    {
        public readonly Process Process;
        public readonly BlockingCollection<JObject> Messages = new BlockingCollection<JObject>();
        public readonly ConcurrentQueue<string> StdoutLines = new ConcurrentQueue<string>();
        public readonly StringBuilder Stderr = new StringBuilder();
        private readonly List<JObject> _seen = new List<JObject>();

        public McpProcess(string pipe, string client = "claude-desktop")
            : this(pipe, Path.Combine(LocalMcpDeployerTests.BuiltDir(), LocalMcpDeployer.ExeName), "--client " + client)
        {
        }

        /// <summary>A given exe and command line, as a client config names them (the integration tests run the deployed copy).</summary>
        public McpProcess(string pipe, string exe, string arguments)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.EnvironmentVariables[LinkProtocol.PipeEnv] = pipe;
            Process = Process.Start(psi);
            Process.StandardInput.AutoFlush = true;
            Process.StandardInput.NewLine = "\n";
            Process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (Stderr) Stderr.AppendLine(e.Data);
            };
            Process.BeginErrorReadLine();
            Task.Run(() =>
            {
                string line;
                while ((line = Process.StandardOutput.ReadLine()) != null)
                {
                    StdoutLines.Enqueue(line);
                    try
                    {
                        Messages.Add(JObject.Parse(line));
                    }
                    catch (JsonException)
                    {
                        Messages.Add(new JObject { ["notJson"] = line });
                    }
                }
                Messages.CompleteAdding();
            });
        }

        public void Send(JObject message) => Process.StandardInput.WriteLine(message.ToString(Formatting.None));

        public void SendRaw(string line) => Process.StandardInput.WriteLine(line);

        public static JObject Request(object id, string method, JObject p = null)
        {
            var o = new JObject { ["jsonrpc"] = "2.0", ["id"] = Key(id), ["method"] = method };
            if (p != null) o["params"] = p;
            return o;
        }

        private static JToken Key(object id) => id == null ? JValue.CreateNull() : JToken.FromObject(id);

        /// <summary>The response with this id (other messages are kept for later).</summary>
        public JObject Wait(object id, int timeoutMs = 20000)
        {
            var key = Key(id).ToString(Formatting.None);
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                lock (_seen)
                {
                    var hit = _seen.FirstOrDefault(m => m["id"] != null && m["id"].ToString(Formatting.None) == key);
                    if (hit != null)
                    {
                        _seen.Remove(hit);
                        return hit;
                    }
                }
                var left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0 || !Messages.TryTake(out var msg, left)) throw new TimeoutException($"No response for id {key}. stderr:\n{Stderr}");
                lock (_seen) _seen.Add(msg);
            }
        }

        public JObject Call(object id, string method, JObject p = null, int timeoutMs = 20000)
        {
            Send(Request(id, method, p));
            return Wait(id, timeoutMs);
        }

        public JObject Tool(object id, string name, JObject args = null, JObject meta = null)
        {
            var p = new JObject { ["name"] = name, ["arguments"] = args ?? new JObject() };
            if (meta != null) p["_meta"] = meta;
            return Call(id, "tools/call", p);
        }

        public bool Saw(object id)
        {
            var key = Key(id).ToString(Formatting.None);
            while (Messages.TryTake(out var msg)) lock (_seen) _seen.Add(msg);
            lock (_seen) return _seen.Any(m => m["id"] != null && m["id"].ToString(Formatting.None) == key);
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.StandardInput.Close();
                    if (!Process.WaitForExit(3000)) Process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            Process.Dispose();
        }
    }

    /// <summary>docs/plugins/local-mcp.md C1: the MCP exe over stdio against a fake pipe server, in both protocol eras.</summary>
    [Collection("Plugins")]
    public class LocalMcpProtocolTests
    {
        private static JObject Modern => new JObject { ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28", ["io.modelcontextprotocol/clientInfo"] = new JObject { ["name"] = "claude-ai", ["version"] = "3.6556.2" } };

        private static string Text(JObject response) => (string)response["result"]["content"][0]["text"];

        private static FakeLinkServer Fake(Func<JObject, JObject> requests) =>
            new FakeLinkServer(r => Task.FromResult((string)r["method"] == LinkProtocol.Hello ? FakeLinkServer.HelloOk() : requests(r)));

        [Fact]
        public void Legacy_initialize_echoes_known_versions_and_lists_seven_tools()
        {
            using (var fake = Fake(r => FakeLinkServer.Error("bad-request", "no")))
            using (var mcp = new McpProcess(fake.Name))
            {
                foreach (var v in new[] { "2025-11-25", "2025-06-18" })
                {
                    var init = mcp.Call(v, "initialize", new JObject { ["protocolVersion"] = v, ["capabilities"] = new JObject(), ["clientInfo"] = new JObject { ["name"] = "claude-ai", ["version"] = "3.6556.2" } });
                    Assert.Equal(v, (string)init["result"]["protocolVersion"]);
                    Assert.Equal("solidworks-agent", (string)init["result"]["serverInfo"]["name"]);
                    Assert.NotNull(init["result"]["capabilities"]["tools"]);
                    Assert.StartsWith("SolidWorks Agent is an AI agent inside SolidWorks", (string)init["result"]["instructions"]);
                    Assert.Null(init["result"]["resultType"]);
                }
                Assert.Equal("2025-11-25", (string)mcp.Call(3, "initialize", new JObject { ["protocolVersion"] = "2099-01-01" })["result"]["protocolVersion"]);
                mcp.Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
                Assert.True(JToken.DeepEquals(new JObject(), mcp.Call(4, "ping")["result"]));

                var tools = (JArray)mcp.Call(5, "tools/list")["result"]["tools"];
                Assert.Equal(new[] { "ask_solidworks_agent", "get_solidworks_agent_answer", "solidworks_status", "solidworks_model_context", "solidworks_feature_tree", "solidworks_selection", "solidworks_open_documents" },
                    tools.Select(t => (string)t["name"]));
                var ask = tools.Single(t => (string)t["name"] == "ask_solidworks_agent");
                Assert.Equal(new[] { "message" }, ask["inputSchema"]["required"].Values<string>());
                Assert.Equal(20000, (int)ask["inputSchema"]["properties"]["message"]["maxLength"]);
                Assert.Equal(55, (int)ask["inputSchema"]["properties"]["wait_seconds"]["maximum"]);
                Assert.False((bool)ask["inputSchema"]["additionalProperties"]);
                Assert.False((bool)ask["annotations"]["readOnlyHint"]);
                Assert.False((bool)ask["annotations"]["destructiveHint"]);
                Assert.Equal("Ask SolidWorks Agent", (string)ask["annotations"]["title"]);
                Assert.All(tools.Where(t => (string)t["name"] != "ask_solidworks_agent"), t => Assert.True((bool)t["annotations"]["readOnlyHint"]));
                var tree = tools.Single(t => (string)t["name"] == "solidworks_feature_tree");
                Assert.Equal(2000, (int)tree["inputSchema"]["properties"]["max_features"]["maximum"]);
                Assert.Equal("boolean", (string)tree["inputSchema"]["properties"]["include_sub_features"]["type"]);
                Assert.Contains("the active SolidWorks document", (string)tools.Single(t => (string)t["name"] == "solidworks_model_context")["description"]);

                Assert.Equal(-32601, (int)mcp.Call(6, "resources/list")["error"]["code"]);
            }
        }

        [Fact]
        public void Modern_requests_are_stateless_and_an_unknown_version_is_refused()
        {
            using (var fake = Fake(r => FakeLinkServer.Result(new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Part1 (part)" }), ["isError"] = false })))
            using (var mcp = new McpProcess(fake.Name))
            {
                var discover = mcp.Call(1, "server/discover", new JObject { ["_meta"] = Modern });
                Assert.Equal("complete", (string)discover["result"]["resultType"]);
                Assert.Contains("2026-07-28", discover["result"]["supportedVersions"].Values<string>());
                Assert.Contains("2025-11-25", discover["result"]["supportedVersions"].Values<string>());
                Assert.Equal("solidworks-agent", (string)discover["result"]["_meta"]["io.modelcontextprotocol/serverInfo"]["name"]);
                Assert.False(string.IsNullOrEmpty((string)discover["result"]["instructions"]));

                var list = mcp.Call(2, "tools/list", new JObject { ["_meta"] = Modern });
                Assert.Equal("complete", (string)list["result"]["resultType"]);
                Assert.Equal(7, list["result"]["tools"].Count());

                var call = mcp.Tool(3, "solidworks_model_context", null, Modern);
                Assert.Equal("complete", (string)call["result"]["resultType"]);
                Assert.Equal("Part1 (part)", Text(call));
                var hello = fake.Received.First(r => (string)r["method"] == "hello");
                Assert.Equal(1, (int)hello["params"]["protocol"]);
                Assert.Equal("claude-desktop", (string)hello["params"]["client"]);
                Assert.Equal("claude-ai 3.6556.2", (string)hello["params"]["clientInfo"]);

                var old = mcp.Call(4, "tools/list", new JObject { ["_meta"] = new JObject { ["io.modelcontextprotocol/protocolVersion"] = "1900-01-01" } });
                Assert.Equal(-32022, (int)old["error"]["code"]);
                Assert.Equal("1900-01-01", (string)old["error"]["data"]["requested"]);
                Assert.Contains("2026-07-28", old["error"]["data"]["supported"].Values<string>());
            }
        }

        [Fact]
        public void Ask_returns_the_answer_or_a_ticket_for_get_solidworks_agent_answer()
        {
            JObject Reply(JObject r)
            {
                var p = (JObject)r["params"];
                if ((string)r["method"] == "ask")
                {
                    if ((string)p["message"] == "slow")
                        return FakeLinkServer.Result(new JObject { ["ticket"] = "u3d_0123456789abcdef", ["state"] = "working", ["note"] = "SolidWorks Agent is still working on it. Call get_solidworks_agent_answer with ticket \"u3d_0123456789abcdef\" to get the answer." });
                    return FakeLinkServer.Result(new JObject { ["ticket"] = "u3d_1111111111111111", ["state"] = "done", ["answer"] = "Added a 2 mm fillet." });
                }
                if ((string)r["method"] == "answer")
                    return FakeLinkServer.Result(new JObject { ["ticket"] = (string)p["ticket"], ["state"] = "done", ["answer"] = "Done late." });
                return FakeLinkServer.Error("bad-request", "?");
            }
            using (var fake = Fake(Reply))
            using (var mcp = new McpProcess(fake.Name))
            {
                var done = mcp.Tool(1, "ask_solidworks_agent", new JObject { ["message"] = "Add a 2 mm fillet" });
                Assert.Equal("Added a 2 mm fillet.", Text(done));
                Assert.False((bool)done["result"]["isError"]);
                Assert.Equal("done", (string)done["result"]["structuredContent"]["state"]);
                var ask = fake.Received.First(r => (string)r["method"] == "ask");
                Assert.Equal("Add a 2 mm fillet", (string)ask["params"]["message"]);
                Assert.Equal(50000, (int)ask["params"]["waitMs"]);

                var slow = mcp.Tool(2, "ask_solidworks_agent", new JObject { ["message"] = "slow", ["wait_seconds"] = 5 });
                Assert.Contains("u3d_0123456789abcdef", Text(slow));
                Assert.Equal("working", (string)slow["result"]["structuredContent"]["state"]);
                Assert.Equal(5000, (int)fake.Received.Last(r => (string)r["method"] == "ask")["params"]["waitMs"]);

                var late = mcp.Tool(3, "get_solidworks_agent_answer", new JObject { ["ticket"] = "u3d_0123456789abcdef" });
                Assert.Equal("Done late.", Text(late));
                Assert.Equal("u3d_0123456789abcdef", (string)fake.Received.Last(r => (string)r["method"] == "answer")["params"]["ticket"]);

                // Bad arguments are protocol errors; an unknown tool too.
                // Bad arguments to a known tool are tool errors the model can correct; only an unknown tool is a protocol error.
                void ToolError(JObject r, string text)
                {
                    Assert.True((bool)r["result"]["isError"]);
                    Assert.Equal(text, Text(r));
                }
                ToolError(mcp.Tool(4, "ask_solidworks_agent", new JObject()), "ask_solidworks_agent needs \"message\".");
                ToolError(mcp.Tool(5, "ask_solidworks_agent", new JObject { ["message"] = new string('x', 20001) }), "\"message\" is longer than 20000 characters.");
                ToolError(mcp.Tool(6, "ask_solidworks_agent", new JObject { ["message"] = "x", ["wait_seconds"] = 90 }), "\"wait_seconds\" must be between 0 and 55.");
                Assert.Equal(-32602, (int)mcp.Tool(7, "execute_csharp", new JObject { ["code"] = "1" })["error"]["code"]);
                ToolError(mcp.Tool(8, "solidworks_status", new JObject { ["x"] = 1 }), "solidworks_status has no argument \"x\".");
                ToolError(mcp.Tool(9, "solidworks_feature_tree", new JObject { ["max_features"] = "10" }), "\"max_features\" must be a whole number.");
            }
        }

        [Theory]
        [InlineData("off", "The Claude MCP plugin is turned off in SolidWorks Agent (Plugins). Turn it on to use SolidWorks Agent from Claude Desktop.", true)]
        [InlineData("version", "SolidWorks Agent and its Claude Desktop link are different versions. Restart Claude Desktop (quit it from the tray) after updating SolidWorks Agent.", true)]
        [InlineData("limit", "SolidWorks Agent has used its 20 automatic turns for this hour (Options > Plugins). Your message was not delivered.", false)]
        [InlineData("unknown-ticket", "SolidWorks Agent doesn't know that ticket.", false)]
        [InlineData("not-allowed", "Reading the open model is turned off for Claude Desktop in SolidWorks Agent (Plugins > Claude MCP > Settings).", false)]
        [InlineData("busy", "Too many requests from Claude Desktop; wait a minute.", false)]
        [InlineData("no-document", "No document is open in SolidWorks.", false)]
        public void Every_add_in_error_reaches_the_model_as_its_text(string code, string message, bool atHello)
        {
            using (var fake = new FakeLinkServer(r => Task.FromResult(
                (string)r["method"] == "hello" ? (atHello ? FakeLinkServer.Error(code, message) : FakeLinkServer.HelloOk()) : FakeLinkServer.Error(code, message))))
            using (var mcp = new McpProcess(fake.Name))
            {
                foreach (var (id, tool, args) in new[]
                {
                    (1, "ask_solidworks_agent", new JObject { ["message"] = "hi" }),
                    (2, "get_solidworks_agent_answer", new JObject { ["ticket"] = "u3d_x" }),
                    (3, "solidworks_status", new JObject()),
                    (4, "solidworks_selection", new JObject()),
                })
                {
                    var r = mcp.Tool(id, tool, args);
                    Assert.True((bool)r["result"]["isError"], tool);
                    Assert.Equal(message, Text(r));
                }
            }
        }

        [Fact]
        public void Without_SolidWorks_every_tool_says_so_quickly_and_status_reads_as_sentences()
        {
            using (var mcp = new McpProcess("SolidWorks Agent.Test.nobody-" + Guid.NewGuid().ToString("N")))
            {
                var watch = Stopwatch.StartNew();
                var r = mcp.Tool(1, "solidworks_status");
                Assert.True(watch.ElapsedMilliseconds < 3000, $"took {watch.ElapsedMilliseconds} ms");
                Assert.True((bool)r["result"]["isError"]);
                Assert.Equal("SolidWorks isn't running, or the SolidWorks Agent add-in isn't loaded in it. Open SolidWorks with SolidWorks Agent, then try again.", Text(r));
                Assert.Equal(Text(r), Text(mcp.Tool(2, "ask_solidworks_agent", new JObject { ["message"] = "hi" })));
            }
            using (var fake = Fake(r => FakeLinkServer.Result(new JObject
            {
                ["solidWorks"] = "2026 SP1.0", ["addIn"] = "0.2.0+a332038", ["panel"] = "ready",
                ["document"] = new JObject { ["title"] = "bushing.SLDPRT", ["type"] = "part", ["path"] = @"C:\parts\bushing.SLDPRT" },
                ["chat"] = new JObject { ["title"] = "Bushing mold", ["busy"] = false },
                ["autoTurnsLeft"] = 17, ["autoTurnsPerHour"] = 20, ["modelRead"] = true,
            })))
            using (var mcp = new McpProcess(fake.Name))
            {
                var r = mcp.Tool(1, "solidworks_status");
                Assert.Equal("SolidWorks 2026 SP1.0 is running with SolidWorks Agent 0.2.0. Active document: bushing.SLDPRT (part). Open chat: \"Bushing mold\" (idle). Automatic turns left this hour: 17 of 20.", Text(r));
                Assert.Equal("bushing.SLDPRT", (string)r["result"]["structuredContent"]["document"]["title"]);
            }
        }

        [Fact]
        public void Status_says_when_SolidWorks_is_busy_or_reading_the_model_is_off()
        {
            using (var fake = Fake(r => FakeLinkServer.Result(new JObject
            {
                ["solidWorks"] = "2026 SP1.0", ["addIn"] = "0.2.0", ["panel"] = "ready", ["document"] = null,
                ["chat"] = new JObject { ["title"] = "Claude Desktop", ["busy"] = true }, ["solidWorksBusy"] = true, ["modelRead"] = true,
            })))
            using (var mcp = new McpProcess(fake.Name))
            {
                Assert.Equal("SolidWorks 2026 SP1.0 is running with SolidWorks Agent 0.2.0. SolidWorks is busy (a rebuild or a dialog), so the active document couldn't be read. " +
                             "Open chat: \"Claude Desktop\" (busy).", Text(mcp.Tool(1, "solidworks_status")));
            }
            using (var fake = Fake(r => FakeLinkServer.Result(new JObject
            {
                ["solidWorks"] = "2026 SP1.0", ["addIn"] = "0.2.0", ["panel"] = "ready", ["document"] = null,
                ["chat"] = new JObject { ["busy"] = false }, ["modelRead"] = false,
            })))
            using (var mcp = new McpProcess(fake.Name))
            {
                Assert.Equal("SolidWorks 2026 SP1.0 is running with SolidWorks Agent 0.2.0. A chat is open in the SolidWorks Agent panel (idle). " +
                             "Reading the open model is turned off for Claude Desktop in SolidWorks Agent, so the active document isn't shown.", Text(mcp.Tool(1, "solidworks_status")));
            }
        }

        [Fact]
        public void A_message_with_a_non_string_method_is_refused_and_the_server_keeps_going()
        {
            using (var mcp = new McpProcess("SolidWorks Agent.Test.none-" + Guid.NewGuid().ToString("N")))
            {
                mcp.Call(1, "initialize", new JObject { ["protocolVersion"] = "2025-11-25" });
                mcp.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":{\"x\":1}}");
                Assert.Equal(-32600, (int)mcp.Wait(2)["error"]["code"]);
                mcp.SendRaw("{\"jsonrpc\":\"2.0\",\"method\":[1,2]}"); // a notification: no answer
                mcp.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":{\"a\":1}}}");
                Assert.Equal(-32602, (int)mcp.Wait(3)["error"]["code"]);
                Assert.True(JToken.DeepEquals(new JObject(), mcp.Call(4, "ping")["result"]));
                Assert.False(mcp.Process.HasExited);
            }
        }

        [Fact]
        public void Tool_descriptions_point_cad_work_at_ask_solidworks_agent()
        {
            using (var mcp = new McpProcess("SolidWorks Agent.Test.none-" + Guid.NewGuid().ToString("N")))
            {
                var init = mcp.Call(1, "initialize", new JObject { ["protocolVersion"] = "2025-11-25" });
                Assert.Contains("even when no document is open", (string)init["result"]["instructions"]);
                var tools = (JArray)mcp.Call(2, "tools/list")["result"]["tools"];
                Assert.StartsWith("Use for any CAD work in SolidWorks: create new parts", (string)tools.Single(t => (string)t["name"] == "ask_solidworks_agent")["description"]);
                Assert.DoesNotContain("Call this first", (string)tools.Single(t => (string)t["name"] == "solidworks_model_context")["description"]);
            }
        }

        [Fact]
        public void Status_answers_while_an_ask_waits_and_a_cancelled_call_gets_no_answer()
        {
            using (var fake = new FakeLinkServer(async r =>
            {
                switch ((string)r["method"])
                {
                    case "hello":
                        return FakeLinkServer.HelloOk();
                    case "ask":
                        await Task.Delay((string)r["params"]["message"] == "cancel me" ? 2000 : 3000);
                        return FakeLinkServer.Result(new JObject { ["ticket"] = "u3d_2", ["state"] = "done", ["answer"] = "slow answer" });
                    default:
                        return FakeLinkServer.Result(new JObject { ["solidWorks"] = "2026", ["addIn"] = "0.2.0", ["panel"] = "ready" });
                }
            }))
            using (var mcp = new McpProcess(fake.Name))
            {
                mcp.Send(McpProcess.Request(1, "tools/call", new JObject { ["name"] = "ask_solidworks_agent", ["arguments"] = new JObject { ["message"] = "take your time" } }));
                Thread.Sleep(300);
                var status = mcp.Tool(2, "solidworks_status");
                Assert.StartsWith("SolidWorks 2026 is running", Text(status));
                Assert.False(mcp.Saw(1), "the ask is still waiting");
                Assert.Equal("slow answer", Text(mcp.Wait(1)));

                mcp.Send(McpProcess.Request("c-1", "tools/call", new JObject { ["name"] = "ask_solidworks_agent", ["arguments"] = new JObject { ["message"] = "cancel me" } }));
                Thread.Sleep(300);
                mcp.Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JObject { ["requestId"] = "c-1", ["reason"] = "user" } });
                Thread.Sleep(3000);
                Assert.True(JToken.DeepEquals(new JObject(), mcp.Call(3, "ping")["result"]));
                Assert.False(mcp.Saw("c-1"), "a cancelled request is never answered");
            }
        }

        [Fact]
        public void Stdout_is_only_json_rpc_bad_lines_get_parse_errors_and_closing_stdin_exits()
        {
            using (var mcp = new McpProcess("SolidWorks Agent.Test.none-" + Guid.NewGuid().ToString("N")))
            {
                mcp.SendRaw("{not json");
                mcp.SendRaw("");
                var err = mcp.Wait(null);
                Assert.Equal(-32700, (int)err["error"]["code"]);
                Assert.Equal(JTokenType.Null, err["id"].Type);
                mcp.Call(1, "initialize", new JObject { ["protocolVersion"] = "2025-11-25" });
                mcp.Call(2, "tools/list");

                var watch = Stopwatch.StartNew();
                mcp.Process.StandardInput.Close();
                Assert.True(mcp.Process.WaitForExit(2000), "exits when stdin closes");
                Assert.True(watch.ElapsedMilliseconds < 2000);
                Assert.Equal(0, mcp.Process.ExitCode);
                Thread.Sleep(200);
                Assert.All(mcp.StdoutLines, l =>
                {
                    var o = JObject.Parse(l);
                    Assert.Equal("2.0", (string)o["jsonrpc"]);
                });
                lock (mcp.Stderr) Assert.Contains("solidworks-agent: started", mcp.Stderr.ToString());
            }
        }

        [Fact]
        public void An_unknown_client_exits_with_code_2()
        {
            var psi = new ProcessStartInfo(Path.Combine(LocalMcpDeployerTests.BuiltDir(), LocalMcpDeployer.ExeName), "--client foo")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                Assert.True(p.WaitForExit(5000));
                Assert.Equal(2, p.ExitCode);
                Assert.Equal("", p.StandardOutput.ReadToEnd());
                Assert.Contains("--client", p.StandardError.ReadToEnd());
            }
        }
    }
}
