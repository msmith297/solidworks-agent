using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Tools;
using SwAgent.UI;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>Built-in SolidWorks Agent tools ship as folders next to the add-in, load like the user's tools, and are read-only.</summary>
    /// <remarks>Not parallel: it writes builtin-settings.json, which other tests read while they list tools.</remarks>
    [Collection("Memory")]
    public class BuiltInToolsTests
    {
        [Fact]
        public void Smart_rebuild_ships_as_a_tool_folder_with_its_page()
        {
            Assert.True(File.Exists(Path.Combine(UserTools.BuiltInRoot, "smart-rebuild", "TOOL.md")), "builtin-tools is copied next to the add-in");
            var spec = ToolSpec.Load("smart-rebuild");
            Assert.NotNull(spec);
            Assert.Equal("tool", spec.Kind);
            Assert.True(spec.Agent);
            Assert.Equal("always", spec.Confirm); // its PropertyManager page always opens
            Assert.Contains(spec.Inputs, i => i.Id == "change" && ToolSpec.Type(i) == "options");
            Assert.Contains(spec.Inputs, i => i.Id == "new_part" && ToolSpec.Type(i) == "files");
            Assert.True(UserTools.IsBuiltIn("smart-rebuild"));

            var listed = UserTools.ListTools().Single(t => (string)t["name"] == "smart-rebuild");
            Assert.True((bool)listed["builtIn"]);
        }

        [Fact]
        public void Design_compression_mold_loads_with_resolvable_page_data_and_registered_agent_tools()
        {
            const string name = "design-compression-mold";
            Assert.True(File.Exists(Path.Combine(UserTools.BuiltInRoot, name, "TOOL.md")), "builtin-tools is copied next to the add-in");
            var spec = ToolSpec.Load(name);
            Assert.NotNull(spec);
            Assert.Equal("tool", spec.Kind);
            Assert.True(spec.Agent);
            Assert.Equal("always", spec.Confirm);
            Assert.Equal("Molding & Toolmaking", spec.Category);
            Assert.Equal("Compression Mold", spec.RibbonTitle);
            Assert.True(spec.LongRun);
            Assert.Equal(600, spec.MaxSteps);
            Assert.True(UserTools.IsBuiltIn(name));

            // every input parses: an id, a known type, and the page's checker finds nothing
            Assert.Equal(44, spec.Inputs.Count);
            var types = new[] { "number", "integer", "checkbox", "choice", "options", "text", "selection", "files", "size", "info" };
            Assert.All(spec.Inputs, i => { Assert.False(string.IsNullOrWhiteSpace(i.Id)); Assert.Contains(ToolSpec.Type(i), types); });
            Assert.Equal(spec.Inputs.Count, spec.Inputs.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Empty(ToolSpec.CheckInputs(spec.Dir, spec.Inputs));

            // every ChoicesFrom resolves to rows, and every Fills path exists in them (a null value is allowed, a missing key is not)
            var data = ToolData.For(spec);
            foreach (var i in spec.Inputs.Where(x => x.ChoicesFrom != null))
            {
                var rows = data.Rows(i.ChoicesFrom);
                Assert.True(rows.Count > 0, i.Id + ": no rows in " + i.ChoicesFrom);
                foreach (var kv in i.Fills ?? new Dictionary<string, string>())
                {
                    var (path, unit) = ToolForm.SplitFill(kv.Value);
                    Assert.True(rows.All(r => PathExists(r, path)), $"{i.Id}: Fills {kv.Key} <- {path} is missing in a row");
                    if (unit != null) Assert.NotNull(Units.FindAny(unit));
                }
            }
            // the page builds in both systems and nothing blocks OK with the shipped defaults
            foreach (var imperial in new[] { true, false })
                Assert.Null(new ToolForm(spec, data, imperial, null).Blocking().Field);

            // every agent tool it requires is registered by the add-in (the registry OnConnect builds)
            Assert.Equal(new[] { "mold_calc", "mold_part", "mold_bodies", "mold_record" }, spec.Requires);
            var registry = AgentAddIn.BuildTools(out _);
            var registered = new HashSet<string>(registry.Definitions().Select(d => (string)d["name"]));
            Assert.All(spec.Requires, r => Assert.Contains(r, registered));
        }

        /// <summary>The path exists in the row: each segment's parent is an object that has the key (its value may be null).</summary>
        private static bool PathExists(JObject row, string path)
        {
            JToken cur = row;
            foreach (var seg in path.Split('.'))
            {
                if (cur is JObject o && o.ContainsKey(seg)) { cur = o[seg]; continue; }
                // a null parent (e.g. viscosityCp: null) hides the field on the page: the path counts as present
                return cur != null && cur.Type == JTokenType.Null;
            }
            return true;
        }

        [Fact]
        public void Reserved_folder_names_are_not_tool_names()
        {
            foreach (var name in new[] { "last-inputs", "builtin-data" })
                Assert.True(new UserTools().Save(null, new UserTools.SaveInput { Name = name, Title = "x", Kind = "tool" }).IsError, name);
        }

        [Fact]
        public void Built_in_tools_are_read_only_but_keep_the_users_settings()
        {
            var md = Path.Combine(UserTools.BuiltInRoot, "smart-rebuild", "TOOL.md");
            var before = File.ReadAllText(md);

            var tools = new UserTools();
            var save = tools.Save(null, new UserTools.SaveInput { Name = "smart-rebuild", Title = "Mine", Kind = "tool" });
            Assert.True(save.IsError);
            Assert.Throws<ToolException>(() => tools.Edit(null, new UserTools.EditInput { Name = "smart-rebuild", Append = "x" }));
            Assert.Throws<ToolException>(() => UserTools.Delete("smart-rebuild"));

            try
            {
                UserTools.SetValue("smart-rebuild", "intelligence", "high");
                Assert.Equal("high", ToolSpec.Load("smart-rebuild").Intelligence);
                Assert.Equal(before, File.ReadAllText(md)); // the shipped folder is untouched
            }
            finally
            {
                UserTools.SetValue("smart-rebuild", "intelligence", "max");
            }
        }

        // ---- Categories, ribbon title, long runs (front matter) ----

        private static Dictionary<string, string> Fm(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        [Fact]
        public void Long_run_front_matter_gives_steps_and_requires()
        {
            Assert.False(UserTools.LongRunOf(Fm()));
            Assert.Null(UserTools.MaxStepsOf(Fm()));                                      // the panel's default (200)
            Assert.True(UserTools.LongRunOf(Fm("long_run", "true")));
            Assert.True(UserTools.LongRunOf(Fm("long_run", " Yes ")));
            Assert.False(UserTools.LongRunOf(Fm("long_run", "false")));
            Assert.Equal(UserTools.LongRunMaxSteps, UserTools.MaxStepsOf(Fm("long_run", "true"))); // 600 without max_steps
            Assert.Equal(600, UserTools.MaxStepsOf(Fm("long_run", "true", "max_steps", "600")));
            Assert.Equal(350, UserTools.MaxStepsOf(Fm("max_steps", "350")));
            Assert.Equal(UserTools.MaxStepsCap, UserTools.MaxStepsOf(Fm("max_steps", "50000"))); // capped
            Assert.Equal(UserTools.MaxStepsMin, UserTools.MaxStepsOf(Fm("max_steps", "-3")));  // the design's range: 50..1000
            Assert.Equal(50, UserTools.MaxStepsOf(Fm("max_steps", "10")));
            Assert.Null(UserTools.MaxStepsOf(Fm("max_steps", "lots")));
            Assert.Equal(new[] { "mold_calc", "mold_part", "mold_bodies", "mold_record" },
                UserTools.RequiresOf(Fm("requires", "mold_calc, mold_part,mold_bodies  mold_record, mold_calc")));
            Assert.Empty(UserTools.RequiresOf(Fm()));
        }

        [Fact]
        public void Categories_are_one_short_line_and_blank_means_none()
        {
            Assert.Equal("Molding & Toolmaking", UserTools.CleanCategory("  Molding &\n Toolmaking "));
            Assert.Equal("", UserTools.CleanCategory(null));
            Assert.Equal(48, UserTools.CleanCategory(new string('x', 80)).Length);
            Assert.Null(UserTools.CategoryOf(Fm("category", "   ")));
            Assert.Equal("Molding & Toolmaking: inserts", UserTools.CategoryOf(Fm("category", "Molding & Toolmaking: inserts")));
        }

        [Fact]
        public void A_users_tool_keeps_its_category_through_save_edit_and_the_panel()
        {
            var tools = new UserTools();
            const string name = "test-category-tool";
            try
            {
                var save = tools.Save(null, new UserTools.SaveInput
                {
                    Name = name, Title = "Flash grooves", Kind = "tool", Description = "Cuts flash grooves.", Instructions = "Do it.",
                    Category = " Molding & Toolmaking ",
                });
                Assert.False(save.IsError);
                var md = File.ReadAllText(Path.Combine(UserTools.Root, name, "TOOL.md"));
                Assert.Contains("\ncategory: Molding & Toolmaking", md.Replace("\r\n", "\n"));
                var listed = UserTools.ListTools().Single(t => (string)t["name"] == name);
                Assert.Equal("Molding & Toolmaking", (string)listed["category"]);
                Assert.False((bool)listed["longRun"]);
                Assert.Null((int?)listed["maxSteps"]);
                Assert.Null((string)listed["ribbonTitle"]);

                var index = tools.List(null, new UserTools.NoInput()).Content.Single()["text"].ToString();
                Assert.Contains($"- {name} (tool, Molding & Toolmaking): Flash grooves", index);

                // Only the category changes: a valid edit on its own.
                tools.Edit(null, new UserTools.EditInput { Name = name, Category = "Fixtures" });
                Assert.Equal("Fixtures", (string)UserTools.ListTools().Single(t => (string)t["name"] == name)["category"]);
                Assert.Contains("Do it.", File.ReadAllText(Path.Combine(UserTools.Root, name, "TOOL.md")));

                // From the panel (userTools.setValue), then removed.
                UserTools.SetValue(name, "category", "Molding & Toolmaking\r\n");
                Assert.Equal("Molding & Toolmaking", (string)UserTools.ListTools().Single(t => (string)t["name"] == name)["category"]);
                tools.Edit(null, new UserTools.EditInput { Name = name, Category = "" });
                Assert.Null((string)UserTools.ListTools().Single(t => (string)t["name"] == name)["category"]);
                Assert.DoesNotContain("category:", File.ReadAllText(Path.Combine(UserTools.Root, name, "TOOL.md")));
                Assert.Null(UserTools.ListTools().Single(t => (string)t["name"] == name)["categoryDefault"]?.Value<string>());

                Assert.Throws<ToolException>(() => tools.Edit(null, new UserTools.EditInput { Name = name }));
            }
            finally
            {
                if (Directory.Exists(Path.Combine(UserTools.Root, name))) UserTools.Delete(name);
            }
        }

        [Fact]
        public void A_built_in_tool_can_be_given_a_category_without_touching_its_folder()
        {
            var md = Path.Combine(UserTools.BuiltInRoot, "smart-rebuild", "TOOL.md");
            var before = File.ReadAllText(md);
            var settings = Path.Combine(UserTools.Root, "builtin-settings.json");
            try
            {
                UserTools.SetValue("smart-rebuild", "category", "Rebuilding");
                Assert.Equal("Rebuilding", (string)UserTools.ListTools().Single(t => (string)t["name"] == "smart-rebuild")["category"]);
                Assert.Equal(before, File.ReadAllText(md));
                UserTools.SetValue("smart-rebuild", "category", ""); // the user removed it: no category, even if the tool ships one
                Assert.Null((string)UserTools.ListTools().Single(t => (string)t["name"] == "smart-rebuild")["category"]);
                // It ships none, so "none" is the shipped value: no override is kept.
                Assert.Null(Override("smart-rebuild", "category"));

                // Reset (null) drops the override, so the shipped value applies again.
                UserTools.SetValue("smart-rebuild", "category", "Rebuilding");
                Assert.Equal("Rebuilding", Override("smart-rebuild", "category"));
                UserTools.SetValue("smart-rebuild", "category", null);
                Assert.Null(Override("smart-rebuild", "category"));
                var listed = UserTools.ListTools().Single(t => (string)t["name"] == "smart-rebuild");
                Assert.Null((string)listed["category"]);
                Assert.True(listed.ContainsKey("categoryDefault"), "built-ins list their shipped category (none here)");
                Assert.Null(listed["categoryDefault"].Value<string>());

                // Retyping the shipped value stores no override either (a later change to the shipped value then applies).
                UserTools.SetValue("smart-rebuild", "intelligence", "high");
                Assert.Equal("high", Override("smart-rebuild", "intelligence"));
                UserTools.SetValue("smart-rebuild", "intelligence", "max"); // what TOOL.md ships
                Assert.Null(Override("smart-rebuild", "intelligence"));
                Assert.Equal("max", (string)UserTools.ListTools().Single(t => (string)t["name"] == "smart-rebuild")["intelligence"]);
            }
            finally
            {
                // Back to the shipped values: drop the overrides.
                var all = JObject.Parse(File.ReadAllText(settings));
                (all["smart-rebuild"] as JObject)?.Remove("category");
                (all["smart-rebuild"] as JObject)?.Remove("intelligence");
                File.WriteAllText(settings, all.ToString());
            }
        }

        /// <summary>The user's override of a built-in's front-matter value in builtin-settings.json, or null.</summary>
        private static string Override(string tool, string key)
        {
            var path = Path.Combine(UserTools.Root, "builtin-settings.json");
            return File.Exists(path) ? (string)JObject.Parse(File.ReadAllText(path))[tool]?[key] : null;
        }

        [Fact]
        public void Every_built_in_tool_lists_valid_category_and_run_settings()
        {
            var builtIns = UserTools.ListTools().Where(t => (bool)t["builtIn"]).ToList();
            Assert.NotEmpty(builtIns);
            foreach (var t in builtIns)
            {
                var steps = (int?)t["maxSteps"];
                Assert.True(steps == null || (steps >= UserTools.MaxStepsMin && steps <= UserTools.MaxStepsCap), $"{t["name"]}: max_steps {steps}");
                if ((bool)t["longRun"]) Assert.True(steps >= UserTools.DefaultMaxSteps, $"{t["name"]}: a long tool gets at least the default steps");
                var category = (string)t["category"];
                Assert.True(category == null || (category.Length <= 48 && category == category.Trim()), $"{t["name"]}: category '{category}'");
                Assert.All(t["requires"].Values<string>(), r => Assert.Matches("^[a-z][a-z0-9_]*$", r));
            }
        }

        private static JObject T(string name, bool builtIn, string category = null, string title = null, string ribbonTitle = null) =>
            new JObject { ["name"] = name, ["builtIn"] = builtIn, ["category"] = category, ["title"] = title ?? name, ["ribbonTitle"] = ribbonTitle, ["description"] = "Does " + name };

        [Fact]
        public void Ribbon_boxes_group_tools_by_category()
        {
            var boxes = RibbonLayout.Boxes(new[]
            {
                T("mold", true, "Molding & Toolmaking"),
                T("smart-rebuild", true),
                T("inspect", true, "Quality"),
                T("grooves", false, " molding & toolmaking "), // joins the built-in box (trimmed, any case)
                T("holes", false),
                T("jig", false, "Fixtures"),
                T("clamp", false, "fixtures"),
                T("chamfer", false),
            });
            Assert.Equal(new[]
            {
                new[] { "mold", "grooves" },
                new[] { "inspect" },
                new[] { "smart-rebuild" },
                new[] { "jig", "clamp" },
                new[] { "holes", "chamfer" }, // the last box: New Tool goes here
            }, boxes.Select(b => b.ToArray()).ToArray());

            // Nothing categorised: exactly today's layout (built-ins | the user's tools + New Tool).
            boxes = RibbonLayout.Boxes(new[] { T("smart-rebuild", true), T("holes", false) });
            Assert.Equal(new[] { new[] { "smart-rebuild" }, new[] { "holes" } }, boxes.Select(b => b.ToArray()).ToArray());

            // No user tools: New Tool still gets its (otherwise empty) box.
            boxes = RibbonLayout.Boxes(new[] { T("mold", true, "Molding & Toolmaking") });
            Assert.Equal(2, boxes.Count);
            Assert.Empty(boxes.Last());
        }

        [Fact]
        public void Ribbon_buttons_use_the_short_title_and_name_the_category_in_the_hint()
        {
            var t = T("design-compression-mold", true, "Molding & Toolmaking", "Design compression mold for plastics and resins", "Compression Mold");
            Assert.Equal("Compression Mold", RibbonLayout.Title(t));
            Assert.Equal("[Molding & Toolmaking] Does design-compression-mold", RibbonLayout.Hint(t));
            var plain = T("smart-rebuild", true, title: "Smart Rebuild");
            Assert.Equal("Smart Rebuild", RibbonLayout.Title(plain));
            Assert.Equal("Does smart-rebuild", RibbonLayout.Hint(plain));
        }
    }
}
