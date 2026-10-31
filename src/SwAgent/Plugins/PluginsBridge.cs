using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Tools;
using SwAgent.UI;

namespace SwAgent.Plugins
{
    /// <summary>
    /// The panel's plugin RPCs (design 8, track A). Installs, updates and marketplace fetches run on a background thread
    /// (files and network only); nothing here touches SolidWorks.
    /// <list type="bullet">
    /// <item>plugins.list() -> [plugin] (PluginManager.PluginJson: id, name, marketplace, displayName, description, version,
    ///   enabled, local, readOnly, components {tools, commands, skills, agents, hooks, mcpServers}, tools, hookEvents,
    ///   mcpServers, userConfig [{key, type, title, description, default, sensitive, value, isSet}], trust {needed,
    ///   approved, hash}, findings, notes, pendingUpdate, ...)</item>
    /// <item>plugins.marketplaces() -> [{ name, source, builtIn, removable, autoUpdate, updatedAt, owner, description, error,
    ///   findings, plugins: [{ id, name, marketplace, description, version, category, keywords, source, sourceError,
    ///   installed, enabled, installWith }] }]</item>
    /// <item>plugins.install({ id, trustHash? }), plugins.enable({ id, enabled, trustHash? }), plugins.update({ id?, trustHash? })
    ///   -> { ok, plugin?, needsTrust?: { hooks: [line], mcp: [line] }, hash?, upToDate?, warnings?, alsoInstalled? }.
    ///   needsTrust: show the trust dialog, then call again with trustHash = hash. update without id updates every enabled
    ///   plugin: { ok, updated: [id], needsTrust: [{ id, needsTrust, hash }], errors: [text] }.
    ///   plugins.checkUpdates() -> { id: newer version | null } (reads only; git sources ask the remote).</item>
    /// <item>plugins.uninstall({ id, deleteData? }) -> { ok, note? } (note: what to remove by hand when a link plugin's entry
    ///   couldn't be); plugins.setConfig({ id, values: { key: value | null } }) -> plugin;
    ///   plugins.repairLink({ id }) -> plugin (adds a Claude MCP / Codex MCP entry to the other app's config again)</item>
    /// <item>plugins.addMarketplace({ source }) -> marketplace; plugins.removeMarketplace({ name });
    ///   plugins.updateMarketplace({ name? }) -> marketplaces; plugins.setMarketplaceAutoUpdate({ name, autoUpdate })</item>
    /// <item>plugins.commands() / plugins.skills() / plugins.agents() -> the enabled plugins' components (CommandIndex,
    ///   SkillIndex, AgentIndex ToJson); plugins.expandCommand({ name, args }) -> { text, model?, note? };
    ///   plugins.readme({ id }) -> { text }</item>
    /// <item>event plugins.changed: the plugins.list array, after any change (userTools.changed and ribbon.stale follow).</item>
    /// </list>
    /// </summary>
    internal static class PluginsBridge
    {
        private static bool _subscribed;
        private static WebBridge _bridge;
        private static SynchronizationContext _ui;

