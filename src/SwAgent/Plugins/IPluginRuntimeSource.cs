using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SwAgent.Plugins
{
    /// <summary>
    /// The seam between the plugin core (installs, trust, settings: PluginManager) and the runtime that runs plugin hooks
    /// and MCP servers (track B). The runtime only reads this; it never looks at installed.json or plugin folders itself.
    /// </summary>
    internal interface IPluginRuntimeSource
    {
        /// <summary>
        /// The plugins whose hooks and MCP servers may run now: enabled, trusted (the hash of their current hooks and MCP
        /// servers is the one the user approved) and allowed by the settings (pluginHooks / pluginMcp: a disallowed kind is
        /// left empty).
        /// </summary>
        IReadOnlyList<PluginRuntime> Runnable();

        /// <summary>Something that changes Runnable() happened (install, enable, trust, update, settings, a local edit). Raised off the UI thread.</summary>
        event Action Changed;
    }

    /// <summary>One plugin as the runtime sees it. A snapshot: Runnable() builds new ones after every change.</summary>
    internal sealed class PluginRuntime
    {
        /// <summary>"name@marketplace", e.g. "agent-bridge@solidworks-agent".</summary>
        public string Id { get; set; }

        /// <summary>The plugin's name (kebab-case), e.g. "agent-bridge".</summary>
        public string Name { get; set; }

        /// <summary>The installed plugin folder (CLAUDE_PLUGIN_ROOT).</summary>
        public string Root { get; set; }

        /// <summary>Its writable folder, kept across updates (CLAUDE_PLUGIN_DATA).</summary>
        public string DataDir { get; set; }

        /// <summary>
        /// Extra environment for its hooks and MCP servers, values already expanded: the marketplace entry's solidworksAgent.env, then
        /// plugin.json's solidworksAgent.env, then the user's options as CLAUDE_PLUGIN_OPTION_&lt;KEY&gt; (sensitive ones too: env is
        /// where secrets go, never a command line), plus CLAUDE_PLUGIN_ROOT, CLAUDE_PLUGIN_DATA, SOLIDWORKS_AGENT_PLUGIN_ROOT, SOLIDWORKS_AGENT=1.
        /// </summary>
        public IDictionary<string, string> Env { get; set; }

        /// <summary>
        /// Expands ${CLAUDE_PLUGIN_ROOT}, ${SOLIDWORKS_AGENT_PLUGIN_ROOT}, ${CLAUDE_PLUGIN_DATA}, ${SOLIDWORKS_AGENT_DEVICE}, ${SOLIDWORKS_AGENT_PID} and
        /// ${user_config.KEY}; anything else (${ENV_VAR}, ${ENV_VAR:-default}) is left as written, for Session.Expand to fill
        /// from the program's own environment. Command and skill text uses only this. A sensitive option expands to "" here
        /// (read it from CLAUDE_PLUGIN_OPTION_&lt;KEY&gt; in Env instead).
        /// </summary>
        public Func<string, string> Expand { get; set; }

        /// <summary>Only ${user_config.KEY} (non-sensitive): hook commands run by bash, which expands the rest from Env.</summary>
        public Func<string, string> ExpandOptions { get; set; }

        /// <summary>
        /// Normalized hooks: { "Event": [ { "matcher": "...", "hooks": [ { "type", "command", "timeout", "async",
        /// "asyncRewake", "rewakeMessage" } ] } ] }. Commands are as written (expand them with Expand).
        /// </summary>
        public JObject Hooks { get; set; }

        /// <summary>
        /// MCP servers by name: { "command", "args", "env", "cwd" } or { "type": "http" | "sse", "url", "headers" }. command and
        /// args are as written (expand with Expand); env, url and header values are already expanded, sensitive options
        /// included (a remote server's key goes in a header).
        /// </summary>
        public IDictionary<string, JObject> McpServers { get; set; }

        /// <summary>The hash of its current hooks and MCP servers ("" when it has none).</summary>
        public string TrustHash { get; set; }
    }
}
