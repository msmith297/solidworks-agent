using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Mcp;
using SwAgent.Plugins.Hooks;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>The hook protocol and runtime rules that need no process: matchers, outcomes, names, schemas, the session.</summary>
    public class HookProtocolTests
    {
        [Fact]
        public void Matchers_work_like_claude_code()
        {
            Assert.True(HookEvents.Matches(null, "anything"));
            Assert.True(HookEvents.Matches("", "anything"));
            Assert.True(HookEvents.Matches("*", "anything"));
            Assert.True(HookEvents.Matches("execute_csharp", "execute_csharp"));
            Assert.False(HookEvents.Matches("execute", "execute_csharp")); // a plain name is exact
            Assert.True(HookEvents.Matches("get_selection|execute_csharp", "execute_csharp"));
            Assert.True(HookEvents.Matches("mcp__plugin_agent-bridge_.*", "mcp__plugin_agent-bridge_agent-bridge__send_message"));
            Assert.False(HookEvents.Matches("mcp__plugin_other_.*", "mcp__plugin_agent-bridge_agent-bridge__send_message"));
            Assert.False(HookEvents.Matches("([", "x")); // an invalid regex matches nothing
            Assert.True(HookEvents.Matches("startup", "startup"));
            Assert.False(HookEvents.Matches("resume", "startup"));
        }

        [Fact]
        public void Exit_codes_and_json_mean_what_they_mean_in_claude_code()
        {
            var plain = HookOutcome.Parse(HookEvents.UserPromptSubmit, 0, "just text\n", "");
            Assert.Equal(new[] { "just text" }, plain.AdditionalContext);
            Assert.Empty(HookOutcome.Parse(HookEvents.PostToolUse, 0, "just text", "").AdditionalContext); // not context for tool events

            var blocked = HookOutcome.Parse(HookEvents.UserPromptSubmit, 2, "", "no, because\n");
            Assert.True(blocked.Block);
            Assert.Equal("no, because", blocked.ToJson()["reason"]);

            var denied = HookOutcome.Parse(HookEvents.PreToolUse, 2, "", "not that tool");
            Assert.Equal("deny", denied.PermissionDecision);
            Assert.Equal("not that tool", denied.PermissionDecisionReason);

            var startNotice = HookOutcome.Parse(HookEvents.SessionStart, 2, "", "only for the user");
            Assert.False(startNotice.Block);
            Assert.Equal(new[] { "only for the user" }, startNotice.SystemMessages);

            var failed = HookOutcome.Parse(HookEvents.UserPromptSubmit, 1, "{\"decision\":\"block\"}", "crashed");
            Assert.False(failed.Block); // any other exit code is a non-blocking error
            Assert.Empty(failed.AdditionalContext);

            var json = HookOutcome.Parse(HookEvents.Stop, 0,
                "{\"decision\":\"block\",\"reason\":\"keep going\",\"systemMessage\":\"note\",\"continue\":false,\"stopReason\":\"halt\"}", "");
            Assert.True(json.Block);
            Assert.Equal(new[] { "keep going" }, json.Reasons);
            Assert.Equal(new[] { "note" }, json.SystemMessages);
            Assert.False(json.Continue);
            Assert.Equal("halt", json.StopReason);

            var context = HookOutcome.Parse(HookEvents.PostToolUse, 0, "{\"hookSpecificOutput\":{\"hookEventName\":\"PostToolUse\",\"additionalContext\":\"mail arrived\"}}", "");
            Assert.Equal(new[] { "mail arrived" }, context.AdditionalContext);
            Assert.False(context.Block);

            var legacy = HookOutcome.Parse(HookEvents.PreToolUse, 0, "{\"decision\":\"approve\",\"reason\":\"fine\"}", "");
            Assert.Equal("allow", legacy.PermissionDecision);
        }

        [Fact]
        public void Several_hooks_combine_block_wins_deny_beats_ask_beats_allow()
        {
            var all = new HookOutcome();
            all.Merge(HookOutcome.Parse(HookEvents.PreToolUse, 0, "{\"hookSpecificOutput\":{\"permissionDecision\":\"allow\",\"permissionDecisionReason\":\"a\"}}", ""));
            all.Merge(HookOutcome.Parse(HookEvents.PreToolUse, 0, "{\"hookSpecificOutput\":{\"permissionDecision\":\"ask\",\"permissionDecisionReason\":\"b\"}}", ""));
            Assert.Equal("ask", all.PermissionDecision);
            Assert.False(all.Block);
            all.Merge(HookOutcome.Parse(HookEvents.PreToolUse, 0, "{\"hookSpecificOutput\":{\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"c\"}}", ""));
            all.Merge(HookOutcome.Parse(HookEvents.PreToolUse, 0, "{\"hookSpecificOutput\":{\"permissionDecision\":\"allow\"}}", ""));
            Assert.Equal("deny", all.PermissionDecision);
            Assert.Equal("c", all.PermissionDecisionReason);
            Assert.True(all.Block);

            var json = all.ToJson();
            foreach (var key in new[] { "block", "reason", "additionalContext", "systemMessages", "continue", "stopReason", "permissionDecision", "permissionDecisionReason" })
                Assert.True(json.ContainsKey(key), key);
            Assert.True((bool)json["continue"]);
        }

        [Fact]
        public void Tool_responses_reach_hooks_as_text_only_and_capped()
        {
            var big = new string('x', RuntimeHost.MaxToolResponseChars + 5000);
            var capped = RuntimeHost.CapResponse(new JObject
            {
                ["content"] = new JArray(
                    new JObject { ["type"] = "text", ["text"] = "first" },
                    new JObject { ["type"] = "image", ["source"] = new JObject { ["data"] = "AAAA" } },
                    new JObject { ["type"] = "text", ["text"] = big }),
                ["isError"] = true,
            });
            var blocks = ((JArray)capped["content"]).Select(b => (string)b["text"]).ToList();
            Assert.Equal("first", blocks[0]);
            Assert.Equal("[image]", blocks[1]);
            Assert.True(blocks.Sum(b => b.Length) < RuntimeHost.MaxToolResponseChars + 100);
            Assert.True((bool)capped["isError"]);
        }

        [Fact]
        public void Mcp_tool_names_follow_claude_code_and_fit_the_api()
        {
            Assert.Equal("mcp__plugin_agent-bridge_agent-bridge__send_message", McpHost.ApiName("agent-bridge", "agent-bridge", "send_message"));
            Assert.Equal("mcp__sim__run_flow", McpHost.ApiName(null, "sim", "run_flow"));
            Assert.Equal("mcp__my_server__do_it_now", McpHost.ApiName(null, "my server", "do.it.now"));
            var longName = McpHost.ApiName("a-very-long-plugin-name-for-testing", "and-a-long-server-name", "with_a_long_tool_name_too");
            Assert.Equal(64, longName.Length);
            Assert.Matches("^[a-zA-Z0-9_-]+$", longName);
            Assert.NotEqual(longName, McpHost.ApiName("a-very-long-plugin-name-for-testing", "and-a-long-server-name", "with_a_long_tool_name_two"));
            Assert.True(McpHost.IsMcp("mcp__x__y"));
            Assert.False(McpHost.IsMcp("execute_csharp"));
        }

        [Fact]
        public void Mcp_schemas_are_made_acceptable_to_the_api()
        {
            var s = McpHost.SanitizeSchema(JObject.Parse("{\"$schema\":\"x\",\"anyOf\":[{\"type\":\"object\"}],\"properties\":{\"a\":{\"type\":\"string\"}}}"));
            Assert.Equal("object", (string)s["type"]);
            Assert.Null(s["$schema"]);
            Assert.Null(s["anyOf"]);
            Assert.Equal("string", (string)s["properties"]["a"]["type"]);
            Assert.Equal("object", (string)McpHost.SanitizeSchema(null)["type"]);
        }

        [Fact]
        public void Mcp_results_map_to_tool_results()
        {
            var r = McpHost.MapResult(JObject.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"hi\"},{\"type\":\"image\",\"data\":\"AAAA\",\"mimeType\":\"image/png\"}," +
                "{\"type\":\"image\",\"data\":\"AAAA\",\"mimeType\":\"image/bmp\"},{\"type\":\"resource\",\"resource\":{\"uri\":\"file:///a\",\"text\":\"body\"}}],\"isError\":true}"));
            Assert.True(r.IsError);
            Assert.Equal("hi", (string)r.Content[0]["text"]);
            Assert.Equal("image", (string)r.Content[1]["type"]);
            Assert.Equal("image/png", (string)r.Content[1]["source"]["media_type"]);
            Assert.Equal("[image image/bmp]", (string)r.Content[2]["text"]);
            Assert.Equal("body", (string)r.Content[3]["text"]);
            Assert.Equal("{\"n\":1}", (string)McpHost.MapResult(JObject.Parse("{\"content\":[],\"structuredContent\":{\"n\":1}}")).Content[0]["text"]);
        }

        [Fact]
        public void Mcp_and_plugin_tools_skip_the_busy_gate()
        {
            Assert.True(SwBusyGate.ModelFree("mcp__plugin_agent-bridge_agent-bridge__send_message"));
            Assert.True(SwBusyGate.ModelFree("use_skill"));
            Assert.True(SwBusyGate.ModelFree("plugin_write_file"));
            Assert.False(SwBusyGate.ModelFree("execute_csharp"));
        }

        [Fact]
        public void The_session_id_is_kept_per_pc_and_a_second_solidworks_gets_its_own()
        {
            using (var first = Session.Open())
            using (var second = Session.Open())
            {
                // Another test class may hold the PC's id right now; then this one is temporary too.
                Assert.True(second.Temporary);
                Assert.NotEqual(first.Id, second.Id);
                if (first.Temporary) return;
                var stored = (string)JObject.Parse(File.ReadAllText(Path.Combine(Session.PluginsDir, "session.json")))["sessionId"];
                Assert.Equal(first.Id, stored);
            }
            using (var again = Session.Open())
            {
                if (!again.Temporary)
                    Assert.Equal((string)JObject.Parse(File.ReadAllText(Path.Combine(Session.PluginsDir, "session.json")))["sessionId"], again.Id);
            }
        }

        [Fact]
        public void Automatic_turns_are_limited_per_hour_and_held_wakes_are_kept()
        {
            var s = Session.ForTests("guard");
            s.AutomaticPerHour = 2;
            var t0 = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(s.AddWake(new Session.Wake { Id = "a", Text = "one" }, t0));
            Assert.True(s.TryCountAutomatic(t0.AddMinutes(1))); // a Stop-hook continuation counts too
            Assert.False(s.AddWake(new Session.Wake { Id = "b", Text = "two" }, t0.AddMinutes(2)));
            Assert.Single(s.PendingWakes());
            Assert.Equal(1, s.HeldCount);
            Assert.True(s.TryCountAutomatic(t0.AddMinutes(61))); // the first one is an hour old

            Assert.False(s.AckWake("b"));
            Assert.True(s.AckWake("a"));
            Assert.Empty(s.PendingWakes());
            Assert.Equal("two", s.TakeHeld().Single().Text);
            Assert.Equal(0, s.HeldCount);
        }

        [Fact]
        public void The_transcript_is_the_threads_latest_save_outside_document_folders()
        {
            var s = Session.ForTests("transcript-" + Guid.NewGuid().ToString("N"));
            s.OnChatSaved(new JObject
            {
                ["threads"] = new JArray(new JObject
                {
                    ["id"] = "t1",
                    ["messages"] = new JArray(
                        new JObject { ["role"] = "user", ["content"] = "hello" },
                        new JObject { ["role"] = "assistant", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "hi" }) }),
                }),
            });
            s.WriteTranscript("t1");
            Assert.StartsWith(Paths.LocalDir, s.TranscriptPath);
            var lines = File.ReadAllLines(s.TranscriptPath).Select(JObject.Parse).ToList();
            Assert.Equal(2, lines.Count);
            Assert.Equal("user", (string)lines[0]["type"]);
            Assert.Equal("hello", (string)lines[0]["message"]["content"]);
            Assert.Equal("assistant", (string)lines[1]["message"]["role"]);
            s.WriteTranscript("unknown");
            Assert.Equal("", File.ReadAllText(s.TranscriptPath));
            File.Delete(s.TranscriptPath);
        }

        [Fact]
        public void Child_programs_never_inherit_secrets_or_another_agents_session()
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_TEST_SECRET_B", "sk-test");
            Environment.SetEnvironmentVariable("CLAUDE_CODE_TEST_MARKER_B", "x");
            try
            {
                var env = Session.ForTests("env").EnvFor(RuntimeTestKit.Plugin("p", env: new System.Collections.Generic.Dictionary<string, string>
                {
                    ["AGENT_BRIDGE_NAME"] = "SolidWorks Agent - ${SOLIDWORKS_AGENT_DEVICE}",
                    ["AGENT_BRIDGE_OWNER_PID"] = "${SOLIDWORKS_AGENT_PID}",
                    ["CLAUDE_CODE_SESSION_ID"] = "a plugin can't override this",
                }));
                Assert.False(env.ContainsKey("ANTHROPIC_TEST_SECRET_B"));
                Assert.False(env.ContainsKey("CLAUDE_CODE_TEST_MARKER_B"));
                Assert.Equal("env", env["CLAUDE_CODE_SESSION_ID"]);
                Assert.Equal("1", env["SOLIDWORKS_AGENT"]);
                Assert.Equal("SolidWorks Agent - " + Environment.MachineName, env["AGENT_BRIDGE_NAME"]);
                Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id.ToString(), env["AGENT_BRIDGE_OWNER_PID"]);
                Assert.Equal(Session.ProjectDir, env["CLAUDE_PROJECT_DIR"]);
                Assert.Equal(RuntimeTestKit.FixtureDir, env["CLAUDE_PLUGIN_ROOT"]);
                Assert.False(env.ContainsKey("SWAGENT_DATA_DIR"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("ANTHROPIC_TEST_SECRET_B", null);
                Environment.SetEnvironmentVariable("CLAUDE_CODE_TEST_MARKER_B", null);
            }
        }

        [Fact]
        public void Child_programs_never_inherit_any_AI_provider_key()
        {
            // OPENAI_API_KEY itself: PluginReviewTests (the "Plugins" collection, with SettingsStoreTests, which read it).
            var names = new[] { "OPENAI_ORG_ID", "OPENAI_TEST_SECRET_D", "ANTHROPIC_TEST_SECRET_D" };
            var saved = names.Select(Environment.GetEnvironmentVariable).ToArray();
            try
            {
                foreach (var n in names) Environment.SetEnvironmentVariable(n, "test-secret-not-real");
                var env = ChildEnv.Base();
                foreach (var n in names) Assert.False(env.ContainsKey(n), n);
                Assert.True(env.ContainsKey("PATH"));
                var hook = Session.ForTests("env-openai").EnvFor(RuntimeTestKit.Plugin("p"));
                foreach (var n in names) Assert.False(hook.ContainsKey(n), n);
                // A hook command line can't read them through ${...} either: the child environment has none.
                Assert.Equal("[]", ChildEnv.Expand("[${OPENAI_ORG_ID}]", hook));
            }
            finally
            {
                for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], saved[i]);
            }
            Assert.True(SwAgent.Infrastructure.SecretEnv.IsProviderSecret("openai_api_key"));
            Assert.True(SwAgent.Infrastructure.SecretEnv.IsProviderSecret("ANTHROPIC_AUTH_TOKEN"));
            Assert.False(SwAgent.Infrastructure.SecretEnv.IsProviderSecret("OPENAIKEY"));
            Assert.False(SwAgent.Infrastructure.SecretEnv.IsProviderSecret("CLAUDE_CODE_SESSION_ID"));
            Assert.False(SwAgent.Infrastructure.SecretEnv.IsProviderSecret(null));
        }

        [Fact]
        public void Arguments_are_quoted_for_windows_and_placeholders_expanded()
        {
            Assert.Equal("plain", PluginProcess.QuoteArg("plain"));
            Assert.Equal("\"two words\"", PluginProcess.QuoteArg("two words"));
            Assert.Equal("\"C:\\a b\\\\\"", PluginProcess.QuoteArg("C:\\a b\\"));
            Assert.Equal("\"say \\\"hi\\\"\"", PluginProcess.QuoteArg("say \"hi\""));
            Assert.Equal("\"\"", PluginProcess.QuoteArg(""));
            var vars = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ROOT"] = "C:\\p" };
            Assert.Equal("node \"C:\\p/x.mjs\" ", ChildEnv.Expand("node \"${root}/x.mjs\" ${MISSING}", vars));
        }
    }
}
