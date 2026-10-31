using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SwAgent.Plugins.Hooks
{
    /// <summary>The Claude Code hook events SolidWorks Agent fires, and how a hook's matcher selects them.</summary>
    internal static class HookEvents
    {
        public const string SessionStart = "SessionStart";
        public const string UserPromptSubmit = "UserPromptSubmit";
        public const string PreToolUse = "PreToolUse";
        public const string PostToolUse = "PostToolUse";
        public const string Stop = "Stop";
        public const string SessionEnd = "SessionEnd";

        public static readonly string[] All = { SessionStart, UserPromptSubmit, PreToolUse, PostToolUse, Stop, SessionEnd };

        /// <summary>The events the panel fires through hooks.fire (SessionStart and SessionEnd belong to the add-in).</summary>
        public static readonly string[] PanelFired = { UserPromptSubmit, PreToolUse, PostToolUse, Stop };

        /// <summary>Events whose matcher means something: the tool name, or SessionStart's source.</summary>
        public static bool UsesMatcher(string ev) => ev == PreToolUse || ev == PostToolUse || ev == SessionStart;

        private static readonly Regex Simple = new Regex(@"^[A-Za-z0-9_|]+$", RegexOptions.Compiled);

        /// <summary>
        /// Claude Code's matcher: empty or "*" matches everything; letters, digits, _ and | are exact names (Edit|Write);
        /// anything else is a regular expression searched in the value (mcp__plugin_x_.*). An invalid one matches nothing.
        /// </summary>
        public static bool Matches(string matcher, string value)
        {
            if (string.IsNullOrEmpty(matcher) || matcher == "*") return true;
            value = value ?? "";
            if (Simple.IsMatch(matcher)) return matcher.Split('|').Any(m => m == value);
            try
            {
                return Regex.IsMatch(value, matcher, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// What the hooks of one event decided together (the hooks.fire result). Several hooks run in parallel: any block wins,
    /// deny beats ask beats allow, contexts and notices are concatenated, any continue:false wins.
    /// </summary>
    internal sealed class HookOutcome
    {
        public bool Block;
        public List<string> Reasons = new List<string>();
        public List<string> AdditionalContext = new List<string>();
        public List<string> SystemMessages = new List<string>();
        public bool Continue = true;
        public string StopReason;
        public string PermissionDecision;
        public string PermissionDecisionReason;

        private static int Rank(string decision) => decision == "deny" ? 3 : decision == "ask" ? 2 : decision == "allow" ? 1 : 0;

        public void Decide(string decision, string reason)
        {
            if (Rank(decision) == 0 || Rank(decision) <= Rank(PermissionDecision)) return;
            PermissionDecision = decision;
            PermissionDecisionReason = reason;
        }

        public void Merge(HookOutcome o)
        {
            Block |= o.Block;
            Reasons.AddRange(o.Reasons);
            AdditionalContext.AddRange(o.AdditionalContext);
            SystemMessages.AddRange(o.SystemMessages);
            if (!o.Continue)
            {
                Continue = false;
                StopReason = StopReason ?? o.StopReason;
            }
            Decide(o.PermissionDecision, o.PermissionDecisionReason);
        }

        /// <summary>
        /// One hook's result, read the Claude Code way. Exit 0: stdout may be JSON (plain text is context for
        /// UserPromptSubmit and SessionStart). Exit 2: blocking, stderr is the reason (PreToolUse: deny). Other: a
        /// non-blocking error (logged by the caller).
        /// </summary>
        public static HookOutcome Parse(string ev, int exitCode, string stdout, string stderr)
        {
            var o = new HookOutcome();
            if (exitCode == 2)
            {
                var reason = (stderr ?? "").Trim();
                if (reason.Length == 0) reason = (stdout ?? "").Trim();
                if (ev == HookEvents.SessionStart || ev == HookEvents.SessionEnd)
                {
                    if (reason.Length > 0) o.SystemMessages.Add(reason); // shown to the user only
                    return o;
                }
                o.Block = true;
                if (reason.Length > 0) o.Reasons.Add(reason);
                if (ev == HookEvents.PreToolUse) o.Decide("deny", reason);
                return o;
            }
            if (exitCode != 0) return o;

            var text = (stdout ?? "").Trim();
            if (text.Length == 0) return o;
            JObject json = null;
            if (text.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    json = JObject.Parse(text);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    // Not JSON after all: plain text.
                }
            }
            if (json == null)
            {
                if (ev == HookEvents.UserPromptSubmit || ev == HookEvents.SessionStart) o.AdditionalContext.Add(text);
                return o;
            }

            if (json["continue"]?.Type == JTokenType.Boolean && !(bool)json["continue"])
            {
                o.Continue = false;
                o.StopReason = (string)json["stopReason"];
            }
            var message = (string)json["systemMessage"];
            if (!string.IsNullOrWhiteSpace(message)) o.SystemMessages.Add(message);

            var decision = (string)json["decision"];
            var why = (string)json["reason"];
            if (decision == "block")
            {
                if (ev == HookEvents.PreToolUse) o.Decide("deny", why); // the older PreToolUse form
                else o.Block = true;
                if (!string.IsNullOrWhiteSpace(why)) o.Reasons.Add(why);
            }
            else if (decision == "approve" && ev == HookEvents.PreToolUse)
            {
                o.Decide("allow", why);
            }

            if (json["hookSpecificOutput"] is JObject specific)
            {
                var context = specific["additionalContext"];
                if (context?.Type == JTokenType.String && ((string)context).Trim().Length > 0) o.AdditionalContext.Add((string)context);
                if (ev == HookEvents.PreToolUse) o.Decide((string)specific["permissionDecision"], (string)specific["permissionDecisionReason"]);
            }
            if (o.PermissionDecision == "deny") o.Block = true;
            return o;
        }

        public JObject ToJson() => new JObject
        {
            ["block"] = Block,
            ["reason"] = Reasons.Count == 0 ? null : string.Join("\n", Reasons),
            ["additionalContext"] = new JArray(AdditionalContext),
            ["systemMessages"] = new JArray(SystemMessages),
            ["continue"] = Continue,
            ["stopReason"] = StopReason,
            ["permissionDecision"] = PermissionDecision,
            ["permissionDecisionReason"] = PermissionDecisionReason,
        };
    }
}
