using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Tools;
using SwAgent.UI;

namespace SwAgent.Memory
{
    /// <summary>
    /// The user's preferred parameters (docs/preferred-parameters.md): save_preference and forget_preference. Files only
    /// (SwBusyGate.ModelFreeTools). The file itself is in the system prompt, so there's no list tool.
    /// </summary>
    public sealed class PreferenceTools
    {
        private readonly Func<PreferenceStore> _store;

        public PreferenceTools()
            : this(() => PreferenceStore.Instance)
        {
        }

        internal PreferenceTools(Func<PreferenceStore> store)
        {
            _store = store;
        }

        public sealed class SaveInput
        {
            [ToolParam("A short topic heading it belongs under, reusing an existing one when it fits: e.g. \"Fasteners\", \"3D printing (FDM)\", " +
                       "\"Sheet metal\", \"Walls and ribs\".", Required = true)]
            public string Topic { get; set; }

            [ToolParam("One value and where it applies, specific enough to reuse: e.g. \"M5 self-tapping pilot holes in FDM parts: Ø4.6 mm\", " +
                       "\"default fillet on cosmetic outside edges: R1 mm\".", Required = true)]
            public string Text { get; set; }

            [ToolParam("Ids of entries this one replaces (an older value for the same thing). Undo puts them back.")]
            public string[] Replaces { get; set; }
        }

        [AgentTool("save_preference",
            "Save a design value the user prefers to the preferred parameters file, so you use it by default from now on: when they pick or " +
            "correct a size, clearance, hole, thickness or other parameter that applies beyond this one part. Replace an older entry for the same thing.")]
        public ToolResult Save(ToolContext ctx, SaveInput input)
        {
            var r = _store().Save(input.Topic, input.Text, input.Replaces);
            if (r.Existed) return ToolResult.Ok($"Already saved under {r.Saved.Topic}: {r.Saved.Text} (id {r.Saved.Id}).");
            var sb = new StringBuilder($"Saved under {r.Saved.Topic}: {r.Saved.Text} (id {r.Saved.Id}).");
            foreach (var p in r.Replaced) sb.Append($" Replaced: {p.Text}.");
            if (r.Warning != null) sb.Append(' ').Append(r.Warning);
            sb.Append(" Mention it in one line; the chat shows it with Undo.");
            return ToolResult.Ok(sb.ToString());
        }

        public sealed class ForgetInput
        {
            [ToolParam("The entry's id (in the Preferred parameters section of your instructions).", Required = true)]
            public string Id { get; set; }
        }

        [AgentTool("forget_preference", "Delete one entry from the preferred parameters file, when the user asks or it no longer applies.")]
        public ToolResult Forget(ToolContext ctx, ForgetInput input)
        {
            var hit = _store().Forget(input.Id);
            return hit == null
                ? ToolResult.Error($"No preferred parameter with id {input.Id}.")
                : ToolResult.Ok($"Deleted from {hit.Topic}: {hit.Text}");
        }
    }

    /// <summary>
    /// The panel's RPCs: prefs.index() (the system prompt's section and Options), prefs.edit({ id, text }), prefs.forget({ id }),
    /// prefs.undo({ id }) (the chat's chip), prefs.openFile() (in the user's editor); the event prefs.changed when entries change.
    /// </summary>
    internal static class PreferenceBridge
    {
        private static WebBridge _bridge;
        private static bool _subscribed;

        public static void Attach(WebBridge bridge)
        {
            _bridge = bridge;
            var store = PreferenceStore.Instance;
            bridge.Register("prefs.index", p => store.Index());
            bridge.Register("prefs.edit", p => (object)store.Edit((string)p["id"], (string)p["text"])?.ToJson());
            bridge.Register("prefs.forget", p => store.Forget((string)p["id"]) != null);
            bridge.Register("prefs.undo", p => store.Undo((string)p["id"]));
            bridge.Register("prefs.openFile", p =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(store.EnsureFile()) { UseShellExecute = true });
                return true;
            });
            if (_subscribed) return; // attached again when the panel reloads
            _subscribed = true;
            store.Changed += () => _bridge?.Emit("prefs.changed", new JObject());
        }
    }
}
