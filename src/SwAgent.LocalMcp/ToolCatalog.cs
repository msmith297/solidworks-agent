using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorksAgent.LocalLink;

namespace SwAgent.LocalMcp
{
    /// <summary>
    /// The seven MCP tools (docs/plugins/local-mcp.md 6.4), their argument checks and every plain-text message the client's
    /// model sees. Texts name the client ("Claude Desktop" / "Codex") where the design says so.
    /// </summary>
    internal static class ToolCatalog
    {
        public const string Ask = "ask_solidworks_agent", GetAnswer = "get_solidworks_agent_answer", Status = "solidworks_status";
        public const int MaxMessage = 20000, MaxWaitSeconds = 55, DefaultWaitSeconds = 50;

        public const string Instructions =
            "SolidWorks Agent is an AI agent inside SolidWorks on this PC. It can create new parts, assemblies and drawings, change the " +
            "user's open models and answer questions about them, and it asks the user in its panel before any change. Use " +
            "ask_solidworks_agent for any CAD work in SolidWorks, even when no document is open (it can start a new part). The solidworks_* " +
            "read tools only give quick facts about the open document. If SolidWorks isn't running, say so to the user.";

        /// <summary>The read-only model tools: MCP name -> description (the add-in's, for the active document).</summary>
        private static readonly Dictionary<string, string> ReadDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["solidworks_model_context"] =
                "Summary of the active SolidWorks document: name, path, type, units, configurations, material, mass properties, " +
                "bounding box, component or sheet counts, feature count and current selection. For quick facts only: use ask_solidworks_agent to do work.",
            ["solidworks_feature_tree"] =
                "FeatureManager design tree of the active SolidWorks document: every feature with its type name, suppression and " +
                "rebuild errors, plus sub-features (sketches, mates).",
            ["solidworks_selection"] =
                "Entities currently selected in SolidWorks (by the user or by code): selection type, owning feature/component, " +
                "geometry facts (face area and surface type, edge type) and the pick point in mm.",
            ["solidworks_open_documents"] = "All documents open in SolidWorks with type, path, visibility and unsaved-changes state.",
        };