        /// <summary>Register the RPCs (again on every panel reload); subscribe and start the background update once.</summary>
        public static void Attach(WebBridge bridge)
        {
            _bridge = bridge;
            var pm = PluginManager.Instance;
            bridge.Register("plugins.list", p => pm.PluginsJson());
            bridge.Register("plugins.marketplaces", p => pm.MarketplacesJson());
            bridge.RegisterAsync("plugins.install", async p =>
                (await Background(() => pm.Install(Id(p), (string)p["trustHash"]))).ToJson(pm));
            bridge.RegisterAsync("plugins.enable", async p =>
                (await Background(() => pm.Enable(Id(p), (bool?)p["enabled"] != false, (string)p["trustHash"]))).ToJson(pm));
            bridge.RegisterAsync("plugins.update", async p => await Background(() => Update(pm, (string)p?["id"], (string)p?["trustHash"])));
            // { id: newer version | null } for the installed plugins that come from a source (git sources ask the remote).
            bridge.RegisterAsync("plugins.checkUpdates", async p => await Background(() =>
                new JObject(pm.Installed().Where(x => !x.Local).Select(x => new JProperty(x.Id, pm.AvailableUpdate(x.Id))))));
            bridge.RegisterAsync("plugins.uninstall", async p =>
            {
                var warnings = await Background(() => pm.Uninstall(Id(p), (bool?)p["deleteData"] == true));
                return new JObject { ["ok"] = true, ["note"] = warnings.Count > 0 ? string.Join(" ", warnings) : null };
            });
            // A link plugin's Repair button: add its "solidworks-agent" entry to Claude Desktop's or Codex's config again.
            bridge.RegisterAsync("plugins.repairLink", async p => pm.PluginJson(await Background(() => pm.RepairLink(Id(p)))));
            bridge.Register("plugins.setConfig", p =>
            {
                pm.SetConfig(Id(p), p["values"] as JObject ?? new JObject());
                return pm.PluginJson(pm.Find(Id(p)));
            });
            bridge.RegisterAsync("plugins.addMarketplace", async p =>
            {
                var m = await Background(() => pm.AddMarketplace((string)p["source"]));
                return pm.MarketplacesJson().FirstOrDefault(x => (string)x["name"] == m.Name);
            });
            bridge.RegisterAsync("plugins.removeMarketplace", async p =>
            {
                await Background(() =>
                {
                    pm.RemoveMarketplace((string)p["name"]);
                    return 0;
                });
                return null;
            });
            bridge.RegisterAsync("plugins.updateMarketplace", async p =>
            {
                var name = (string)p?["name"];
                await Background(() =>
                {
                    foreach (var m in pm.Marketplaces().Where(m => name == null ? m.Removable : m.Name == name)) pm.UpdateMarketplace(m.Name);
                    return 0;
                });
                return pm.MarketplacesJson();
            });
            bridge.Register("plugins.setMarketplaceAutoUpdate", p =>
            {
                pm.SetMarketplaceAutoUpdate((string)p["name"], (bool?)p["autoUpdate"] == true);
                return null;
            });
            bridge.Register("plugins.commands", p => new JArray(CommandIndex.All(pm).Select(c => c.ToJson())));
            bridge.Register("plugins.skills", p => new JArray(SkillIndex.All(pm).Select(s => s.ToJson())));
            bridge.Register("plugins.agents", p => new JArray(AgentIndex.All(pm).Select(a => a.ToJson())));
            bridge.Register("plugins.expandCommand", p => CommandIndex.Expand(pm, (string)p["name"], (string)p["args"] ?? ""));
            bridge.Register("plugins.readme", p =>
            {
                var plugin = pm.Find(Id(p)) ?? throw new InvalidOperationException($"No installed plugin \"{p["id"]}\".");
                var file = new[] { "README.md", "readme.md", "README" }.Select(f => System.IO.Path.Combine(plugin.Root, f)).FirstOrDefault(System.IO.File.Exists);
                return new JObject { ["text"] = file == null ? plugin.Manifest.Description : System.IO.File.ReadAllText(file) };
            });

            if (_subscribed) return;
            _subscribed = true; // the bridge is attached again when the panel reloads
            _ui = SynchronizationContext.Current;
            pm.Changed += OnChanged;
            // Start-up: leftovers of earlier sessions, then auto-update (files only) once SolidWorks has settled.
            Task.Run(async () =>
            {
                try
                {
                    pm.CleanUp();
                    // The per-user copy of the Claude/Codex link program (files only; never a client config write).
                    pm.Links.Deployer.Ensure();
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    var updated = pm.AutoUpdate();
                    if (updated.Count > 0) Log.Info("Plugins updated at start-up: " + string.Join(", ", updated));
                }
                catch (Exception ex)
                {
                    Log.Warn("Plugin start-up update failed: " + ex.Message);
                }
            });
        }

        /// <summary>A change (from any thread): tell the panel on the UI thread; the tool index and ribbon follow.</summary>
        private static void OnChanged()
        {
            void Notify() => Safe.Run("Plugins changed", () =>
            {
                _bridge?.Emit("plugins.changed", PluginManager.Instance.PluginsJson());
                UserTools.NotifyChanged();
            });
            if (_ui != null && SynchronizationContext.Current != _ui) _ui.Post(_ => Notify(), null);
            else Notify();
        }

        private static string Id(JToken p) => (string)p?["id"] ?? throw new InvalidOperationException("Pass the plugin's id (name@marketplace).");

        private static Task<T> Background<T>(Func<T> work) => Task.Run(work);

        /// <summary>One plugin, or every enabled one (each needing approval is reported, not applied).</summary>
        private static JObject Update(PluginManager pm, string id, string trustHash)
        {
            if (id != null) return pm.Update(id, trustHash).ToJson(pm);
            var updated = new JArray();
            var trust = new JArray();
            var errors = new JArray();
            foreach (var p in pm.EnabledPlugins().Where(x => !x.Local).ToList())
            {
                try
                {
                    var r = pm.Update(p.Id);
                    if (r.NeedsTrust != null) trust.Add(new JObject { ["id"] = p.Id, ["needsTrust"] = r.NeedsTrust, ["hash"] = r.Hash });
                    else if (r.Ok && !r.UpToDate) updated.Add(p.Id);
                }
                catch (Exception ex)
                {
                    errors.Add($"{p.Id}: {ex.Message}");
                }
            }
            return new JObject { ["ok"] = errors.Count == 0, ["updated"] = updated, ["needsTrust"] = trust, ["errors"] = errors };
        }
    }
}
