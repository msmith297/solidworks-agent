using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// The tool registry's schemas (critique B1): a JObject input used to send BuildSchema into endless recursion - a stack
    /// overflow that kills SolidWorks while the add-in loads. Every [AgentTool]'s input schema is built here, headless.
    /// </summary>
    public class ToolRegistryTests
    {
        private static IEnumerable<(MethodInfo Method, AgentToolAttribute Attr)> AllTools() =>
            typeof(ToolRegistry).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Select(m => (m, m.GetCustomAttribute<AgentToolAttribute>()))
                .Where(x => x.Item2 != null);

        [Fact]
        public void Every_agent_tool_input_builds_a_schema()
        {
            var tools = AllTools().ToList();
            Assert.True(tools.Count >= 30, $"found only {tools.Count} tools");
            foreach (var (method, attr) in tools)
            {
                var ps = method.GetParameters();
                Assert.True(ps.Length == 2 && ps[0].ParameterType == typeof(ToolContext), $"{attr.Name}: signature must be (ToolContext, TInput)");
                var schema = ToolRegistry.SchemaOf(ps[1].ParameterType);
                Assert.Equal("object", (string)schema["type"]);
                Assert.IsType<JObject>(schema["properties"]);
                var text = schema.ToString(Formatting.None);
                Assert.True(text.Length < 20000, $"{attr.Name}: schema is {text.Length} characters");
                foreach (var p in ((JObject)schema["properties"]).Properties())
                {
                    var s = (JObject)p.Value;
                    Assert.True(s["type"] != null || !s.Properties().Any(x => x.Name != "description"), $"{attr.Name}.{p.Name} has no type");
                }
                Assert.False(string.IsNullOrWhiteSpace(attr.Description), $"{attr.Name} has no description");
            }
        }

        [Fact]
        public void Tool_names_are_unique()
        {
            var dupes = AllTools().GroupBy(t => t.Attr.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.Empty(dupes);
        }

        public sealed class JsonInput
        {
            [ToolParam("free-form object", Required = true)]
            public JObject Design { get; set; }

            public JArray List { get; set; }
            public JToken Anything { get; set; }
            public Dictionary<string, double> Map { get; set; }
            public double[][] Points { get; set; }
            public Node Tree { get; set; }
        }

        /// <summary>A type that contains itself: bounded, not recursed forever.</summary>
        public sealed class Node
        {
            public string Name { get; set; }
            public Node Child { get; set; }
            public List<Node> Children { get; set; }
        }

        [Fact]
        public void Json_inputs_are_free_form_values_and_self_references_stop()
        {
            var schema = ToolRegistry.SchemaOf(typeof(JsonInput));
            var props = (JObject)schema["properties"];
            Assert.Equal("object", (string)props["design"]["type"]);
            Assert.Equal("free-form object", (string)props["design"]["description"]);
            Assert.Equal("array", (string)props["list"]["type"]);
            Assert.Null(props["anything"]["type"]);
            Assert.Equal("object", (string)props["map"]["type"]);
            Assert.Equal("array", (string)props["points"]["items"]["type"]);
            Assert.Equal("number", (string)props["points"]["items"]["items"]["type"]);
            Assert.Equal("object", (string)props["tree"]["properties"]["child"]["type"]);
            Assert.Null(props["tree"]["properties"]["child"]["properties"]); // stopped at the type's second appearance
            Assert.Equal(new[] { "design" }, schema["required"].Select(x => (string)x));
        }

        public sealed class JsonTool
        {
            public JObject Seen;

            [AgentTool("json_echo", "test")]
            public ToolResult Echo(ToolContext ctx, JsonInput input)
            {
                Seen = input.Design;
                return ToolResult.Ok(input.Design.ToString(Formatting.None));
            }
        }

        [Fact]
        public async Task A_JObject_input_arrives_unchanged()
        {
            var registry = new ToolRegistry();
            var tool = new JsonTool();
            registry.RegisterAll(tool);
            var input = JObject.Parse("{\"design\":{\"inputs\":{\"shrinkPct\":1.8},\"camelCase\":[1,2]}}");
            var result = await registry.InvokeAsync("json_echo", input, new ToolContext(null, null, "test", registry));
            Assert.False(result.IsError, result.Content[0]["text"]?.ToString());
            Assert.Equal(1.8, (double)tool.Seen.SelectToken("inputs.shrinkPct"));
            Assert.Equal(2, ((JArray)tool.Seen["camelCase"]).Count); // keys keep their case: not snake-cased
            var missing = await registry.InvokeAsync("json_echo", new JObject(), new ToolContext(null, null, "test", registry));
            Assert.True(missing.IsError);
        }

        [Fact]
        public void Mold_tools_register_with_short_prefixed_descriptions()
        {
            var registry = new ToolRegistry();
            registry.RegisterAll(new MoldTools());
            var defs = registry.Definitions().OfType<JObject>().ToList();
            Assert.Equal(new[] { "mold_bodies", "mold_calc", "mold_part", "mold_record" }, defs.Select(d => (string)d["name"]).OrderBy(x => x));
            foreach (var d in defs)
            {
                var desc = (string)d["description"];
                Assert.StartsWith("For the design-compression-mold tool:", desc);
                Assert.True(desc.Length <= 400, $"{d["name"]}: {desc.Length} characters");
            }
            var calc = defs.Single(d => (string)d["name"] == "mold_calc");
            Assert.Equal("object", (string)calc.SelectToken("input_schema.properties.design.type"));
            Assert.Equal(new[] { "action" }, calc.SelectToken("input_schema.required").Select(x => (string)x));
        }

        [Fact]
        public async Task Mold_calc_rejects_unknown_actions_without_solidworks()
        {
            var registry = new ToolRegistry();
            registry.RegisterAll(new MoldTools());
            var r = await registry.InvokeAsync("mold_calc", JObject.Parse("{\"action\":\"bake\"}"), new ToolContext(null, null, "test", registry));
            Assert.True(r.IsError);
            Assert.Contains("lookup", (string)r.Content[0]["text"]);
            var noModel = await registry.InvokeAsync("mold_part", new JObject(), new ToolContext(null, null, "test", registry));
            Assert.True(noModel.IsError);
        }
    }

    /// <summary>mold_record save_default: the override file format the data loader merges (design 1.5.5).</summary>
    public class MoldOverrideTests
    {
        private static JObject Shipped() => JObject.Parse(@"{
            ""schema"": ""solidworksAgent.part-materials/1"",
            ""rows"": [ { ""id"": ""epdm-70a"", ""shrinkPct"": { ""min"": 1.5, ""typ"": 1.8, ""max"": 2.5 }, ""sg"": { ""typ"": 1.1 } },
                       { ""id"": ""pu-90a"", ""shrinkPct"": { ""typ"": 1.5 } } ],
            ""flash"": { ""lip"": { ""in"": 0.125 } } }");

        [Fact]
        public void A_row_value_writes_the_whole_row()
        {
            var ovr = new JObject();
            Assert.Null(MoldTools.ApplyOverride(Shipped(), ovr, "EPDM-70A", JObject.Parse("{\"shrinkPct.typ\": 2.1}")));
            var row = (JObject)ovr["rows"].Single();
            Assert.Equal("epdm-70a", (string)row["id"]);
            Assert.Equal(2.1, (double)row.SelectToken("shrinkPct.typ"));
            Assert.Equal(2.5, (double)row.SelectToken("shrinkPct.max")); // the rest of the shipped row comes along
            Assert.Null(MoldTools.ApplyOverride(Shipped(), ovr, "epdm-70a", JObject.Parse("{\"sg.typ\": 1.2}")));
            row = (JObject)ovr["rows"].Single();
            Assert.Equal(2.1, (double)row.SelectToken("shrinkPct.typ")); // earlier overrides kept
            Assert.Equal(1.2, (double)row.SelectToken("sg.typ"));
        }

        [Fact]
        public void Unknown_rows_and_fields_are_refused()
        {
            var ovr = new JObject();
            Assert.Contains("No row", MoldTools.ApplyOverride(Shipped(), ovr, "nope", JObject.Parse("{\"sg.typ\": 1}")));
            Assert.Contains("Unknown field", MoldTools.ApplyOverride(Shipped(), ovr, "pu-90a", JObject.Parse("{\"sg.typ\": 1}")));
            Assert.Contains("Unknown field", MoldTools.ApplyOverride(Shipped(), ovr, null, JObject.Parse("{\"flash.width\": 1}")));
            Assert.Empty(ovr.Properties());
        }

        [Fact]
        public void Object_tables_merge_key_by_key()
        {
            var ovr = new JObject();
            Assert.Null(MoldTools.ApplyOverride(Shipped(), ovr, null, JObject.Parse("{\"flash.lip.in\": 0.1}")));
            Assert.Equal(0.1, (double)ovr.SelectToken("flash.lip.in"));
            Assert.Null(ovr["rows"]);
        }
    }

    /// <summary>Checkpoints (critique B2): dated stamps, restore by exact name or newest time, never another design's.</summary>
    public class CheckpointPickTests
    {
        [Fact]
        public void Name_without_its_stamp()
        {
            Assert.Equal("stage-4-3", RebuildTools.CheckpointName("20261019-153012-stage-4-3"));
            Assert.Equal("after-plates", RebuildTools.CheckpointName("153012-after-plates"));
            Assert.Equal("plain", RebuildTools.CheckpointName("plain"));
        }

        [Fact]
        public void Restore_takes_the_newest_by_time_not_by_name()
        {
            var files = new List<(string, DateTime)>
            {
                (@"c:\x\150000-stage-4-2.SLDPRT", new DateTime(2026, 10, 18, 15, 0, 0)), // yesterday 15:00, old stamp style
                (@"c:\x\20261019-090000-stage-4-2.SLDPRT", new DateTime(2026, 10, 19, 9, 0, 0)), // today 09:00
                (@"c:\x\20261019-091500-stage-4-2b.SLDPRT", new DateTime(2026, 10, 19, 9, 15, 0)),
            };
            Assert.Equal(@"c:\x\20261019-090000-stage-4-2.SLDPRT", RebuildTools.PickCheckpoint(files, "stage-4.2"));
            Assert.Equal(@"c:\x\150000-stage-4-2.SLDPRT", RebuildTools.PickCheckpoint(files, "150000-stage-4-2")); // exact name wins
            Assert.Equal(@"c:\x\20261019-091500-stage-4-2b.SLDPRT", RebuildTools.PickCheckpoint(files, "stage-4-2b"));
            Assert.Null(RebuildTools.PickCheckpoint(files, "4-2")); // a suffix is not a name
            Assert.Null(RebuildTools.PickCheckpoint(files, ""));
            // a stamped name written as it was asked for ('.' kept) still finds the saved file (save writes '-')
            Assert.Equal(@"c:\x\20261019-090000-stage-4-2.SLDPRT", RebuildTools.PickCheckpoint(files, "20261019-090000-stage-4.2"));
        }

        [Fact]
        public void Prune_never_removes_the_newest_intake_checkpoint()
        {
            var t0 = new DateTime(2026, 1, 1);
            var files = new List<(string, DateTime)> { (@"c:\x\20260101-000000-intake.SLDPRT", t0) };
            files.AddRange(Enumerable.Range(1, 25).Select(k => ($"f{k}", t0.AddMinutes(k))));
            var gone = RebuildTools.Prune(files, RebuildTools.KeepCheckpoints);
            Assert.Equal(5, gone.Count);
            Assert.DoesNotContain(@"c:\x\20260101-000000-intake.SLDPRT", gone);
        }

        [Fact]
        public void Prune_keeps_the_newest()
        {
            var t0 = new DateTime(2026, 1, 1);
            var files = Enumerable.Range(0, 25).Select(k => ($"f{k}", t0.AddMinutes(k))).ToList();
            var gone = RebuildTools.Prune(files, RebuildTools.KeepCheckpoints);
            Assert.Equal(5, gone.Count);
            Assert.All(gone, g => Assert.True(int.Parse(g.Substring(1)) < 5));
        }

        [Fact]
        public void Mold_designs_are_registered_apart_from_working_copies()
        {
            var path = Path.Combine(Path.GetTempPath(), "SwAgent.Tests.Data", "mold-" + Guid.NewGuid().ToString("N") + ".SLDPRT");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "x");
            try
            {
                Assert.False(RebuildTools.IsMoldDesign(path));
                RebuildTools.RegisterMoldDesign(path, "5b0c6f0e");
                Assert.True(RebuildTools.IsMoldDesign(path));
                RebuildTools.UnregisterMoldDesign(path);
                Assert.False(RebuildTools.IsMoldDesign(path));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