        private static readonly Dictionary<string, string> ReadTitles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["solidworks_model_context"] = "SolidWorks Agent: model summary",
            ["solidworks_feature_tree"] = "SolidWorks Agent: feature tree",
            ["solidworks_selection"] = "SolidWorks Agent: selection",
            ["solidworks_open_documents"] = "SolidWorks Agent: open documents",
        };

        public static bool IsModelTool(string name) => LinkProtocol.ModelTools.Any(t => t[0] == name);

        /// <summary>One of the seven tools.</summary>
        public static bool Exists(string name) => name == Ask || name == GetAnswer || name == Status || IsModelTool(name);

        /// <summary>tools/list: exactly seven tools.</summary>
        public static JArray List()
        {
            var tools = new JArray
            {
                Tool(Ask,
                    "Use for any CAD work in SolidWorks: create new parts, assemblies or drawings (e.g. \"make a 100 mm cube\"), edit the " +
                    "open model, or answer questions that need the SolidWorks Agent running inside SolidWorks on this PC. It works with or " +
                    "without an open document. Any change waits for the user's OK in the SolidWorks Agent panel. If the reply takes longer than " +
                    "wait_seconds you get a ticket: call get_solidworks_agent_answer with it.",
                    new JObject
                    {
                        ["message"] = new JObject
                        {
                            ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxMessage,
                            ["description"] = "What to ask or tell the SolidWorks Agent. Write it as you would to a colleague; it sees the user's open model.",
                        },
                        ["wait_seconds"] = WaitSchema(),
                    },
                    new[] { "message" },
                    new JObject
                    {
                        ["title"] = "Ask SolidWorks Agent", ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = false, ["openWorldHint"] = false,
                    }),
                Tool(GetAnswer,
                    "Get the SolidWorks Agent's reply to an earlier ask_solidworks_agent, by its ticket. Waits up to wait_seconds.",
                    new JObject
                    {
                        ["ticket"] = new JObject { ["type"] = "string", ["description"] = "The ticket ask_solidworks_agent returned (u3d_...)." },
                        ["wait_seconds"] = WaitSchema(),
                    },
                    new[] { "ticket" },
                    ReadOnly("Get the SolidWorks Agent answer")),
                Tool(Status,
                    "Whether SolidWorks and the SolidWorks Agent add-in are running, the active document, the chat open in the SolidWorks Agent panel " +
                    "and whether it is busy, and how many automatic turns are left this hour.",
                    new JObject(), new string[0], ReadOnly("SolidWorks Agent status")),
            };
            foreach (var pair in LinkProtocol.ModelTools)
            {
                var name = pair[0];
                var props = name == "solidworks_feature_tree"
                    ? new JObject
                    {
                        ["max_features"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 2000, ["description"] = "At most this many features (default 400)." },
                        ["include_sub_features"] = new JObject { ["type"] = "boolean", ["description"] = "Include sketches and other sub-features (default true)." },
                    }
                    : new JObject();
                tools.Add(Tool(name, ReadDescriptions[name], props, new string[0], ReadOnly(ReadTitles[name])));
            }
            return tools;
        }

        private static JObject WaitSchema() => new JObject
        {
            ["type"] = "integer", ["minimum"] = 0, ["maximum"] = MaxWaitSeconds, ["default"] = DefaultWaitSeconds,
            ["description"] = "How long to wait for the reply, in seconds (default 50).",
        };

        private static JObject ReadOnly(string title) => new JObject
        {
            ["title"] = title, ["readOnlyHint"] = true, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false,
        };

        private static JObject Tool(string name, string description, JObject properties, string[] required, JObject annotations)
        {
            var schema = new JObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
            if (required.Length > 0) schema["required"] = new JArray(required.Cast<object>().ToArray());
            return new JObject
            {
                ["name"] = name,
                ["title"] = (string)annotations["title"],
                ["description"] = description,
                ["inputSchema"] = schema,
                ["annotations"] = annotations,
            };
        }

        /// <summary>Checks a tools/call's arguments against the schema; returns the problem, or null when they are fine.</summary>
        public static string CheckArguments(string name, JObject args)
        {
            var def = List().OfType<JObject>().FirstOrDefault(t => (string)t["name"] == name);
            if (def == null) return $"Unknown tool \"{name}\".";
            var props = (JObject)def["inputSchema"]["properties"];
            foreach (var p in args.Properties())
            {
                if (props[p.Name] == null) return $"{name} has no argument \"{p.Name}\".";
            }
            foreach (var r in def["inputSchema"]["required"]?.Values<string>() ?? Enumerable.Empty<string>())
            {
                if (args[r] == null || args[r].Type == JTokenType.Null) return $"{name} needs \"{r}\".";
            }
            foreach (var p in args.Properties())
            {
                if (p.Value.Type == JTokenType.Null) continue;
                var s = (JObject)props[p.Name];
                switch ((string)s["type"])
                {
                    case "string":
                        if (p.Value.Type != JTokenType.String) return $"\"{p.Name}\" must be a string.";
                        var len = ((string)p.Value).Length;
                        if (s["minLength"] != null && len < (int)s["minLength"]) return $"\"{p.Name}\" must not be empty.";
                        if (s["maxLength"] != null && len > (int)s["maxLength"]) return $"\"{p.Name}\" is longer than {(int)s["maxLength"]} characters.";
                        break;
                    case "integer":
                        if (p.Value.Type != JTokenType.Integer && !(p.Value.Type == JTokenType.Float && Math.Abs((double)p.Value % 1) < 1e-9))
                            return $"\"{p.Name}\" must be a whole number.";
                        var v = (double)p.Value;
                        if (s["minimum"] != null && v < (double)s["minimum"] || s["maximum"] != null && v > (double)s["maximum"])
                            return $"\"{p.Name}\" must be between {s["minimum"]} and {s["maximum"]}.";
                        break;
                    case "boolean":
                        if (p.Value.Type != JTokenType.Boolean) return $"\"{p.Name}\" must be true or false.";
                        break;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- texts

        public static string NotRunning =>
            "SolidWorks isn't running, or the SolidWorks Agent add-in isn't loaded in it. Open SolidWorks with SolidWorks Agent, then try again.";

        public static string NoHello =>
            "SolidWorks Agent didn't answer within 5 seconds. SolidWorks may be busy (a rebuild or a dialog). Try again in a moment.";

        public static string AccessDenied(string client) =>
            $"Windows refused the connection to SolidWorks Agent (a different Windows user, or an app sandbox). Run {LinkProtocol.Label(client)} as the same user as SolidWorks.";

        public static string NotOurPipe =>
            "Another program, not SolidWorks Agent, holds SolidWorks Agent's connection on this PC, so nothing was sent. Restart SolidWorks; if this keeps happening, restart Windows.";

        public static string SolidWorksSlow =>
            "SolidWorks didn't answer in 45 seconds (it may be in a long rebuild or a dialog). Try again in a moment.";

        public static string LinkLost =>
            "The connection to SolidWorks Agent closed before it answered. SolidWorks may have closed. Try again in a moment.";

        /// <summary>The note for a ticket that isn't done, when the add-in sent none (5.6).</summary>
        public static string Note(string state, string ticket, string answer)
        {
            switch (state)
            {
                case "working":
                    return $"SolidWorks Agent is still working on it. Call get_solidworks_agent_answer with ticket \"{ticket}\" to get the answer.";
                case "approval":
                    return "SolidWorks Agent is waiting for the user to approve a change (or answer a question) in the SolidWorks Agent panel in SolidWorks. " +
                           $"Tell the user, then call get_solidworks_agent_answer with ticket \"{ticket}\".";
                case "queued":
                    return $"The SolidWorks Agent chat is busy (or the panel is still loading). Your message goes next. Call get_solidworks_agent_answer with ticket \"{ticket}\" later.";
                case "paused":
                    return "SolidWorks Agent has paused automatic turns. Your message is in the chat and the agent answers it with the user's next message. " +
                           $"Call get_solidworks_agent_answer with ticket \"{ticket}\" later.";
                case "stopped":
                    return "The user stopped the agent before it finished." + (string.IsNullOrEmpty(answer) ? "" : " Partial reply: " + answer);
                case "failed":
                    return "The agent's turn failed.";
                default:
                    return $"SolidWorks Agent reported \"{state}\" for ticket \"{ticket}\".";
            }
        }

        /// <summary>solidworks_status as sentences (6.4).</summary>
        public static string StatusText(JObject s, string client)
        {
            var parts = new List<string>();
            var sw = (string)s["solidWorks"];
            var addIn = (string)s["addIn"];
            if (addIn != null && addIn.IndexOf('+') > 0) addIn = addIn.Substring(0, addIn.IndexOf('+'));
            parts.Add($"SolidWorks {sw ?? "(version unknown)"} is running with SolidWorks Agent {addIn ?? "(version unknown)"}.");
            var modelRead = (bool?)s["modelRead"] != false;
            if (s["document"] is JObject d)
                parts.Add($"Active document: {(string)d["title"]}{((string)d["type"] is string t ? $" ({t})" : "")}.");
            else if ((bool?)s["solidWorksBusy"] == true) parts.Add("SolidWorks is busy (a rebuild or a dialog), so the active document couldn't be read.");
            else if (modelRead) parts.Add("No document is open.");
            if ((string)s["panel"] == "loading") parts.Add("The SolidWorks Agent panel is still loading.");
            else if (s["chat"] is JObject c)
            {
                var busy = (bool?)c["busy"] == true ? "busy" : "idle";
                parts.Add((string)c["title"] is string title ? $"Open chat: \"{title}\" ({busy})." : $"A chat is open in the SolidWorks Agent panel ({busy}).");
            }
            else parts.Add("No chat is open in the SolidWorks Agent panel; a message starts a new one.");
            if (s["autoTurnsLeft"] != null && s["autoTurnsPerHour"] != null)
                parts.Add($"Automatic turns left this hour: {(int)s["autoTurnsLeft"]} of {(int)s["autoTurnsPerHour"]}.");
            if (!modelRead) parts.Add($"Reading the open model is turned off for {LinkProtocol.Label(client)} in SolidWorks Agent, so the active document isn't shown.");
            return string.Join(" ", parts);
        }
    }
}
