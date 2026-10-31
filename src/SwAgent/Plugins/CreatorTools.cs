using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Mcp;
using SwAgent.Plugins.Hooks;
using SwAgent.Tools;

namespace SwAgent.Plugins
{
    /// <summary>
    /// The plugin creator's checks (docs/plugins/creator-v2.md 2.4 and 2.7). check_prerequisites looks for the programs a
    /// plugin's MCP servers or hooks need, with versions and how to install what is missing: it never installs anything.
    /// plugin_try_mcp starts one of the user's own plugins' MCP servers on its own - a throw-away session and data folder,
    /// not registered as agent tools - lists its tools, calls one if allowed, and stops it. The panel asks the user before
    /// each try (it runs a program). Neither touches SolidWorks.
    /// </summary>
    public sealed class CreatorTools
    {
        /// <summary>The add-in's plugins; tests use their own.</summary>
        internal Func<PluginManager> Manager = () => PluginManager.Instance;

        /// <summary>Tests: where a program is (null = not found), instead of the PATH.</summary>
        internal static Func<string, string> FindOverride;

        /// <summary>Tests: what "program --version" prints, instead of running it.</summary>
        internal static Func<string, string> VersionOverride;

        /// <summary>After this many tries of one server that failed, the agent stops and reports.</summary>
        public const int MaxTries = 5;

        private static readonly ConcurrentDictionary<string, int> FailedTries = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        public sealed class PrerequisitesInput
        {
            [ToolParam("Programs to look for, each with an optional minimum version, e.g. [\"node>=18\", \"python>=3.10\", \"uv\", \"git\", \"docker\"]. Any program name on the PATH (not a path).", Required = true)]
            public string[] Programs { get; set; }
        }

        public sealed class TryInput
        {
            [ToolParam("The user's own plugin (plugin_create) whose MCP server to try.", Required = true)]
            public string Plugin { get; set; }

            [ToolParam("The server's name in .mcp.json. Default: every server of the plugin.")]
            public string Server { get; set; }

            [ToolParam("One of its tools to call after listing them, e.g. \"search\". Only a tool the server marks read-only, unless user_ok is true.")]
            public string Call { get; set; }

            [ToolParam("The arguments for that call.")]
            public JObject Arguments { get; set; }

            [ToolParam("true only after the user agreed (ask_user) to this call of a tool that isn't marked read-only.")]
            public bool? UserOk { get; set; }
        }

        // ---------------------------------------------------------------- check_prerequisites

        private sealed class Known
        {
            public string[] Programs;
            public string Install;
        }

        private const string NodeInstall = "Node.js LTS: winget install OpenJS.NodeJS.LTS, or the installer from https://nodejs.org";
        private const string UvInstall = "winget install astral-sh.uv, or https://docs.astral.sh/uv/getting-started/installation/";

        private static readonly Dictionary<string, Known> KnownPrograms = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase)
        {
            ["node"] = new Known { Programs = new[] { "node" }, Install = NodeInstall },
            ["npx"] = new Known { Programs = new[] { "npx" }, Install = "comes with " + NodeInstall },
            ["npm"] = new Known { Programs = new[] { "npm" }, Install = "comes with " + NodeInstall },
            ["python"] = new Known { Programs = new[] { "python", "python3", "py" }, Install = "winget install Python.Python.3.12, or https://www.python.org/downloads/ (tick \"Add python.exe to PATH\")" },
            ["python3"] = new Known { Programs = new[] { "python3", "python", "py" }, Install = "winget install Python.Python.3.12, or https://www.python.org/downloads/ (tick \"Add python.exe to PATH\")" },
            ["uv"] = new Known { Programs = new[] { "uv" }, Install = UvInstall },
            ["uvx"] = new Known { Programs = new[] { "uvx" }, Install = "comes with uv: " + UvInstall },
            ["git"] = new Known { Programs = new[] { "git" }, Install = "winget install Git.Git, or https://git-scm.com/download/win" },
            ["docker"] = new Known { Programs = new[] { "docker" }, Install = "Docker Desktop: winget install Docker.DockerDesktop, or https://www.docker.com/products/docker-desktop/ (it must also be running)" },
            ["bun"] = new Known { Programs = new[] { "bun" }, Install = "winget install Oven-sh.Bun, or https://bun.sh" },
            ["deno"] = new Known { Programs = new[] { "deno" }, Install = "winget install DenoLand.Deno, or https://deno.com" },
            ["java"] = new Known { Programs = new[] { "java" }, Install = "winget install EclipseAdoptium.Temurin.21.JDK, or https://adoptium.net" },
            ["dotnet"] = new Known { Programs = new[] { "dotnet" }, Install = "winget install Microsoft.DotNet.SDK.8, or https://dot.net" },
        };

