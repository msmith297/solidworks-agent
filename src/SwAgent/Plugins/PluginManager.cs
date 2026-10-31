using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins
{
    /// <summary>
    /// Installed plugins and marketplaces (design 3): %APPDATA%\SwAgent\plugins\ holds installed.json, marketplaces\ (copies
    /// or clones), cache\&lt;plugin&gt;\&lt;version&gt;\ (installed files, read-only, replaced on update), local\&lt;plugin&gt;\ (the
    /// user's own plugins: the "local" marketplace, editable) and data\&lt;plugin&gt;\ (CLAUDE_PLUGIN_DATA, kept across updates).
    /// The built-in "solidworks-agent" marketplace ships with the add-in in builtin-plugins\.
    ///
    /// Trust (design critique 16): hooks and MCP servers only run when the hash of the plugin's current hooks and MCP servers
    /// equals the hash the user approved in the panel's trust dialog (Install / Enable with trustHash). The agent's tools
    /// never pass a trustHash, so nothing the agent writes or enables runs a program without the user's click.
    ///
    /// Everything here is files and network: no SolidWorks, safe on background threads (fetching runs there).
    /// </summary>
    internal sealed class PluginManager : IPluginRuntimeSource
    {
        public const string BuiltInMarketplace = "solidworks-agent", LocalMarketplace = "local";

        private static readonly Regex IdPattern = new Regex("^([a-z0-9][a-z0-9-]{1,47})@([a-z0-9][a-z0-9-]{1,47})$");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SwAgent.PluginConfig.v1");
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
        };

        private static PluginManager _instance;

        /// <summary>The add-in's plugins (under Paths, so SWAGENT_DATA_DIR redirects it). Tests may swap it.</summary>
        public static PluginManager Instance
        {
            get => _instance ?? (_instance = new PluginManager());
            internal set => _instance = value;
        }

        public string Root { get; }
        public string BuiltInRoot { get; }
        public string InstalledFile => Path.Combine(Root, "installed.json");
        public string MarketplacesDir => Path.Combine(Root, "marketplaces");
        public string CacheDir => Path.Combine(Root, "cache");
        public string LocalDir => Path.Combine(Root, "local");
        public string DataRoot => Path.Combine(Root, "data");
        private string SecretsDir => Path.Combine(Root, "secrets");
        private string TrashDir => Path.Combine(Root, "trash");

        private readonly object _lock = new object();
        private readonly object _op = new object();
        private readonly Dictionary<string, Staged> _staged = new Dictionary<string, Staged>(StringComparer.Ordinal);
        private List<LoadedPlugin> _snapshot;
        private DateTime _snapshotAt;

        private ClientLinks _links;

        /// <summary>
        /// Claude Desktop's and Codex's configs for the Claude MCP and Codex MCP plugins (docs/plugins/local-mcp.md 7). Tests swap
        /// it for one over temp folders.
        /// </summary>
        internal ClientLinks Links
        {
            get => _links ?? (_links = new ClientLinks(ClientLocator.Default, new LocalMcpDeployer(), DataRoot));
            set => _links = value;
        }

        /// <summary>Install, uninstall, enable, trust, update, options or a local plugin's files changed. Raised on the calling (often a background) thread.</summary>
        public event Action Changed;

        /// <param name="root">The plugins folder (default %APPDATA%\SwAgent\plugins).</param>
        /// <param name="builtInRoot">The built-in marketplace folder (default builtin-plugins next to the add-in).</param>
        public PluginManager(string root = null, string builtInRoot = null)
        {
            Root = root ?? Paths.PluginsDir;
            BuiltInRoot = builtInRoot ?? Path.Combine(Paths.AddInDir, "builtin-plugins");
            Directory.CreateDirectory(Root);
            MigrateRename();
        }

        /// <summary>
        /// The product was called Unify3D until 2026-10: installed.json named the built-in marketplace "unify3d". Move it to the
        /// new ids once (a copy of the old file stays next to it), so installed plugins stay installed, on and approved.
        /// </summary>
        private void MigrateRename()
        {
            try
            {
                if (!File.Exists(InstalledFile)) return;
                var before = File.ReadAllText(InstalledFile);
                var after = RenamedState(before);
                if (after == before) return;
                File.Copy(InstalledFile, InstalledFile + ".before-rename", true);
                var tmp = InstalledFile + ".tmp";
                File.WriteAllText(tmp, after, new UTF8Encoding(false));
                AtomicFile.Replace(tmp, InstalledFile);
                var oldData = Path.Combine(DataRoot, "agent-bridge-unify3d");
                var newData = Path.Combine(DataRoot, "agent-bridge-solidworks");
                if (Directory.Exists(oldData) && !Directory.Exists(newData)) Directory.Move(oldData, newData);
                Log.Info("Plugins: moved installed.json to the SolidWorks Agent ids");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Error("Moving installed.json to the new ids failed", ex);
            }
        }

        /// <summary>installed.json's text with the old marketplace id, overlay name, block name and variables replaced.</summary>
        internal static string RenamedState(string json) => json
            .Replace("agent-bridge-unify3d", "agent-bridge-solidworks")
            .Replace("@unify3d\"", "@" + BuiltInMarketplace + "\"")
            .Replace("${UNIFY3D_", "${SOLIDWORKS_AGENT_")
            .Replace("\"Unify3D SolidWorks - ", "\"SolidWorks Agent - ")
            .Replace("\"unify3d\": {", "\"solidworksAgent\": {")
            .Replace("\"marketplace\": \"unify3d\"", "\"marketplace\": \"" + BuiltInMarketplace + "\"");

        // ---------------------------------------------------------------- state (installed.json)

        internal sealed class PluginRecord
        {
            public string Name;
            public string Marketplace;
            public string Version;
            public string Sha;
            public string Path;
            public bool Enabled;
            public string TrustedHash;
            public JObject Source;
            public string InstalledAt;
            public string UpdatedAt;
            public JObject UserConfig = new JObject();
            public JObject SolidWorksAgent = new JObject();
            public string PendingUpdate;

            [JsonIgnore] public string Id => Name + "@" + Marketplace;
            [JsonIgnore] public bool Local => Marketplace == LocalMarketplace;
        }

        internal sealed class MarketplaceRecord
        {
            public JObject Source;
            public string Path;
            public string UpdatedAt;
            public string Sha;
            public bool? AutoUpdate;
        }

        private sealed class State
        {
            public Dictionary<string, PluginRecord> Plugins = new Dictionary<string, PluginRecord>(StringComparer.Ordinal);
            public Dictionary<string, MarketplaceRecord> Marketplaces = new Dictionary<string, MarketplaceRecord>(StringComparer.Ordinal);
            public List<string> Trash = new List<string>();
        }

        private State Load()
        {
            try
            {
                if (File.Exists(InstalledFile))
                {
                    var s = JsonConvert.DeserializeObject<State>(File.ReadAllText(InstalledFile), Json) ?? new State();
                    s.Plugins = new Dictionary<string, PluginRecord>(s.Plugins ?? new Dictionary<string, PluginRecord>(), StringComparer.Ordinal);
                    s.Marketplaces = new Dictionary<string, MarketplaceRecord>(s.Marketplaces ?? new Dictionary<string, MarketplaceRecord>(), StringComparer.Ordinal);
                    s.Trash = s.Trash ?? new List<string>();
                    foreach (var r in s.Plugins.Values)
                    {
                        r.UserConfig = r.UserConfig ?? new JObject();
                        r.SolidWorksAgent = r.SolidWorksAgent ?? new JObject();
                    }
                    return s;
                }
            }
            catch (Exception ex)
            {
                Log.Error("installed.json unreadable; plugins start empty", ex);
            }
            return new State();
        }

        private void Save(State s)
        {
            var tmp = InstalledFile + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(s, Json), new UTF8Encoding(false));
            AtomicFile.Replace(tmp, InstalledFile);
        }

        /// <summary>Read, change and write installed.json under the lock; then raise Changed.</summary>
        private T Mutate<T>(Func<State, T> change)
        {
            T result;
            lock (_lock)
            {
                var s = Load();
                result = change(s);
                Save(s);
                _snapshot = null;
            }
            RaiseChanged();
            return result;
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("A plugins.changed handler failed", ex);
            }
        }

        /// <summary>A local plugin's files changed (plugin_write_file, plugin_add_tool): drop the cached snapshot and tell the runtime.</summary>
        public void Invalidate()
        {
            lock (_lock) _snapshot = null;
            RaiseChanged();
        }

        // ---------------------------------------------------------------- plugins as loaded

        /// <summary>An installed plugin with its manifest read from disk.</summary>
        internal sealed class LoadedPlugin
        {
            public PluginRecord Record;
            public PluginManifest Manifest;
            public string Id => Record.Id;
            public string Name => Record.Name;
            public string Root => Record.Path;
            public bool Enabled => Record.Enabled;
            public bool Local => Record.Local;
        }

        /// <summary>
        /// Every installed plugin (local ones included: each folder in local\ is installed, disabled until enabled), read
        /// once and cached for a few seconds - the tool index asks for every tool.
        /// </summary>
        public List<LoadedPlugin> Installed()
        {
            lock (_lock)
            {
                if (_snapshot != null && (DateTime.UtcNow - _snapshotAt).TotalSeconds < 3) return _snapshot;
                var s = Load();
                var list = new List<LoadedPlugin>();
                foreach (var r in s.Plugins.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    if (r.Local) r.Path = Path.Combine(LocalDir, r.Name);
                    var m = PluginManifest.Load(r.Path);
                    if (m == null)
                    {
                        if (!r.Local) Log.Warn($"Plugin {r.Id}: its folder {r.Path} is missing.");
                        if (r.Local) continue; // deleted by hand
                        m = new PluginManifest { Root = r.Path, Name = r.Name, Findings = { "error: the installed files are missing; reinstall the plugin." } };
                    }
                    list.Add(new LoadedPlugin { Record = r, Manifest = m });
                }
                if (Directory.Exists(LocalDir))
                {
                    foreach (var dir in Directory.GetDirectories(LocalDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        var name = Path.GetFileName(dir);
                        if (!PluginManifest.NamePattern.IsMatch(name) || s.Plugins.ContainsKey(name + "@" + LocalMarketplace)) continue;
                        if (!File.Exists(PluginManifest.ManifestPath(dir))) continue;
                        list.Add(new LoadedPlugin
                        {
                            Record = new PluginRecord { Name = name, Marketplace = LocalMarketplace, Path = dir, Enabled = false },
                            Manifest = PluginManifest.Load(dir),
                        });
                    }
                }
                _snapshot = list;
                _snapshotAt = DateTime.UtcNow;
                return list;
            }
        }

        public IEnumerable<LoadedPlugin> EnabledPlugins() => Installed().Where(p => p.Enabled);

        /// <summary>An installed plugin by id ("name@marketplace") or by name; null when none.</summary>
        public LoadedPlugin Find(string idOrName)
        {
            var key = (idOrName ?? "").Trim().ToLowerInvariant();
            return Installed().FirstOrDefault(p => p.Id == key) ?? Installed().FirstOrDefault(p => p.Name == key);
        }

        /// <summary>
        /// The folder of a plugin's tool ("tool" of "plugin:tool") when the plugin is enabled and has it; else null. A tool
        /// with C# of a plugin the user hasn't approved (in this exact form) isn't found: its macro and preview would run
        /// inside SolidWorks (ribbon, panel form, run_tool_macro, test_tool all look tools up here).
        /// </summary>
        public string ToolDir(string plugin, string tool)
        {
            var p = EnabledPlugins().FirstOrDefault(x => x.Name == plugin);
            return p == null ? null : UsableTools(p).Where(t => t.Name == tool).Select(t => t.Dir).FirstOrDefault();
        }

        /// <summary>A plugin's tools that may run: all of them once it is approved; before that only those without C#.</summary>
        public IEnumerable<(string Name, string Dir)> UsableTools(LoadedPlugin p)
        {
            var tools = p.Manifest?.Tools().ToList() ?? new List<(string, string)>();
            if (tools.Count == 0 || IsTrusted(p)) return tools;
            return tools.Where(t => !Directory.EnumerateFiles(t.Dir, "*.cs", SearchOption.AllDirectories).Any()).ToList();
        }

        /// <summary>A folder holding installed (not local) plugin files: read-only, replaced on update.</summary>
        public bool IsReadOnlyDir(string dir) => dir != null && PluginManifest.IsInside(CacheDir, dir);

        /// <summary>
        /// The current trust hash: hooks + MCP servers + its tools' C#, and for a local plugin with hooks or MCP servers every
        /// file too (its hooks may run them). A local plugin's tools are the user's own (made in SolidWorks Agent, like the tools in
        /// %APPDATA%\SwAgent\tools), so on their own they need no approval.
        /// </summary>
        internal string CurrentHash(LoadedPlugin p)
        {
            if (p.Manifest == null) return "";
            var client = LinkClientOf(p);
            if (!p.Manifest.HasRuntime && (p.Local || !p.Manifest.HasToolCode)) return client == null ? "" : Links.Combine("", client);
            var files = p.Local && Directory.Exists(p.Root)
                ? Directory.GetFiles(p.Root, "*", SearchOption.AllDirectories).Where(f => !f.Substring(p.Root.Length).Replace('\\', '/').Contains("/.git/"))
                : null;
            var hash = p.Manifest.TrustHash(files, toolCode: !p.Local);
            return client == null ? hash : Links.Combine(hash, client);
        }

        /// <summary>
        /// The app a link plugin (Claude MCP, Codex MCP) registers in, from the record's copy of its marketplace entry (so uninstall
        /// still knows when the marketplace changed later); null for every other plugin.
        /// </summary>
        internal static string LinkClientOf(LoadedPlugin p) => p.Local ? null : ClientLinks.ClientOf(p.Record.Marketplace, p.Record.SolidWorksAgent);

        public bool IsTrusted(LoadedPlugin p)
        {
            var hash = CurrentHash(p);
            return hash == "" || hash == p.Record.TrustedHash;
        }

        // ---------------------------------------------------------------- the runtime seam (track B)

        /// <summary>Every enabled plugin as the runtime sees it, trusted or not (Runnable() filters).</summary>
        public IReadOnlyList<PluginRuntime> Enabled() => EnabledPlugins().Where(p => p.Manifest != null).Select(p => BuildRuntime(p)).ToList();

        public IReadOnlyList<PluginRuntime> Runnable()
        {
            var list = new List<PluginRuntime>();
            foreach (var p in EnabledPlugins())
            {
                if (p.Manifest == null || !IsTrusted(p)) continue;
                var rt = BuildRuntime(p);
                if (!PluginSettings.Hooks) rt.Hooks = new JObject();
                if (!PluginSettings.Mcp) rt.McpServers = new Dictionary<string, JObject>();
                list.Add(rt);
            }
            return list;
        }

        /// <param name="dataDir">Another CLAUDE_PLUGIN_DATA (plugin_try_mcp's throw-away folder); default its own.</param>
        internal PluginRuntime BuildRuntime(LoadedPlugin p, string dataDir = null)
        {
            var m = p.Manifest;
            dataDir = Directory.CreateDirectory(dataDir ?? Path.Combine(DataRoot, p.Name)).FullName;
            var options = Options(p);
            var vars = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CLAUDE_PLUGIN_ROOT"] = p.Root,
                ["SOLIDWORKS_AGENT_PLUGIN_ROOT"] = p.Root,
                ["CLAUDE_PLUGIN_DATA"] = dataDir,
                ["SOLIDWORKS_AGENT_DEVICE"] = Environment.MachineName,
                ["SOLIDWORKS_AGENT_PID"] = Process.GetCurrentProcess().Id.ToString(),
            };
            Func<string, string> full = text => Expand(text, vars, options, withSecrets: true);
            Func<string, string> forCommands = text => Expand(text, vars, options, withSecrets: false);

            // Entry env first, then plugin.json's solidworksAgent.env, then the user's options (design critique 3).
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in new[] { EntryEnv(p), m.SolidWorksAgent?["env"] as JObject })
            {
                foreach (var kv in source?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    if (IsBlockedEnv(kv.Name)) continue;
                    env[kv.Name] = full((string)kv.Value ?? "");
                }
            }
            foreach (var o in options)
            {
                if (o.Value.Value != null) env["CLAUDE_PLUGIN_OPTION_" + o.Key.ToUpperInvariant()] = o.Value.Value;
            }
            env["CLAUDE_PLUGIN_ROOT"] = p.Root;
            env["SOLIDWORKS_AGENT_PLUGIN_ROOT"] = p.Root;
            env["CLAUDE_PLUGIN_DATA"] = dataDir;
            env["SOLIDWORKS_AGENT"] = "1";

            var servers = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var kv in m.McpServers)
            {
                var s = (JObject)kv.Value.DeepClone();
                if (s["env"] is JObject e)
                {
                    foreach (var prop in e.Properties().ToList())
                    {
                        if (IsBlockedEnv(prop.Name)) prop.Remove();
                        else prop.Value = full((string)prop.Value ?? "");
                    }
                }
                // A remote server's key travels in a header: "Authorization": "Bearer ${user_config.token}".
                if (s["headers"] is JObject h)
                {
                    foreach (var prop in h.Properties()) prop.Value = full((string)prop.Value ?? "");
                }
                if (s["url"] != null) s["url"] = full((string)s["url"] ?? "");
                servers[kv.Key] = s;
            }
            return new PluginRuntime
            {
                Id = p.Id,
                Name = p.Name,
                Root = p.Root,
                DataDir = dataDir,
                Env = env,
                Expand = forCommands,
                ExpandOptions = text => ExpandOptions(text, options),
                Hooks = (JObject)m.Hooks.DeepClone(),
                McpServers = servers,
                TrustHash = CurrentHash(p),
            };
        }

        /// <summary>
        /// Environment names a plugin may not set (solidworksAgent.env, an MCP server's env): the Anthropic key and every other
        /// ANTHROPIC_* secret, and CLAUDE_CODE_* (the runtime sets CLAUDE_CODE_SESSION_ID itself), as ChildEnv.Hidden.
        /// OPENAI_* is allowed on purpose: a plugin may give its own MCP server OPENAI_API_KEY from its own
        /// ${user_config.key}. The user's agent key can't leak that way (hidden from the child; ${OPENAI_API_KEY} is "").
        /// </summary>
        private static bool IsBlockedEnv(string name) =>
            name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase);

        private static readonly Regex Placeholder = new Regex(@"\$\{([^}]+)\}");

        /// <summary>
        /// ${CLAUDE_PLUGIN_ROOT} and friends and ${user_config.KEY}; secrets (${ANTHROPIC_*}, ${OPENAI_*}) become "". Anything else is left
        /// as written: never this process's environment. Command and skill text (which goes to the model, from plugins that
        /// may need no approval) gets nothing else; a hook or MCP command line then gets ${ENV} and ${ENV:-default} from its
        /// own child environment (Session.Expand), which holds no secrets.
        /// </summary>
        internal static string Expand(string text, IDictionary<string, string> vars, IDictionary<string, (string Value, bool Sensitive)> options, bool withSecrets)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Placeholder.Replace(text, m =>
            {
                var key = m.Groups[1].Value.Trim();
                if (vars.TryGetValue(key, out var v)) return v;
                if (key.StartsWith("user_config.", StringComparison.Ordinal))
                {
                    if (!options.TryGetValue(key.Substring(12), out var o)) return "";
                    return o.Sensitive && !withSecrets ? "" : o.Value ?? "";
                }
                // ${CLAUDE_CODE_SESSION_ID} stays: the child environment has the runtime's own.
                if (SecretEnv.IsProviderSecret(key)) return "";
                return m.Value;
            });
        }

        /// <summary>Only ${user_config.KEY} (sensitive ones become ""): for hook commands bash expands itself.</summary>
        internal static string ExpandOptions(string text, IDictionary<string, (string Value, bool Sensitive)> options)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("${user_config.", StringComparison.Ordinal) < 0) return text;
            return Placeholder.Replace(text, m =>
            {
                var key = m.Groups[1].Value.Trim();
                if (!key.StartsWith("user_config.", StringComparison.Ordinal)) return m.Value;
                return options.TryGetValue(key.Substring(12), out var o) && !o.Sensitive ? o.Value ?? "" : "";
            });
        }

        // ---------------------------------------------------------------- user options (userConfig)

        /// <summary>Every option with its value: the user's, else the default; sensitive values come from the DPAPI file.</summary>
        internal Dictionary<string, (string Value, bool Sensitive)> Options(LoadedPlugin p)
        {
            var result = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
            var secrets = ReadSecrets(p.Id);
            foreach (var kv in p.Manifest.UserConfig)
            {
                var sensitive = (bool?)kv.Value["sensitive"] == true;
                string value;
                if (sensitive) value = (string)secrets[kv.Key];
                else value = p.Record.UserConfig?[kv.Key] is JToken t && t.Type != JTokenType.Null ? (string)t : null;
                if (value == null && kv.Value["default"] is JToken d && d.Type != JTokenType.Null) value = d.Type == JTokenType.Boolean ? ((bool)d ? "true" : "false") : (string)d;
                result[kv.Key] = (value, sensitive);
            }
            return result;
        }

        private string SecretsFile(string id) => Path.Combine(SecretsDir, id + ".bin");

        private JObject ReadSecrets(string id)
        {
            try
            {
                var f = SecretsFile(id);
                if (!File.Exists(f)) return new JObject();
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(f), Entropy, DataProtectionScope.CurrentUser);
                return JObject.Parse(Encoding.UTF8.GetString(plain));
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the stored options of {id}: {ex.GetType().Name}");
                return new JObject();
            }
        }

        private void WriteSecrets(string id, JObject values)
        {
            var f = SecretsFile(id);
            if (!values.HasValues)
            {
                if (File.Exists(f)) File.Delete(f);
                return;
            }
            Directory.CreateDirectory(SecretsDir);
            File.WriteAllBytes(f, ProtectedData.Protect(Encoding.UTF8.GetBytes(values.ToString(Formatting.None)), Entropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>Set options (null removes a value). Only keys the plugin declares; sensitive ones are stored encrypted, never in installed.json.</summary>
        public void SetConfig(string id, JObject values)
        {
            var p = Find(id) ?? throw new InvalidOperationException($"No installed plugin \"{id}\".");
            var secrets = ReadSecrets(p.Id);
            var plain = new JObject();
            foreach (var kv in values?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                if (!p.Manifest.UserConfig.TryGetValue(kv.Name, out var opt)) throw new InvalidOperationException($"{p.Name} has no option \"{kv.Name}\".");
                var value = kv.Value == null || kv.Value.Type == JTokenType.Null ? null : kv.Value.Type == JTokenType.Boolean ? ((bool)kv.Value ? "true" : "false") : (string)kv.Value;
                if ((bool?)opt["sensitive"] == true)
                {
                    if (value == null) secrets.Remove(kv.Name);
                    else secrets[kv.Name] = value;
                }
                else plain[kv.Name] = value;
            }
            WriteSecrets(p.Id, secrets);
            Mutate(s =>
            {
                var r = Record(s, p);
                foreach (var kv in plain.Properties())
                {
                    if (kv.Value.Type == JTokenType.Null) r.UserConfig.Remove(kv.Name);
                    else r.UserConfig[kv.Name] = kv.Value;
                }
                return 0;
            });
            Log.Info($"Plugin {p.Id}: options updated ({string.Join(", ", values?.Properties().Select(x => x.Name) ?? new string[0])})");
        }

        /// <summary>The record of a plugin in the state (a local plugin's is created on first use).</summary>
        private PluginRecord Record(State s, LoadedPlugin p)
        {
            if (!s.Plugins.TryGetValue(p.Id, out var r))
            {
                if (!p.Local) throw new InvalidOperationException($"No installed plugin \"{p.Id}\".");
                r = new PluginRecord
                {
                    Name = p.Name, Marketplace = LocalMarketplace, Path = p.Root, Version = p.Manifest?.Version,
                    Source = new JObject { ["source"] = "local" }, InstalledAt = Now(),
                };
                s.Plugins[p.Id] = r;
            }
            return r;
        }

        // ---------------------------------------------------------------- marketplaces

        internal sealed class MarketplaceInfo
        {
            public string Name;
            public PluginSource Source;
            public string SourceText;
            public string Root;
            public bool BuiltIn;
            public bool Removable;
            public bool AutoUpdate;
            public string UpdatedAt;
            public MarketplaceManifest Manifest;
            public string Error;

            /// <summary>
            /// What the panel shows (docs/plugins/local-mcp.md 4): the built-in one's solidworksAgent.displayName, else "SolidWorks Agent" (an older
            /// builtin-plugins copy too); "Your plugins" for local; an added one's display name, else its id.
            /// </summary>
            public string DisplayName =>
                Name == BuiltInMarketplace ? Manifest?.DisplayName ?? "SolidWorks Agent" :
                Name == LocalMarketplace ? "Your plugins" :
                Manifest?.DisplayName ?? Name;
        }

        /// <summary>The built-in "solidworks-agent", the "local" marketplace and every added one, with their plugin lists.</summary>
        public List<MarketplaceInfo> Marketplaces()
        {
            var s = Snapshot();
            var list = new List<MarketplaceInfo>();
            var builtIn = new MarketplaceInfo { Name = BuiltInMarketplace, Root = BuiltInRoot, BuiltIn = true, SourceText = "Built in", AutoUpdate = MarketplaceAutoUpdate(s, BuiltInMarketplace) };
            try
            {
                builtIn.Manifest = File.Exists(MarketplaceManifest.PathIn(BuiltInRoot)) ? MarketplaceManifest.Load(BuiltInRoot) : new MarketplaceManifest { Name = BuiltInMarketplace };
            }
            catch (Exception ex)
            {
                builtIn.Error = ex.Message;
            }
            list.Add(builtIn);

            var local = new MarketplaceInfo { Name = LocalMarketplace, Root = LocalDir, BuiltIn = true, SourceText = LocalDir, Manifest = new MarketplaceManifest { Name = LocalMarketplace } };
            foreach (var p in Installed().Where(x => x.Local))
            {
                local.Manifest.Plugins.Add(new MarketplaceEntry
                {
                    Name = p.Name, Description = p.Manifest.Description, Version = p.Manifest.Version, Keywords = p.Manifest.Keywords,
                    Category = PluginManifest.Str(p.Manifest.SolidWorksAgent["category"]),
                    Source = new PluginSource { Kind = PluginSource.SourceKind.Relative, Path = "./" + p.Name },
                });
            }
            list.Add(local);

            foreach (var kv in s.Marketplaces.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var info = new MarketplaceInfo { Name = kv.Key, Root = kv.Value.Path, Removable = true, UpdatedAt = kv.Value.UpdatedAt, AutoUpdate = MarketplaceAutoUpdate(s, kv.Key) };
                try
                {
                    info.Source = PluginSource.FromJson(kv.Value.Source);
                    info.SourceText = info.Source.ToString();
                    info.Manifest = MarketplaceManifest.Load(kv.Value.Path);
                    if (info.Source.Kind == PluginSource.SourceKind.Url) info.Root = null; // one marketplace.json: no folders in it
                }
                catch (Exception ex)
                {
                    info.Error = ex.Message;
                }
                list.Add(info);
            }
            return list;
        }

        private State Snapshot()
        {
            lock (_lock) return Load();
        }

        /// <summary>Auto-update is on for the built-in marketplace and off for third-party ones unless the user turned it on.</summary>
        private static bool MarketplaceAutoUpdate(State s, string name) =>
            s.Marketplaces.TryGetValue(name, out var r) && r.AutoUpdate.HasValue ? r.AutoUpdate.Value : name == BuiltInMarketplace;

        public MarketplaceInfo AddMarketplace(string input, CancellationToken cancel = default(CancellationToken))
        {
            var source = PluginSource.FromUserInput(input);
            lock (_op)
            {
                string name, path, sha = null;
                if (source.Kind == PluginSource.SourceKind.Folder)
                {
                    if (!File.Exists(MarketplaceManifest.PathIn(source.Path)))
                        throw new InvalidOperationException($"\"{source.Path}\" has no .claude-plugin\\marketplace.json.");
                    name = MarketplaceManifest.Load(source.Path).Name;
                    path = source.Path; // read in place: edits show at once
                    CheckNewMarketplace(name);
                }
                else
                {
                    Directory.CreateDirectory(MarketplacesDir);
                    var tmp = Path.Combine(MarketplacesDir, ".new-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        sha = Fetcher.FetchMarketplace(source, tmp, cancel);
                        if (!File.Exists(MarketplaceManifest.PathIn(tmp))) throw new InvalidOperationException("That repository has no .claude-plugin/marketplace.json.");
                        name = MarketplaceManifest.Load(tmp).Name;
                        CheckNewMarketplace(name);
                        path = Path.Combine(MarketplacesDir, name);
                        if (Directory.Exists(path)) Fetcher.TryDelete(path);
                        Fetcher.MoveDirectory(tmp, path);
                    }
                    finally
                    {
                        Fetcher.TryDelete(tmp);
                    }
                }
                Mutate(s =>
                {
                    s.Marketplaces[name] = new MarketplaceRecord { Source = source.ToJson(), Path = path, UpdatedAt = Now(), Sha = sha };
                    return 0;
                });
                Log.Info($"Added plugin marketplace {name} ({source})");
                return Marketplaces().First(m => m.Name == name);
            }
        }

        private void CheckNewMarketplace(string name)
        {
            if (name == BuiltInMarketplace || name == LocalMarketplace) throw new InvalidOperationException($"\"{name}\" is a reserved marketplace name.");
            if (Snapshot().Marketplaces.ContainsKey(name)) throw new InvalidOperationException($"A marketplace named \"{name}\" is already added. Update it instead.");
        }

        /// <summary>Remove an added marketplace and uninstall its plugins (as Claude Code does).</summary>
        public void RemoveMarketplace(string name)
        {
            if (name == BuiltInMarketplace || name == LocalMarketplace) throw new InvalidOperationException($"The \"{name}\" marketplace can't be removed.");
            lock (_op)
            {
                var s = Snapshot();
                if (!s.Marketplaces.TryGetValue(name ?? "", out var rec)) throw new InvalidOperationException($"No marketplace \"{name}\".");
                foreach (var id in s.Plugins.Values.Where(r => r.Marketplace == name).Select(r => r.Id).ToList()) Uninstall(id);
                var source = PluginSource.FromJson(rec.Source);
                if (source.Kind != PluginSource.SourceKind.Folder && PluginManifest.IsInside(MarketplacesDir, rec.Path)) Fetcher.TryDelete(rec.Path);
                Mutate(st => st.Marketplaces.Remove(name));
                Log.Info($"Removed plugin marketplace {name}");
            }
        }

        /// <summary>Fetch a marketplace again (git: fetch + hard reset; zip or marketplace.json: download again). Built-in and folder ones are read in place.</summary>
        public void UpdateMarketplace(string name, CancellationToken cancel = default(CancellationToken))
        {
            if (name == BuiltInMarketplace || name == LocalMarketplace) return;
            lock (_op)
            {
                var s = Snapshot();
                if (!s.Marketplaces.TryGetValue(name ?? "", out var rec)) throw new InvalidOperationException($"No marketplace \"{name}\".");
                var source = PluginSource.FromJson(rec.Source);
                string sha = rec.Sha;
                if (source.Kind != PluginSource.SourceKind.Folder)
                {
                    if (Fetcher.TryPull(rec.Path, source.Ref, cancel))
                    {
                        sha = Fetcher.GitHead(rec.Path);
                    }
                    else
                    {
                        var tmp = Path.Combine(MarketplacesDir, ".new-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        try
                        {
                            sha = Fetcher.FetchMarketplace(source, tmp, cancel);
                            var fetched = MarketplaceManifest.Load(tmp).Name;
                            if (fetched != name) throw new InvalidOperationException($"The marketplace is now called \"{fetched}\"; remove it and add it again.");
                            Fetcher.TryDelete(rec.Path);
                            Fetcher.MoveDirectory(tmp, rec.Path);
                        }
                        finally
                        {
                            Fetcher.TryDelete(tmp);
                        }
                    }
                }
                Mutate(st =>
                {
                    if (st.Marketplaces.TryGetValue(name, out var r))
                    {
                        r.UpdatedAt = Now();
                        r.Sha = sha;
                    }
                    return 0;
                });
            }
        }

        public void SetMarketplaceAutoUpdate(string name, bool on)
        {
            Mutate(s =>
            {
                if (s.Marketplaces.TryGetValue(name, out var r)) r.AutoUpdate = on;
                else if (name == BuiltInMarketplace) s.Marketplaces.Remove(name); // the built-in one has no record: always on
                return 0;
            });
        }

        /// <summary>
        /// The marketplace entry's solidworksAgent.env for a plugin. The built-in marketplace ships with the add-in, so its current
        /// entry applies at once (e.g. agent-bridge's session id and listening); others use the copy made at install/update.
        /// </summary>
        private JObject EntryEnv(LoadedPlugin p)
        {
            var saved = p.Record.SolidWorksAgent?["env"] as JObject;
            if (p.Record.Marketplace != BuiltInMarketplace) return saved;
            try
            {
                return Entry(BuiltInMarketplace, p.Name).Entry.SolidWorksAgent?["env"] as JObject ?? saved;
            }
            catch (Exception ex)
            {
                Log.Warn($"Reading the built-in entry of {p.Name} failed: {ex.Message}");
                return saved;
            }
        }

        private (MarketplaceInfo Market, MarketplaceEntry Entry) Entry(string marketplace, string name)
        {
            var market = Marketplaces().FirstOrDefault(m => m.Name == marketplace) ?? throw new InvalidOperationException($"No marketplace \"{marketplace}\".");
            if (market.Error != null) throw new InvalidOperationException($"The marketplace \"{marketplace}\" can't be read: {market.Error}");
            var entry = market.Manifest.Plugins.FirstOrDefault(p => p.Name == name) ?? throw new InvalidOperationException($"The marketplace \"{marketplace}\" has no plugin \"{name}\".");
            if (entry.Source == null) throw new InvalidOperationException($"{name}: {entry.SourceError}");
            return (market, entry);
        }

        // ---------------------------------------------------------------- install, enable, update, uninstall

        /// <summary>The result of install / enable / update.</summary>
        internal sealed class Outcome
        {
            public bool Ok;
            public LoadedPlugin Plugin;

            /// <summary>Set when the user must approve what the plugin runs before it can: { hooks: [...], mcp: [...] }.</summary>
            public JObject NeedsTrust;
            public string Hash;
            public bool UpToDate;
            public List<string> Warnings = new List<string>();
            public List<string> AlsoInstalled = new List<string>();

            public JObject ToJson(PluginManager pm)
            {
                var o = new JObject { ["ok"] = Ok };
                if (Plugin != null) o["plugin"] = pm.PluginJson(Plugin);
                if (NeedsTrust != null)
                {
                    o["needsTrust"] = NeedsTrust;
                    o["hash"] = Hash;
                }
                if (UpToDate) o["upToDate"] = true;
                if (Warnings.Count > 0) o["warnings"] = new JArray(Warnings);
                if (AlsoInstalled.Count > 0)
                {
                    o["alsoInstalled"] = new JArray(AlsoInstalled);
                    o["installed"] = new JArray(AlsoInstalled); // the panel's name for it
                }
                if (Warnings.Count > 0) o["note"] = string.Join(" ", Warnings);
                else if (UpToDate) o["note"] = "Already up to date.";
                return o;
            }
        }

        /// <summary>A plugin fetched for install or update, kept while the trust dialog is open (so it isn't downloaded twice).</summary>
        private sealed class Staged
        {
            public string Dir;
            public string Sha;
            public string Hash;
        }

        public static (string Name, string Marketplace) ParseId(string id)
        {
            var m = IdPattern.Match((id ?? "").Trim().ToLowerInvariant());
            if (!m.Success) throw new InvalidOperationException($"\"{id}\" is not a plugin id (name@marketplace).");
            return (m.Groups[1].Value, m.Groups[2].Value);
        }

        /// <summary>
        /// Install a plugin from a marketplace (fetch into cache\&lt;plugin&gt;\&lt;version&gt;, enabled). A plugin with hooks or MCP
        /// servers needs the user's approval: without the matching trustHash nothing is installed and NeedsTrust lists what
        /// it would run. The marketplace entry's solidworksAgent.installWith plugins are installed with it.
        /// </summary>
        public Outcome Install(string id, string trustHash = null, CancellationToken cancel = default(CancellationToken))
        {
            var (name, marketplace) = ParseId(id);
            lock (_op)
            {
                if (marketplace == LocalMarketplace) return Enable(id, true, trustHash);
                var other = Installed().FirstOrDefault(p => p.Name == name);
                if (other != null && other.Record.Marketplace != marketplace)
                    throw new InvalidOperationException($"A plugin named \"{name}\" is already installed from \"{other.Record.Marketplace}\". Uninstall it first.");
                var (market, entry) = Entry(marketplace, name);
                // A link plugin (Claude MCP, Codex MCP): the app must be there and its "solidworks-agent" free before the dialog shows.
                var client = ClientLinks.ClientOf(marketplace, entry.SolidWorksAgent);
                if (client != null) Links.Preflight(client);
                var staged = Stage(id, market, entry, cancel);
                var manifest = PluginManifest.Load(staged.Dir);
                if (manifest.Name != name)
                    throw new InvalidOperationException($"The marketplace lists \"{name}\" but the plugin calls itself \"{manifest.Name}\".");
                var outcome = new Outcome();
                if (client == null && ClientLinks.Declares(entry.SolidWorksAgent)) outcome.Warnings.Add(ClientLinks.NotHonoured);
                CheckNode(manifest, outcome);
                var hash = client == null ? staged.Hash : Links.Combine(staged.Hash, client);
                if (hash != "" && trustHash != hash)
                {
                    outcome.NeedsTrust = TrustList(manifest, staged.Dir, client);
                    outcome.Hash = hash;
                    return outcome;
                }
                var version = VersionOf(manifest, entry, staged);
                var dest = Path.Combine(CacheDir, name, version);
                var previous = other?.Record.Path;
                // Register first: when the other app's config can't be changed, nothing is installed.
                if (client != null) Links.Register(client, name, addIfMissing: true);
                try
                {
                    Place(staged, dest);
                }
                catch
                {
                    if (client != null) Links.Unregister(client, name);
                    throw;
                }
                _staged.Remove(id);
                Mutate(s =>
                {
                    s.Plugins[id] = new PluginRecord
                    {
                        Name = name, Marketplace = marketplace, Version = version, Sha = staged.Sha, Path = dest, Enabled = true,
                        TrustedHash = hash, Source = entry.Source.ToJson(), InstalledAt = other?.Record.InstalledAt ?? Now(), UpdatedAt = Now(),
                        UserConfig = other?.Record.UserConfig ?? new JObject(), SolidWorksAgent = (JObject)entry.SolidWorksAgent.DeepClone(),
                    };
                    return 0;
                });
                if (previous != null && !SamePath(previous, dest)) RemoveFiles(previous);
                Log.Info($"Installed plugin {id} {version}");

                // The overlay that belongs with it (agent-bridge-solidworks with agent-bridge), from the same marketplace.
                foreach (var dep in (entry.SolidWorksAgent["installWith"] as JArray ?? new JArray()).Select(t => (string)t).Where(t => t != null))
                {
                    var depId = dep.Contains("@") ? dep : dep + "@" + marketplace;
                    try
                    {
                        if (Find(depId) != null) continue;
                        var r = Install(depId, null, cancel);
                        if (r.Ok) outcome.AlsoInstalled.Add(depId);
                        else outcome.Warnings.Add($"{depId} needs your approval: install it from the store.");
                    }
                    catch (Exception ex)
                    {
                        outcome.Warnings.Add($"{depId} could not be installed: {ex.Message}");
                    }
                }
                outcome.Ok = true;
                outcome.Plugin = Find(id);
                return outcome;
            }
        }

        /// <summary>Fetch (or reuse the copy fetched for the trust dialog) into a staging folder under cache\&lt;plugin&gt;.</summary>
        private Staged Stage(string id, MarketplaceInfo market, MarketplaceEntry entry, CancellationToken cancel)
        {
            if (_staged.TryGetValue(id, out var old))
            {
                if (Directory.Exists(old.Dir)) return old;
                _staged.Remove(id);
            }
            var dir = Path.Combine(CacheDir, entry.Name, ".staging-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.GetDirectoryName(dir));
            try
            {
                var sha = Fetcher.FetchTo(entry.Source, market.Root, dir, cancel);
                var manifest = PluginManifest.Load(dir) ?? throw new InvalidOperationException("The fetched plugin is empty.");
                var staged = new Staged { Dir = dir, Sha = sha, Hash = manifest.TrustHash() };
                _staged[id] = staged;
                return staged;
            }
            catch
            {
                Fetcher.TryDelete(dir);
                throw;
            }
        }

        /// <summary>The cache folder name: the plugin's version, else the listed one, else the commit (lib.mjs reports a hex folder as CLIENT_VERSION), else a content hash.</summary>
        private static string VersionOf(PluginManifest manifest, MarketplaceEntry entry, Staged staged)
        {
            var v = manifest.Version ?? entry.Version;
            if (v != null && Regex.IsMatch(v, @"^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$")) return v;
            if (staged.Sha != null && Regex.IsMatch(staged.Sha, "^[0-9a-f]{40}$")) return staged.Sha.Substring(0, 12);
            return ContentHash(staged.Dir);
        }

        /// <summary>12 hex characters over every file's path and bytes.</summary>
        internal static string ContentHash(string dir)
        {
            using (var sha = SHA256.Create())
            {
                var root = Path.GetFullPath(dir);
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Encoding.UTF8.GetBytes(f.Substring(root.Length).Replace('\\', '/').ToLowerInvariant());
                    sha.TransformBlock(name, 0, name.Length, null, 0);
                    var bytes = File.ReadAllBytes(f);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return PluginManifest.Hex(sha.Hash).Substring(0, 12);
            }
        }

        /// <summary>Move a staged plugin to its version folder. The same version already there is kept (it may be running).</summary>
        private static void Place(Staged staged, string dest)
        {
            if (Directory.Exists(dest))
            {
                if (ContentHash(dest) == ContentHash(staged.Dir))
                {
                    Fetcher.TryDelete(staged.Dir);
                    return;
                }
                if (!Fetcher.TryDelete(dest)) throw new InvalidOperationException($"{dest} is in use; close what runs from it and try again.");
            }
            Fetcher.MoveDirectory(staged.Dir, dest);
        }

        private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>Delete an old version folder; when it is in use (an MCP server still running) it goes on the list cleaned at start-up.</summary>
        private void RemoveFiles(string dir)
        {
            if (!PluginManifest.IsInside(CacheDir, dir)) return;
            if (!Fetcher.TryDelete(dir)) Mutate(s => { s.Trash.Add(dir); return 0; });
            var parent = Path.GetDirectoryName(dir);
            try
            {
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
            catch (IOException)
            {
                // Not empty after all.
            }
        }

        /// <summary>Delete what an earlier session couldn't (folders that were in use), and stale staging folders.</summary>
        public void CleanUp()
        {
            var s = Snapshot();
            var left = s.Trash.Where(d => Directory.Exists(d) && !Fetcher.TryDelete(d)).ToList();
            if (left.Count != s.Trash.Count) Mutate(st => { st.Trash = left; return 0; });
            var scratch = new List<string>();
            if (Directory.Exists(CacheDir)) scratch.AddRange(Directory.GetDirectories(CacheDir).SelectMany(Directory.GetDirectories));
            if (Directory.Exists(MarketplacesDir)) scratch.AddRange(Directory.GetDirectories(MarketplacesDir));
            foreach (var d in scratch.Where(d => Path.GetFileName(d).StartsWith("."))) Fetcher.TryDelete(d);
        }

        /// <summary>
        /// Enable or disable. Enabling a plugin whose hooks or MCP servers the user hasn't approved (in this exact form): from the
        /// panel (fromAgent false) nothing changes and NeedsTrust is returned until the matching trustHash comes back; from the
        /// agent it is enabled without trust, so its tools, commands and skills work but nothing runs.
        /// </summary>
        public Outcome Enable(string id, bool enabled, string trustHash = null, bool fromAgent = false)
        {
            var p = Find(id) ?? throw new InvalidOperationException($"No installed plugin \"{id}\".");
            var outcome = new Outcome();
            var client = LinkClientOf(p);
            if (client != null && fromAgent)
            {
                var label = p.Manifest?.DisplayName ?? p.Name;
                throw new InvalidOperationException($"{label} changes {SolidWorksAgent.LocalLink.LinkProtocol.Label(client)}'s settings, so only the user can turn it on or off in Plugins.");
            }
            var hash = CurrentHash(p);
            var trusted = hash == "" || hash == p.Record.TrustedHash || trustHash == hash;
            if (enabled && !trusted && !fromAgent)
            {
                if (client != null) Links.Preflight(client);
                CheckNode(p.Manifest, outcome);
                outcome.NeedsTrust = TrustList(p.Manifest, p.Root, client);
                outcome.Hash = hash;
                outcome.Plugin = p;
                return outcome;
            }
            // A link plugin: turning on adds its entry to the other app's config (a failure leaves it off), turning off removes it.
            if (client != null)
            {
                if (enabled) Links.Register(client, p.Name, addIfMissing: true);
                else if (Links.Unregister(client, p.Name) is string warning) outcome.Warnings.Add(warning);
            }
            Mutate(s =>
            {
                var r = Record(s, p);
                r.Enabled = enabled;
                if (enabled && hash != "" && trustHash == hash) r.TrustedHash = hash;
                return 0;
            });
            if (enabled && !trusted) outcome.Warnings.Add("Its hooks, MCP servers and tools with code won't run until you approve them in Plugins.");
            Log.Info($"Plugin {p.Id} {(enabled ? "enabled" : "disabled")}");
            outcome.Ok = true;
            outcome.Plugin = Find(p.Id);
            return outcome;
        }

        /// <summary>
        /// The newer version a plugin's source offers: one waiting for approval, a new listed version, or a new commit of a
        /// git source (its short sha, like the version folders). Null when it is up to date, local, or can't be checked
        /// (offline, marketplace gone). Reads only; never stages or installs anything.
        /// </summary>
        public string AvailableUpdate(string id, CancellationToken cancel = default(CancellationToken))
        {
            var p = Find(id);
            if (p == null || p.Local) return null;
            if (p.Record.PendingUpdate != null) return p.Record.PendingUpdate;
            try
            {
                var entry = Entry(p.Record.Marketplace, p.Name).Entry;
                if (entry.Version != null && entry.Version != p.Record.Version) return entry.Version;
                var remote = entry.Source.Kind == PluginSource.SourceKind.GitHub || entry.Source.Kind == PluginSource.SourceKind.Git || entry.Source.Kind == PluginSource.SourceKind.GitSubdir;
                if (!remote || entry.Version != null || p.Record.Sha == null) return null;
                var sha = Fetcher.RemoteSha(entry.Source, cancel);
                return sha != null && sha != p.Record.Sha ? sha.Substring(0, Math.Min(12, sha.Length)) : null;
            }
            catch (Exception ex)
            {
                Log.Info($"Checking {id} for updates failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Update one plugin: a new listed version or a new commit of its source is fetched; the old version folder goes. An
        /// update that changes the hooks or MCP servers asks again (NeedsTrust); autoUpdate leaves those for the user.
        /// </summary>
        public Outcome Update(string id, string trustHash = null, bool autoUpdate = false, CancellationToken cancel = default(CancellationToken))
        {
            lock (_op)
            {
                var p = Find(id) ?? throw new InvalidOperationException($"No installed plugin \"{id}\".");
                var outcome = new Outcome { Plugin = p };
                if (p.Local)
                {
                    outcome.Ok = outcome.UpToDate = true;
                    return outcome;
                }
                var (market, entry) = Entry(p.Record.Marketplace, p.Name);
                var remote = entry.Source.Kind == PluginSource.SourceKind.GitHub || entry.Source.Kind == PluginSource.SourceKind.Git || entry.Source.Kind == PluginSource.SourceKind.GitSubdir;
                if (remote && !_staged.ContainsKey(p.Id) && (entry.Version == null || entry.Version == p.Record.Version))
                {
                    var sha = Fetcher.RemoteSha(entry.Source, cancel);
                    if (sha != null && sha == p.Record.Sha)
                    {
                        outcome.Ok = outcome.UpToDate = true;
                        return outcome;
                    }
                }
                var staged = Stage(p.Id, market, entry, cancel);
                var manifest = PluginManifest.Load(staged.Dir);
                if (manifest.Name != p.Name) throw new InvalidOperationException($"The new version calls itself \"{manifest.Name}\".");
                var version = VersionOf(manifest, entry, staged);
                var dest = Path.Combine(CacheDir, p.Name, version);
                var client = ClientLinks.ClientOf(p.Record.Marketplace, entry.SolidWorksAgent);
                if (SamePath(dest, p.Root) && Directory.Exists(dest) && ContentHash(dest) == ContentHash(staged.Dir))
                {
                    Fetcher.TryDelete(staged.Dir);
                    _staged.Remove(p.Id);
                    Mutate(s => { if (s.Plugins.TryGetValue(p.Id, out var r)) { r.Sha = staged.Sha ?? r.Sha; r.SolidWorksAgent = (JObject)entry.SolidWorksAgent.DeepClone(); r.PendingUpdate = null; } return 0; });
                    if (!autoUpdate) RefreshLink(p, client, outcome);
                    outcome.Ok = outcome.UpToDate = true;
                    outcome.Plugin = Find(p.Id);
                    return outcome;
                }
                var hash = client == null ? staged.Hash : Links.Combine(staged.Hash, client);
                if (hash != "" && hash != p.Record.TrustedHash && trustHash != hash)
                {
                    if (autoUpdate)
                    {
                        Mutate(s => { if (s.Plugins.TryGetValue(p.Id, out var r)) r.PendingUpdate = version; return 0; });
                        Fetcher.TryDelete(staged.Dir);
                        _staged.Remove(p.Id);
                    }
                    outcome.NeedsTrust = TrustList(manifest, staged.Dir, client);
                    outcome.Hash = hash;
                    return outcome;
                }
                CheckNode(manifest, outcome);
                Place(staged, dest);
                _staged.Remove(p.Id);
                var old = p.Root;
                Mutate(s =>
                {
                    var r = s.Plugins[p.Id];
                    r.Version = version;
                    r.Sha = staged.Sha;
                    r.Path = dest;
                    r.UpdatedAt = Now();
                    r.TrustedHash = hash;
                    r.SolidWorksAgent = (JObject)entry.SolidWorksAgent.DeepClone();
                    r.PendingUpdate = null;
                    return 0;
                });
                if (!SamePath(old, dest)) RemoveFiles(old);
                Log.Info($"Updated plugin {p.Id} to {version}");
                if (!autoUpdate) RefreshLink(p, client, outcome);
                outcome.Ok = true;
                outcome.Plugin = Find(p.Id);
                return outcome;
            }
        }

        /// <summary>
        /// After an update the user asked for: refresh an entry SolidWorks Agent owns (normally no write). A missing entry isn't added
        /// again (the status offers Repair); the start-up auto-update never writes another app's config.
        /// </summary>
        private void RefreshLink(LoadedPlugin p, string client, Outcome outcome)
        {
            if (client == null || !p.Enabled) return;
            try
            {
                Links.Register(client, p.Name, addIfMissing: false);
            }
            catch (InvalidOperationException ex)
            {
                outcome.Warnings.Add(ex.Message);
            }
        }

        /// <summary>
        /// plugins.repairLink: add a link plugin's entry to the other app's config again (the user pressed Repair). Only for an
        /// installed, turned-on and approved link plugin.
        /// </summary>
        public LoadedPlugin RepairLink(string id)
        {
            lock (_op)
            {
                var p = Find(id) ?? throw new InvalidOperationException($"No installed plugin \"{id}\".");
                var client = LinkClientOf(p) ?? throw new InvalidOperationException($"{p.Name} doesn't connect another app.");
                if (!p.Enabled) throw new InvalidOperationException($"Turn {p.Manifest?.DisplayName ?? p.Name} on first.");
                if (!IsTrusted(p)) throw new InvalidOperationException($"Approve the change to {p.Manifest?.DisplayName ?? p.Name} first (Review in Plugins).");
                Links.Register(client, p.Name, addIfMissing: true);
                Invalidate();
                return Find(p.Id);
            }
        }

        /// <summary>
        /// Start-up auto-update (setting pluginAutoUpdate): auto-updating marketplaces are fetched, then their enabled plugins
        /// updated, files only. Updates that need a new approval wait for the user. Returns the ids updated.
        /// </summary>
        public List<string> AutoUpdate(CancellationToken cancel = default(CancellationToken))
        {
            var updated = new List<string>();
            if (!PluginSettings.AutoUpdate) return updated;
            foreach (var m in Marketplaces().Where(x => x.AutoUpdate && x.Name != LocalMarketplace))
            {
                try
                {
                    UpdateMarketplace(m.Name, cancel);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Auto-update of marketplace {m.Name} failed: {ex.Message}");
                    continue;
                }
                foreach (var p in EnabledPlugins().Where(x => x.Record.Marketplace == m.Name).ToList())
                {
                    try
                    {
                        var r = Update(p.Id, null, autoUpdate: true, cancel: cancel);
                        if (r.Ok && !r.UpToDate) updated.Add(p.Id);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Auto-update of {p.Id} failed: {ex.Message}");
                    }
                }
            }
            return updated;
        }

        /// <summary>
        /// Uninstall: its files go (a local plugin's folder is moved to plugins\trash, so the user's work can be recovered); its
        /// data folder stays (Claude Code keeps it too) unless deleteData. A link plugin's entry in the other app's config is removed
        /// first; when that fails the plugin is still uninstalled and the returned warnings say what to remove by hand.
        /// </summary>
        public List<string> Uninstall(string id, bool deleteData = false)
        {
            lock (_op)
            {
                var p = Find(id) ?? throw new InvalidOperationException($"No installed plugin \"{id}\".");
                var warnings = new List<string>();
                var client = LinkClientOf(p);
                if (client != null && Links.Unregister(client, p.Name) is string warning) warnings.Add(warning);
                Mutate(s => s.Plugins.Remove(p.Id));
                if (p.Local)
                {
                    if (Directory.Exists(p.Root))
                    {
                        var to = Path.Combine(TrashDir, p.Name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                        Directory.CreateDirectory(TrashDir);
                        Fetcher.MoveDirectory(p.Root, to);
                    }
                }
                else RemoveFiles(p.Root);
                try
                {
                    if (File.Exists(SecretsFile(p.Id))) File.Delete(SecretsFile(p.Id));
                }
                catch (IOException ex)
                {
                    Log.Warn($"Could not delete the stored options of {p.Id}: {ex.Message}");
                }
                if (deleteData) Fetcher.TryDelete(Path.Combine(DataRoot, p.Name));
                _staged.Remove(p.Id);
                Invalidate();
                Log.Info($"Uninstalled plugin {p.Id}");
                return warnings;
            }
        }

        /// <summary>
        /// What the trust dialog shows: each hook command and MCP server command line (sensitive options left out), and the
        /// tools whose C# runs inside SolidWorks; for a link plugin, the other app's config file it changes and what that runs.
        /// </summary>
        private JObject TrustList(PluginManifest m, string root, string client = null)
        {
            var vars = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CLAUDE_PLUGIN_ROOT"] = root, ["SOLIDWORKS_AGENT_PLUGIN_ROOT"] = root, ["CLAUDE_PLUGIN_DATA"] = Path.Combine(DataRoot, m.Name ?? ""),
                ["SOLIDWORKS_AGENT_DEVICE"] = Environment.MachineName, ["SOLIDWORKS_AGENT_PID"] = Process.GetCurrentProcess().Id.ToString(),
            };
            var none = new Dictionary<string, (string, bool)>();
            var list = new JObject
            {
                ["hooks"] = new JArray(m.HookCommands().Select(h => $"{h.Event}: {Expand(h.Command, vars, none, false)}")),
                ["mcp"] = new JArray(m.McpServers.Select(kv =>
                {
                    var cmd = PluginManifest.Str(kv.Value["command"]);
                    if (cmd == null)
                    {
                        var headers = (kv.Value["headers"] as JObject)?.Properties().Select(x => x.Name).ToList() ?? new List<string>();
                        return $"{kv.Key}: {Mcp.McpHost.TypeOf(kv.Value)} {kv.Value["url"]}" + (headers.Count > 0 ? $" (sends {string.Join(", ", headers)})" : "");
                    }
                    var args = (kv.Value["args"] as JArray ?? new JArray()).Select(a => Fetcher.QuoteArg(Expand((string)a ?? "", vars, none, false)));
                    return $"{kv.Key}: {string.Join(" ", new[] { Expand(cmd, vars, none, false) }.Concat(args))}";
                })),
                ["tools"] = new JArray(m.ToolCodeSummary()),
            };
            if (client != null) list["links"] = new JArray(Links.TrustLine(client));
            return list;
        }

        /// <summary>Hooks and MCP servers that run node need Node.js 18 or newer (global fetch, AbortSignal.timeout).</summary>
        private static void CheckNode(PluginManifest m, Outcome outcome)
        {
            bool IsNode(string cmd) => Regex.IsMatch(Path.GetFileName(ExecutableResolver.FirstToken(cmd ?? "")) ?? "", @"^node(\.exe)?$", RegexOptions.IgnoreCase);
            var needsNode = m.HookCommands().Any(h => IsNode(h.Command)) || m.McpServers.Values.Any(s => IsNode(PluginManifest.Str(s["command"])));
            if (!needsNode) return;
            var v = ExecutableResolver.NodeVersion();
            if (v == null) throw new InvalidOperationException($"{m.DisplayName ?? m.Name} needs Node.js 18 or newer, and Node.js isn't installed. Install it from https://nodejs.org, then try again.");
            if (v.Major < 18) throw new InvalidOperationException($"{m.DisplayName ?? m.Name} needs Node.js 18 or newer; this PC has {v}. Update it from https://nodejs.org, then try again.");
        }

        // ---------------------------------------------------------------- JSON for the panel

        /// <summary>
        /// One installed plugin for plugins.list and the store's Installed tab (ui/src/lib/types.ts PluginInfo, plus the
        /// core's own fields: readOnly, path, tools, hookEvents, trust, findings, pendingUpdate).
        /// </summary>
        public JObject PluginJson(LoadedPlugin p) => PluginJson(p, null);

        /// <param name="markets">The marketplaces, when the caller has read them already (plugins.list reads them once).</param>
        private JObject PluginJson(LoadedPlugin p, List<MarketplaceInfo> markets)
        {
            var m = p.Manifest;
            var hash = CurrentHash(p);
            var options = Options(p);
            var commands = CommandIndex.For(m);
            var skills = SkillIndex.For(m, HiddenSkills(p));
            var agents = AgentIndex.For(m);
            var tools = m.Tools().ToList();
            var notes = new JArray();
            if (m.Hooks["PermissionRequest"] != null) notes.Add("Remote approvals (PermissionRequest hooks) don't run in SolidWorks Agent yet; SolidWorks Agent's own approval cards are used.");
            markets = markets ?? SafeMarketplaces();
            var entry = MarketplaceEntryOf(p, markets);
            var client = LinkClientOf(p);
            if (client == null && !p.Local && ClientLinks.Declares(p.Record.SolidWorksAgent)) notes.Add(ClientLinks.NotHonoured);
            var errors = m.Findings.Where(f => f.StartsWith("error:", StringComparison.Ordinal)).ToList();
            var json = new JObject
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["marketplace"] = p.Record.Marketplace,
                ["marketplaceDisplayName"] = DisplayNameOf(p.Record.Marketplace, markets),
                ["displayName"] = m.DisplayName ?? p.Name,
                ["description"] = m.Description ?? entry?.Description,
                ["version"] = p.Record.Version ?? m.Version,
                ["author"] = m.Author == null ? null : new JObject { ["name"] = m.Author },
                ["homepage"] = PluginManifest.Str(m.Json["homepage"]) ?? PluginManifest.Str(entry?.Json?["homepage"]),
                ["repository"] = PluginManifest.Str(m.Json["repository"]),
                ["license"] = PluginManifest.Str(m.Json["license"]),
                ["keywords"] = new JArray(m.Keywords.Concat(entry?.Keywords ?? new string[0]).Distinct()),
                ["category"] = entry?.Category ?? PluginManifest.Str(m.SolidWorksAgent["category"]),
                ["icon"] = PluginManifest.Str(m.SolidWorksAgent["icon"]),
                ["installed"] = true,
                ["enabled"] = p.Enabled,
                ["trusted"] = hash == "" || hash == p.Record.TrustedHash,
                ["local"] = p.Local,
                ["readOnly"] = !p.Local,
                ["path"] = p.Root,
                ["installedAt"] = p.Record.InstalledAt,
                ["updatedAt"] = p.Record.UpdatedAt,
                ["pendingUpdate"] = p.Record.PendingUpdate,
                ["updateAvailable"] = p.Record.PendingUpdate,
                ["installWith"] = InstallWithIds(p.Record.SolidWorksAgent, p.Record.Marketplace),
                ["error"] = errors.Count > 0 ? errors[0].Substring("error:".Length).Trim() : null,
                ["components"] = new JObject
                {
                    ["tools"] = tools.Count,
                    ["commands"] = commands.Count,
                    ["skills"] = skills.Count,
                    ["agents"] = agents.Count,
                    ["hooks"] = m.HookCommands().Count(),
                    ["mcpServers"] = m.McpServers.Count,
                },
                ["tools"] = new JArray(tools.Select(t => p.Name + ":" + t.Name)),
                ["hookEvents"] = new JArray(m.Hooks.Properties().Select(x => x.Name)),
                ["mcpServers"] = new JArray(m.McpServers.Keys),
                ["userConfig"] = new JObject(m.UserConfig.Select(kv => new JProperty(kv.Key, new JObject
                {
                    ["type"] = PluginManifest.Str(kv.Value["type"]) ?? "string",
                    ["title"] = PluginManifest.Str(kv.Value["title"]) ?? kv.Key,
                    ["description"] = PluginManifest.Str(kv.Value["description"]),
                    ["default"] = kv.Value["default"]?.DeepClone(),
                    ["sensitive"] = (bool?)kv.Value["sensitive"] == true,
                    ["required"] = (bool?)kv.Value["required"] == true,
                }))),
                // a sensitive value never goes back to the panel: only whether one is set
                ["config"] = new JObject(m.UserConfig.Select(kv => new JProperty(kv.Key, (bool?)kv.Value["sensitive"] == true
                    ? new JObject { ["set"] = options[kv.Key].Value != null }
                    : p.Record.UserConfig[kv.Key]?.DeepClone()))),
                ["trust"] = new JObject
                {
                    ["needed"] = hash != "",
                    ["approved"] = hash == "" || hash == p.Record.TrustedHash,
                    ["hash"] = hash,
                },
                ["findings"] = new JArray(m.Findings),
                ["notes"] = notes,
            };
            if (client != null) json["link"] = LinkStatus(client, p.Name, p.Enabled);
            return json;
        }

        /// <summary>A link plugin's status for the Plugins screen; a failure to read it is a note, never an error for the list.</summary>
        private JObject LinkStatus(string client, string plugin, bool enabled)
        {
            try
            {
                return Links.Status(client, plugin, enabled);
            }
            catch (Exception ex)
            {
                Log.Warn($"The link status of {plugin} can't be read: {ex.Message}");
                return new JObject { ["client"] = client, ["label"] = SolidWorksAgent.LocalLink.LinkProtocol.Label(client), ["found"] = false, ["entry"] = "unreadable", ["note"] = ex.Message };
            }
        }

        /// <summary>The marketplaces, or none when they can't be read (the list still shows).</summary>
        private List<MarketplaceInfo> SafeMarketplaces()
        {
            try
            {
                return Marketplaces();
            }
            catch (Exception ex)
            {
                Log.Warn($"The marketplaces can't be read: {ex.Message}");
                return new List<MarketplaceInfo>();
            }
        }

        /// <summary>A marketplace id's display name ("SolidWorks Agent" for "solidworks-agent", "Your plugins" for "local").</summary>
        private static string DisplayNameOf(string marketplace, List<MarketplaceInfo> markets) =>
            markets?.FirstOrDefault(x => x.Name == marketplace)?.DisplayName ??
            (marketplace == BuiltInMarketplace ? "SolidWorks Agent" : marketplace == LocalMarketplace ? "Your plugins" : marketplace);

        /// <summary>The marketplace entry's solidworksAgent.hideSkills: Claude Code-only skills that would mislead the agent here.</summary>
        internal static HashSet<string> HiddenSkills(LoadedPlugin p) =>
            new HashSet<string>((p.Record.SolidWorksAgent?["hideSkills"] as JArray ?? new JArray()).Select(t => (string)t).Where(t => t != null), StringComparer.Ordinal);

        /// <summary>The marketplaces with their plugins, for the store's Discover and Marketplaces tabs.</summary>
        public JArray MarketplacesJson()
        {
            var installed = Installed();
            return new JArray(Marketplaces().Select(m => new JObject
            {
                ["name"] = m.Name,
                ["displayName"] = m.DisplayName,
                ["source"] = m.SourceText,
                ["builtIn"] = m.BuiltIn,
                ["removable"] = m.Removable,
                ["autoUpdate"] = m.AutoUpdate,
                ["updatedAt"] = m.UpdatedAt,
                ["owner"] = m.Manifest?.Owner,
                ["description"] = m.Manifest?.Description,
                ["error"] = m.Error,
                ["findings"] = new JArray(m.Manifest?.Findings ?? new List<string>()),
                ["pluginCount"] = m.Manifest?.Plugins.Count ?? 0,
                ["plugins"] = new JArray((m.Manifest?.Plugins ?? new List<MarketplaceEntry>()).Select(e =>
                {
                    var id = e.Name + "@" + m.Name;
                    var have = installed.FirstOrDefault(x => x.Id == id);
                    return new JObject
                    {
                        ["id"] = id,
                        ["name"] = e.Name,
                        ["marketplace"] = m.Name,
                        ["marketplaceDisplayName"] = m.DisplayName,
                        ["description"] = e.Description,
                        ["version"] = e.Version,
                        ["category"] = e.Category,
                        ["keywords"] = new JArray(e.Keywords),
                        ["source"] = e.Source?.ToString(),
                        ["sourceError"] = e.SourceError,
                        ["installed"] = have != null,
                        ["enabled"] = have?.Enabled ?? false,
                        ["installWith"] = e.SolidWorksAgent["installWith"]?.DeepClone(),
                    };
                })),
            }));
        }

        /// <summary>
        /// plugins.list: every installed plugin (PluginJson) and every marketplace plugin not installed yet, so Discover
        /// and Installed read one list.
        /// </summary>
        public JArray PluginsJson()
        {
            var installed = Installed();
            var markets = Marketplaces();
            var list = new JArray(installed.Select(p => PluginJson(p, markets)));
            foreach (var m in markets.Where(x => x.Manifest != null && x.Name != LocalMarketplace))
                foreach (var e in m.Manifest.Plugins)
                {
                    var id = e.Name + "@" + m.Name;
                    if (installed.Any(x => x.Id == id)) continue;
                    list.Add(EntryJson(m, e, id));
                }
            return list;
        }

        /// <summary>A marketplace plugin that isn't installed. A "./folder" entry is read for its manifest; a remote one shows what the entry says.</summary>
        private JObject EntryJson(MarketplaceInfo market, MarketplaceEntry e, string id)
        {
            PluginManifest m = null;
            if (e.Source?.Kind == PluginSource.SourceKind.Relative && market.Root != null)
            {
                var dir = Path.GetFullPath(Path.Combine(market.Root, e.Source.Path));
                if (PluginManifest.IsInside(market.Root, dir)) m = PluginManifest.Load(dir);
            }
            var hidden = new HashSet<string>((e.SolidWorksAgent["hideSkills"] as JArray ?? new JArray()).Select(t => (string)t).Where(t => t != null), StringComparer.Ordinal);
            // A link plugin: where it would write, or why it can't (that also disables Install, as any error does).
            var client = ClientLinks.ClientOf(market.Name, e.SolidWorksAgent);
            JObject link = null;
            if (client != null)
            {
                var loc = Links.Locate(client);
                link = new JObject
                {
                    ["client"] = client, ["label"] = SolidWorksAgent.LocalLink.LinkProtocol.Label(client), ["found"] = loc.Found,
                    ["configPath"] = loc.ConfigPath, ["note"] = loc.Note,
                };
            }
            var json = new JObject
            {
                ["id"] = id,
                ["name"] = e.Name,
                ["marketplace"] = market.Name,
                ["marketplaceDisplayName"] = market.DisplayName,
                ["displayName"] = m?.DisplayName ?? e.Name,
                ["description"] = e.Description ?? m?.Description,
                ["version"] = e.Version ?? m?.Version,
                ["author"] = m?.Author == null ? null : new JObject { ["name"] = m.Author },
                ["homepage"] = PluginManifest.Str(e.Json?["homepage"]) ?? PluginManifest.Str(m?.Json["homepage"]),
                ["license"] = PluginManifest.Str(m?.Json["license"]),
                ["category"] = e.Category ?? PluginManifest.Str(m?.SolidWorksAgent["category"]),
                ["keywords"] = new JArray(e.Keywords),
                ["installed"] = false,
                ["enabled"] = false,
                ["trusted"] = client == null && (m == null || (!m.HasRuntime && !m.HasToolCode)),
                ["local"] = false,
                ["components"] = new JObject
                {
                    ["tools"] = m?.Tools().Count() ?? 0,
                    ["commands"] = m == null ? 0 : CommandIndex.For(m).Count,
                    ["skills"] = m == null ? 0 : SkillIndex.For(m, hidden).Count,
                    ["agents"] = m == null ? 0 : AgentIndex.For(m).Count,
                    ["hooks"] = m?.HookCommands().Count() ?? 0,
                    ["mcpServers"] = m?.McpServers.Count ?? 0,
                },
                ["installWith"] = InstallWithIds(e.SolidWorksAgent, market.Name),
                ["error"] = e.Source == null ? e.SourceError : market.Error ?? (link != null && !(bool)link["found"] ? (string)link["note"] : null),
            };
            if (link != null) json["link"] = link;
            if (client == null && ClientLinks.Declares(e.SolidWorksAgent)) json["notes"] = new JArray(ClientLinks.NotHonoured);
            return json;
        }

        /// <summary>A marketplace entry's solidworksAgent.installWith as plugin ids (the companions come from the same marketplace).</summary>
        private static JArray InstallWithIds(JObject ext, string marketplace) =>
            new JArray((ext?["installWith"] as JArray ?? new JArray()).Select(t => (string)t).Where(t => !string.IsNullOrEmpty(t))
                .Select(n => n.Contains("@") ? n : n + "@" + marketplace));

        /// <summary>The installed plugin's entry in its marketplace (for the store's tags), or null.</summary>
        private MarketplaceEntry MarketplaceEntryOf(LoadedPlugin p, List<MarketplaceInfo> markets)
        {
            try
            {
                return (markets ?? Marketplaces()).FirstOrDefault(x => x.Name == p.Record.Marketplace)?.Manifest?.Plugins.FirstOrDefault(x => x.Name == p.Name);
            }
            catch (Exception ex)
            {
                Log.Warn($"The marketplace entry of {p.Id} can't be read: {ex.Message}");
                return null;
            }
        }

        private static string Now() => DateTime.UtcNow.ToString("o");
    }
}