        private static readonly Regex Requirement = new Regex(@"^\s*([^<>=\s]+)\s*(?:(>=|=>|>|==|=)\s*v?(\d+(?:\.\d+){0,3}))?\s*$");

        [AgentTool("check_prerequisites",
            "Check that programs a plugin needs are installed on this PC (node, python, uv, git, docker or any program name): where each is, its version, " +
            "and the exact install command or link for what is missing or too old. It never installs anything: give the user the install line and wait for them.")]
        public async Task<ToolResult> CheckPrerequisites(ToolContext ctx, PrerequisitesInput input)
        {
            var items = (input.Programs ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)).Take(20).ToList();
            if (items.Count == 0) return ToolResult.Error("Name at least one program, e.g. [\"node>=18\"].");
            var checks = await Task.WhenAll(items.Select(i => Task.Run(() => Check(i)))).ConfigureAwait(false);
            var sb = new StringBuilder();
            foreach (var c in checks) sb.AppendLine("- " + c.Line);
            var missing = checks.Where(c => !c.Ok).Select(c => c.Name).ToList();
            sb.AppendLine(missing.Count == 0
                ? "Everything needed is installed."
                : $"Missing or too old: {string.Join(", ", missing)}. Nothing was installed: give the user the install line(s) above, wait until they say it's done, then check again (a program installed while SolidWorks runs is found).");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        internal sealed class CheckResult
        {
            public string Name;
            public bool Ok;
            public string Line;
            public Version Version;
            public string Path;
        }

        /// <summary>One requirement, e.g. "node>=18": found or not, its version against the minimum, and how to install it.</summary>
        internal static CheckResult Check(string requirement)
        {
            var m = Requirement.Match(requirement ?? "");
            if (!m.Success) return new CheckResult { Name = requirement, Line = $"\"{requirement}\": write it as a program name with an optional version, e.g. node>=18." };
            var name = m.Groups[1].Value;
            // A program's name only, looked up on the PATH: a path would run any file the agent names (one it just wrote into
            // a plugin, say) without the user's OK.
            if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || name.Contains("..") || name.StartsWith("."))
                return new CheckResult { Name = name, Line = $"\"{name}\": give a program's name only (e.g. node), not a path. Only programs on the PATH are checked." };
            var min = m.Groups[3].Success ? ParseVersion(m.Groups[3].Value) : null;
            var minText = m.Groups[3].Value; // as asked: "18", "3.10"
            var exact = m.Groups[2].Value == "=" || m.Groups[2].Value == "==";
            var strict = m.Groups[2].Value == ">";
            KnownPrograms.TryGetValue(name, out var known);
            var install = known?.Install ?? $"install {name} and make sure it is on the PATH";
            var r = new CheckResult { Name = name };

            string path = null, output = null;
            var storeShortcut = false;
            foreach (var program in known?.Programs ?? new[] { name })
            {
                var found = FindOverride != null ? FindOverride(program) : ExecutableResolver.Find(program);
                if (found == null || !Path.IsPathRooted(found) || InUserData(found)) continue; // a relative PATH entry, or a file a plugin or the agent wrote
                var text = VersionOverride != null ? VersionOverride(found) : RunVersion(found);
                // Windows' "python" in WindowsApps only opens the Microsoft Store until Python is really installed.
                if (found.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0 && ParseVersion(text) == null)
                {
                    storeShortcut = true;
                    continue;
                }
                path = found;
                output = text;
                break;
            }
            if (path == null)
            {
                r.Line = $"{name}: not found{(storeShortcut ? " (only the Microsoft Store shortcut)" : "")}. To install: {install}";
                return r;
            }
            r.Path = path;
            r.Version = ParseVersion(output);
            var shown = r.Version?.ToString() ?? "version unknown";
            if (min == null)
            {
                r.Ok = true;
                r.Line = $"{name}: {shown} ({path}) - OK";
            }
            else if (r.Version == null)
            {
                r.Ok = true;
                r.Line = $"{name}: found ({path}), but its version couldn't be read; it needs {(exact ? "" : ">= ")}{minText}. Ask the user to check it.";
            }
            else
            {
                var cmp = Compare(r.Version, min);
                r.Ok = exact ? cmp == 0 : strict ? cmp > 0 : cmp >= 0;
                r.Line = r.Ok
                    ? $"{name}: {shown} ({path}) - OK, needs {(exact ? minText : minText + " or newer")}"
                    : $"{name}: {shown} ({path}) - {(exact ? "not" : "older than")} {minText}. To update: {install}";
            }
            return r;
        }

        /// <summary>Under the add-in's own data folders (plugins, tools, notes): never run as a "program", whatever the PATH says.</summary>
        private static bool InUserData(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                return new[] { Paths.RoamingDir, Paths.LocalDir }.Any(d => full.StartsWith(Path.GetFullPath(d).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>The first version-looking number in a program's output ("v20.11.0", "Python 3.12.1", "git version 2.45.1.windows.1").</summary>
        internal static Version ParseVersion(string text)
        {
            var m = Regex.Match(text ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return Regex.IsMatch((text ?? "").Trim(), @"^v?\d+$") ? new Version(int.Parse((text ?? "").Trim().TrimStart('v')), 0) : null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        /// <summary>Compares on the parts the requirement names: 18 matches 18.x.y, 3.10 matches 3.10.z.</summary>
        private static int Compare(Version have, Version min)
        {
            var a = new[] { have.Major, have.Minor, Math.Max(0, have.Build) };
            var b = new[] { min.Major, Math.Max(0, min.Minor), Math.Max(0, min.Build) };
            for (var i = 0; i < 3; i++)
            {
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            }
            return 0;
        }

        /// <summary>
        /// "program --version" (a .cmd shim through cmd.exe) with a fresh PATH, in the plugin programs' environment
        /// (ChildEnv.Base: no AI provider keys, no other agent's session): these are third-party programs.
        /// </summary>
        internal static ProcessStartInfo VersionStartInfo(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var psi = ext == ".cmd" || ext == ".bat"
                ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /s /c \"\"{path}\" --version\"")
                : new ProcessStartInfo(path, "--version");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables.Clear();
            foreach (var kv in ChildEnv.Base()) psi.EnvironmentVariables[kv.Key] = kv.Value;
            psi.EnvironmentVariables["PATH"] = ExecutableResolver.CurrentPath();
            return psi;
        }

        /// <summary>"program --version"; its output, or null.</summary>
        private static string RunVersion(string path)
        {
            try
            {
                var psi = VersionStartInfo(path);
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Close();
                    var output = p.StandardOutput.ReadToEndAsync();
                    var error = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(15000))
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch
                        {
                            // Already gone.
                        }
                        return null;
                    }
                    Task.WaitAll(new Task[] { output, error }, 2000);
                    return (output.Status == TaskStatus.RanToCompletion ? output.Result : "") + "\n" + (error.Status == TaskStatus.RanToCompletion ? error.Result : "");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"{System.IO.Path.GetFileName(path)} --version failed: {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------- plugin_try_mcp

        [AgentTool("plugin_try_mcp",
            "Try one of the user's own plugins' MCP servers before the plugin is enabled: starts it on its own (not as your tools, with a throw-away data " +
            "folder), lists its tools with their inputs, optionally calls one, then stops it. Returns the server's name and version, its tools, the error if " +
            "it failed and the end of its error output. The user approves each try in the chat. Fix .mcp.json (command, args, env, url, headers) with " +
            "plugin_write_file and try again; after 5 failed tries of a server, stop and tell the user what fails.")]
        public async Task<ToolResult> TryMcp(ToolContext ctx, TryInput input)
        {
            var pm = Manager();
            var p = pm.Find(input.Plugin) ?? throw new ToolException($"No plugin '{input.Plugin}'. list_plugins shows them.");
            if (!p.Local)
                throw new ToolException($"'{p.Name}' is an installed plugin: its MCP servers run once the user approves them in Plugins. Only the user's own plugins can be tried.");
            if (p.Manifest == null) return ToolResult.Error($"'{p.Name}' has no valid .claude-plugin/plugin.json.");
            if (!PluginSettings.Mcp) return ToolResult.Error("MCP servers are turned off (Options > Plugins > Allow MCP servers). Ask the user to turn them on first.");
            if (p.Manifest.McpServers.Count == 0) return ToolResult.Error($"'{p.Name}' has no MCP servers yet: write .mcp.json with plugin_write_file first.");
            var names = string.IsNullOrWhiteSpace(input.Server) ? p.Manifest.McpServers.Keys.ToList() : new List<string> { input.Server.Trim() };
            var unknown = names.Where(n => !p.Manifest.McpServers.ContainsKey(n)).ToList();
            if (unknown.Count > 0) return ToolResult.Error($"'{p.Name}' has no MCP server '{unknown[0]}'. Its servers: {string.Join(", ", p.Manifest.McpServers.Keys)}.");

            var data = Path.Combine(Path.GetTempPath(), "solidworks-agent-try-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            var options = pm.Options(p);
            // Values the user entered stay out of what the agent reads, even when a server prints them.
            var secrets = options.Values.Where(o => o.Sensitive && o.Value != null && o.Value.Length >= 4).Select(o => o.Value).ToList();
            var sb = new StringBuilder();
            var failed = false;
            try
            {
                var runtime = pm.BuildRuntime(p, data);
                var session = Session.Sandbox();
                foreach (var name in names)
                {
                    var raw = p.Manifest.McpServers[name];
                    var unset = options.Where(o => o.Value.Sensitive && o.Value.Value == null && raw.ToString(Formatting.None).Contains("${user_config." + o.Key + "}")).Select(o => o.Key).ToList();
                    if (unset.Count > 0) sb.AppendLine($"Note: {string.Join(", ", unset)} isn't set yet; ask the user with ask_secret first if the server needs it.");
                    failed |= !await TryOne(session, runtime, p.Name, name, raw, runtime.McpServers[name], input, sb).ConfigureAwait(false);
                    sb.AppendLine();
                }
            }
            finally
            {
                Fetcher.TryDelete(data);
            }
            var text = Scrub(sb.ToString().TrimEnd(), secrets);
            return failed ? ToolResult.Error(text) : ToolResult.Ok(text);
        }

        /// <summary>One server: start, list, maybe call, stop. False when it failed to start or list.</summary>
        private static async Task<bool> TryOne(Session session, PluginRuntime runtime, string plugin, string name, JObject raw, JObject config, TryInput input, StringBuilder sb)
        {
            var key = plugin + "/" + name;
            sb.AppendLine($"Server \"{name}\": {McpHost.Describe(raw)}");
            IMcpConnection c = null;
            var watch = Stopwatch.StartNew();
            var ok = false;
            string stderr = null;
            try
            {
                c = McpHost.Create(session, runtime, config, plugin + ":" + name + " (try)");
                Log.Info($"plugin_try_mcp {plugin}:{name}: starting {McpHost.Describe(raw)}");
                try
                {
                    await c.StartAsync().ConfigureAwait(false);
                }
                finally
                {
                    stderr = c.Stderr;
                }
                var info = c.ServerInfo;
                sb.AppendLine($"Started in {watch.Elapsed.TotalSeconds:0.0} s: {(string)info?["name"] ?? "(no name)"} {(string)info?["version"]}".TrimEnd());
                if (!string.IsNullOrWhiteSpace(c.Instructions)) sb.AppendLine("Its instructions: " + McpClient.Clip(OneLine(c.Instructions), 600));
                List<JObject> tools;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60))) tools = await c.ListToolsAsync(cts.Token).ConfigureAwait(false);
                ok = true;
                sb.AppendLine($"{tools.Count} tool(s):");
                foreach (var t in tools.Take(60))
                {
                    sb.AppendLine($"- {(string)t["name"]}{(ReadOnly(t) ? " [read-only]" : "")}: {McpClient.Clip(OneLine((string)t["description"] ?? (string)t["title"] ?? ""), 200)}");
                    var inputs = Inputs(t["inputSchema"] as JObject);
                    if (inputs.Length > 0) sb.AppendLine("  inputs: " + inputs);
                }
                if (tools.Count > 60) sb.AppendLine($"(and {tools.Count - 60} more)");
                if (!string.IsNullOrWhiteSpace(input.Call)) await CallOne(c, tools, input, sb).ConfigureAwait(false);
                stderr = c.Stderr ?? stderr;
            }
            catch (Exception ex)
            {
                stderr = c?.Stderr ?? stderr;
                sb.AppendLine($"Failed after {watch.Elapsed.TotalSeconds:0.0} s: {ex.Message}");
                if (Regex.IsMatch(ex.Message, "not found|cannot find|not recognized|No such file", RegexOptions.IgnoreCase))
                    sb.AppendLine("The program may not be installed: check_prerequisites tells what's missing.");
            }
            finally
            {
                if (c != null)
                {
                    try
                    {
                        await c.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        c.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"plugin_try_mcp {plugin}:{name}: stopping failed: {ex.Message}");
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine("Its error output (last lines): " + McpClient.Clip(McpClient.LastLines(stderr, 15), 1500));
            sb.AppendLine("Stopped.");
            if (ok)
            {
                FailedTries.TryRemove(key, out _);
                return true;
            }
            var tries = FailedTries.AddOrUpdate(key, 1, (k, v) => v + 1);
            sb.AppendLine(tries >= MaxTries
                ? $"That was failed try {tries} of this server: stop changing it and tell the user what fails (the error and output above), and what you tried."
                : $"Failed try {tries} of {MaxTries}.");
            return false;
        }

        private static async Task CallOne(IMcpConnection c, List<JObject> tools, TryInput input, StringBuilder sb)
        {
            var tool = tools.FirstOrDefault(t => (string)t["name"] == input.Call.Trim());
            if (tool == null)
            {
                sb.AppendLine($"Not called: it has no tool '{input.Call}'.");
                return;
            }
            if (!ReadOnly(tool) && input.UserOk != true)
            {
                sb.AppendLine($"Not called: {input.Call} isn't marked read-only, so it may change something. Ask the user (ask_user) first, then try again with user_ok: true.");
                return;
            }
            try
            {
                JObject result;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90))) result = await c.CallToolAsync((string)tool["name"], input.Arguments ?? new JObject(), TimeSpan.FromSeconds(90), cts.Token).ConfigureAwait(false);
                var mapped = McpHost.MapResult(result);
                var text = string.Join("\n", mapped.Content.Select(x => (string)x["type"] == "text" ? (string)x["text"] : $"[{(string)x["type"]}]"));
                sb.AppendLine($"Called {input.Call}{(mapped.IsError ? " - it returned an error" : "")}:");
                sb.AppendLine(McpClient.Clip(text, 3000));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Calling {input.Call} failed: {ex.Message}");
            }
        }

        private static bool ReadOnly(JObject tool) => (bool?)tool["annotations"]?["readOnlyHint"] == true;

        /// <summary>A tool's inputs in one line: "query (string, required), limit (integer)".</summary>
        internal static string Inputs(JObject schema)
        {
            if (!(schema?["properties"] is JObject props)) return "";
            var required = new HashSet<string>((schema["required"] as JArray ?? new JArray()).Select(x => (string)x));
            var parts = props.Properties().Take(15).Select(pr =>
            {
                var type = (string)pr.Value["type"] ?? (pr.Value["enum"] != null ? "enum" : pr.Value["anyOf"] != null ? "any of" : "any");
                return $"{pr.Name} ({type}{(required.Contains(pr.Name) ? ", required" : "")})";
            });
            return McpClient.Clip(string.Join(", ", parts) + (props.Count > 15 ? $", and {props.Count - 15} more" : ""), 500);
        }

        /// <summary>Replaces every secret value in a text with [hidden].</summary>
        internal static string Scrub(string text, IEnumerable<string> secrets)
        {
            foreach (var s in secrets.OrderByDescending(x => x.Length)) text = text.Replace(s, "[hidden]");
            return text;
        }

        private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
    }
}
