using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>The shipped data tables (design 1.5 + critique A5/A10/C3/C5/D18/E2) and the shop-override merge.</summary>
    public class MoldDataTests
    {
        private static readonly string[] ProcessTypes =
            { "liquid_cast_hot", "liquid_cast_rt", "foam_pour", "rubber_compression", "compound_compression", "liquid_compression", "thermoplastic_compression", "preform_sinter" };

        /// <summary>The Fills paths of the page (build contract, Track E), with Track B's optional "|unit" suffix.</summary>
        private static readonly string[] FillPaths =
            { "shrinkPct.typ", "sg.typ", "viscosityCp.typ", "mooney.typ", "potLifeMin.typ", "demoldMin.typ", "draftDeg.min", "moldTempC.typ", "moldingPressurePsi.typ|psi", "flashAllowancePct.typ", "shrinkBasisChoice", "processLabel", "confidence" };

        [Fact]
        public void Materials_table_has_96_rows_with_identical_keys_and_ordered_ranges()
        {
            var d = Data;
            Assert.Empty(d.Warnings);
            Assert.Equal(96, d.PartMaterials.Count);
            Assert.Equal(96, d.PartMaterials.Select(r => (string)r["id"]).Distinct().Count());
            var keys = d.PartMaterials[0].Properties().Select(p => p.Name).ToList();
            foreach (var r in d.PartMaterials)
            {
                Assert.Equal(keys, r.Properties().Select(p => p.Name).ToList());
                Assert.Contains((string)r["processType"], ProcessTypes);
                foreach (var range in r.Properties().Select(p => p.Value).OfType<JObject>().Concat((r["flash"] as JObject)?.Properties().Select(p => p.Value).OfType<JObject>() ?? Enumerable.Empty<JObject>()))
                    if (range["min"]?.Type is JTokenType t1 && (t1 == JTokenType.Float || t1 == JTokenType.Integer) && range["typ"] != null && range["max"] != null &&
                        range["typ"].Type != JTokenType.Null && range["max"].Type != JTokenType.Null)
                    {
                        Assert.True((double)range["min"] <= (double)range["typ"] && (double)range["typ"] <= (double)range["max"], r["id"] + ": " + range);
                    }
            }
            var doc = d.Raw("materials.json");
            Assert.Equal("solidworksAgent.part-materials/1", (string)doc["schema"]);
            foreach (var src in d.PartMaterials.SelectMany(r => r["sources"].Select(s => (string)s)).Distinct())
                Assert.NotNull(doc["sources"][src]);
        }

        [Fact]
        public void Every_fill_path_exists_on_every_row()
        {
            foreach (var path in FillPaths)
                foreach (var r in Data.PartMaterials)
                {
                    JToken node = r;
                    foreach (var part in path.Split('|')[0].Split('.'))
                    {
                        Assert.True(node is JObject o && o.ContainsKey(part), $"{r["id"]} lacks {path}");
                        node = node[part];
                        if (node.Type == JTokenType.Null) break;   // a null parent hides the page field: allowed
                    }
                }
        }

        [Fact]
        public void Rows_carry_class_flags_labels_defaults_and_the_shrink_basis()
        {
            var d = Data;
            foreach (var fam in d.PartMaterials.GroupBy(r => (string)r["family"]))
            {
                Assert.Single(fam, r => (bool)r["isDefault"]);
                Assert.Equal(fam.Count(), fam.Select(r => (string)r["short"]).Distinct().Count());
                Assert.Single(fam.Select(r => (string)r["familyShort"]).Distinct());
            }
            Assert.True((bool)d.PartMaterial("pu-tdi-ether-90a")["isDefault"]);
            foreach (var r in d.PartMaterials)
            {
                var cls = (string)r["processClass"];
                Assert.Equal(ProcessClasses.Letter(ProcessClasses.From((string)r["processType"])), cls);
                Assert.Equal(cls != "C", (bool)r["builtByV1"]);
                Assert.False(string.IsNullOrWhiteSpace((string)r["processLabel"]));
                Assert.True(((string)r["short"]).Length <= 24, "short label too long: " + r["short"]);
                Assert.True(((string)r["familyShort"]).Length <= 24, "family label too long: " + r["familyShort"]);
                Shrink.ParseBasis((string)r["shrinkBasis"]);
                Shrink.ParseBasis((string)r["shrinkBasisChoice"]);
            }
            // critique A5: the hot-cast PU rows are hot-cavity basis, flagged as an estimate
            var hotPu = d.PartMaterials.Where(r => (string)r["group"] == "polyurethane" && (string)r["processType"] == "liquid_cast_hot").ToList();
            Assert.Equal(22, hotPu.Count);
            Assert.All(hotPu, r => Assert.Equal("hot_cavity_cold_part", (string)r["shrinkBasis"]));
            Assert.All(hotPu, r => Assert.Equal("estimate", (string)r["shrinkBasisConfidence"]));
            Assert.Equal(8, d.PartMaterials.Count(r => (string)r["processClass"] == "C"));
        }

        [Fact]
        public void Mold_grades_have_page_fields_and_the_D18_fatigue_curve()
        {
            var d = Data;
            Assert.Equal(16, d.MoldGrades.Count);
            foreach (var g in d.MoldGrades)
            {
                Assert.False(string.IsNullOrWhiteSpace((string)g["short"]));
                Assert.NotNull(g["use"] as JArray);
                Assert.NotNull(g["isDefault"]);
                Assert.Equal(4, ((JArray)g["retained"]["yield_long_10000h"]).Count);
            }
            Assert.Equal("al_6061_t651", (string)d.DefaultGrade("aluminium")["id"]);
            Assert.Equal("st_p20", (string)d.DefaultGrade("steel")["id"]);
            Assert.Equal("st_p20", (string)d.DefaultGrade("steel", "insert")["id"]);
            Assert.Empty(d.MoldGrade("al_2024_t351")["use"]);                       // ages above ~120 C: not offered
            Assert.True((bool)d.MoldGrade("al_2024_t351")["agesDimensionally"]);
            var f = d.MoldGrade("al_6061_t651")["fatigue_MPa"];
            Assert.Equal(142, (double)f["1e5"]);
            Assert.Equal(111, (double)f["1e6"]);
            Assert.Equal(101, (double)f["1e7"]);
            Assert.Equal("6061-T6 (SS)", (string)d.MoldGrade("6061-T651")["swMaterial"]);   // by its page label
        }

        [Fact]
        public void Fasteners_carry_washers_minor_diameters_and_stock_lengths()
        {
            var d = Data;
            var half = d.Fastener("1/2-13");
            Assert.Equal(1.063, (double)half["washer"]["od_in"]);
            Assert.True((double)half["washer"]["cbore_for_washer_in"] > (double)half["cbore_dia_in"], "a hardened washer does not fit the standard counterbore (E2)");
            Assert.Equal(0.4056, (double)half["minor_dia_in"], 3);
            Assert.Equal("M10", (string)d.Fastener("M10")["size"]);
            Assert.Equal("M10", (string)d.Fastener("M10x1.5")["size"]);
            Assert.Equal("1/2-13", (string)d.Fastener("1/2")["size"]);
            Assert.Null(d.Fastener("M99"));
            Assert.Equal(new[] { "#10-24", "1/4-20", "5/16-18", "3/8-16", "7/16-14", "1/2-13", "5/8-11", "3/4-10" }, d.Fasteners(true).Select(x => (string)x["size"]));
            Assert.NotEmpty(d.Raw("mold-materials.json").SelectToken("fasteners_common.stock_lengths_in"));
        }

        [Fact]
        public void Sizing_defaults_equal_the_compiled_copy_and_carry_the_critique_values()
        {
            var shipped = Si.ParseObject(File.ReadAllText(Path.Combine(DataDir, "sizing-defaults.json")));
            Assert.True(JToken.DeepEquals(shipped, SizingDefaults.Compiled()), "data/sizing-defaults.json and SizingDefaults.Builtin.cs differ: re-run tools/dev/mold-data/gen_defaults.py");
            var c = Cfg();
            Assert.Equal(0.25, c.D("bolts.tighteningScatter", 0));                 // A1
            Assert.Equal(0.95, c.Arr("bolts.hotProofFactor.factor")[1]);
            Assert.Equal(11, c.D("lifting.unknownLid_kg", 0));                      // A2
            Assert.Equal(4.0, c.D("bearingDivisor.aluminiumHot", 0));               // A8
            Assert.Equal(0.010, c.D("landFlatness_mm.liquid1000to10000cP", 0));    // A11
            Assert.Equal(30, c.D("thermal.lidBaseGradientSlideIn_K", 0));          // D1
            Assert.Equal(2.0, c.D("pins.eulerK", 0));                               // A12
        }

        [Fact]
        public void Shop_standards_read_in_mm_whatever_unit_they_are_written_in()
        {
            var d = Data;
            Assert.Equal(3.175, d.ShopMm("flash.lip", 0), 6);
            Assert.Equal(12.7, d.ShopMm("flash.grooveWidth", 0), 6);
            Assert.Equal(9.0, d.ShopMm("thermocouple.endFromCavity", 0), 6);
            Assert.Equal(10, d.ShopNum("pour.massRate_g_per_s", 0));               // D3
            Assert.Equal(4, d.ShopNum("dowels.count", 0));
            Assert.Equal(42.0, d.ShopMm("missing.path", 42.0));
        }

        [Fact]
        public void Lookups_find_rows_by_id_name_or_short_label_in_their_family()
        {
            var d = Data;
            Assert.Equal("pu-tdi-ether-90a", (string)d.FindPartMaterial("TDI-polyether / MBOCA 90A")["id"]);
            Assert.Equal("pu-tdi-ether-90a", (string)d.FindPartMaterial("TDI-ether 90A")["id"]);
            Assert.Equal("pu-tdi-ether-90a", (string)d.FindPartMaterial("TDI-polyether / MBOCA 90A [pu-tdi-ether-90a; data confidence medium]")["id"]);
            // "Cellulose-filled" is a grade of melamine and of urea-formaldehyde: the family decides
            Assert.Null(d.FindPartMaterial("Cellulose-filled"));
            Assert.Equal("uf-cellulose", (string)d.FindPartMaterial("Cellulose-filled", "Urea-formaldehyde (UF)")["id"]);
            Assert.Equal("mf-cellulose", (string)d.FindPartMaterial("Cellulose-filled", "melamine")["id"]);
            Assert.Equal("st_h13", (string)d.MoldGrade("H13 44-50 HRC")["id"]);
        }

        [Fact]
        public void Shop_overrides_merge_rows_by_id_add_new_rows_and_merge_objects()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mold-override-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "materials.json"), new JObject
                {
                    ["rows"] = new JArray(
                        new JObject { ["id"] = "epdm-s-70a", ["shrinkPct"] = new JObject { ["typ"] = 2.1 } },
                        new JObject { ["id"] = "my-epdm", ["family"] = "rubber_epdm", ["name"] = "Shop EPDM", ["processType"] = "rubber_compression" }),
                }.ToString());
                File.WriteAllText(Path.Combine(dir, "sizing-defaults.json"), new JObject { ["bolts"] = new JObject { ["tighteningScatter"] = 0.10 } }.ToString());
                File.WriteAllText(Path.Combine(dir, "shop-standards.json"), new JObject { ["flash"] = new JObject { ["lip"] = new JObject { ["mm"] = 4.0 } } }.ToString());
                var d = MoldData.Load(MoldData.DefaultToolDir, dir);
                Assert.Equal(97, d.PartMaterials.Count);
                var e = d.PartMaterial("epdm-s-70a");
                Assert.Equal(2.1, (double)e.SelectToken("shrinkPct.typ"));
                Assert.Equal(1.9, (double)e.SelectToken("shrinkPct.min"));          // the rest of the shipped row stays
                Assert.Equal("Shop EPDM", (string)d.PartMaterial("my-epdm")["name"]);
                Assert.Equal(0.10, new Cfg(d.Defaults).D("bolts.tighteningScatter", 0));
                Assert.Equal(2.0, new Cfg(d.Defaults).D("sfStatic", 0));            // untouched keys come from the shipped file
                Assert.Equal(4.0, d.ShopMm("flash.lip", 0), 6);
                Assert.Equal(12.7, d.ShopMm("flash.grooveWidth", 0), 6);
                Assert.Equal(1.9, (double)Data.PartMaterial("epdm-s-70a").SelectToken("shrinkPct.min"));
                Assert.Equal(2.4, (double)Data.PartMaterial("epdm-s-70a").SelectToken("shrinkPct.typ")); // the shipped data is not changed
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Save_default_writes_checked_keys_into_the_override()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mold-save-" + Guid.NewGuid().ToString("N"));
            try
            {
                var status = Data.SaveDefault(dir, "materials", "epdm-s-70a", new JObject { ["shrinkPct.typ"] = 2.1 });
                Assert.StartsWith("saved shrinkPct.typ = 2.1", status);
                Data.SaveDefault(dir, "materials", "epdm-s-70a", new JObject { ["sg.typ"] = 1.21 });           // a second save keeps the first
                Data.SaveDefault(dir, "sizing-defaults", null, new JObject { ["bolts.tighteningScatter"] = 0.10 });
                var d = MoldData.Load(MoldData.DefaultToolDir, dir);
                Assert.Equal(2.1, (double)d.PartMaterial("epdm-s-70a").SelectToken("shrinkPct.typ"));
                Assert.Equal(1.21, (double)d.PartMaterial("epdm-s-70a").SelectToken("sg.typ"));
                Assert.Equal(0.10, new Cfg(d.Defaults).D("bolts.tighteningScatter", 0));
                Assert.Throws<ArgumentException>(() => Data.SaveDefault(dir, "materials", "epdm-s-70a", new JObject { ["shrinkPercent"] = 2 }));
                Assert.Throws<ArgumentException>(() => Data.SaveDefault(dir, "materials", null, new JObject { ["sg.typ"] = 1 }));
                Assert.Throws<ArgumentException>(() => Data.SaveDefault(dir, "presses", null, new JObject { ["x"] = 1 }));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Payload_si_values_are_converted_by_quantity()
        {
            var d = MoldTestData.E2Design();
            d["inputs"]["press_pressure"] = new JObject { ["si"] = 2000 * 6894.757293168 };          // Pa
            d["inputs"]["press_rated_force"] = new JObject { ["si"] = 50 * 8896.443230521 };          // N
            d["inputs"]["press_rated_gauge"] = new JObject { ["value"] = 3000, ["unit"] = "psi" };
            var design = Design.Read(d, Data);
            Assert.Equal(2000 * Si.PsiToMPa, design.PressValue, 9);
            Assert.Equal(50 * Si.UsTonToKN, design.RatedForce_kN, 9);
            Assert.Equal(3000 * Si.PsiToMPa, design.RatedGauge_MPa, 9);
        }

        [Fact]
        public void Missing_data_folder_still_gives_the_compiled_sizing_defaults()
        {
            var d = MoldData.Load(Path.Combine(Path.GetTempPath(), "no-such-mold-tool-" + Guid.NewGuid().ToString("N")));
            Assert.Empty(d.PartMaterials);
            Assert.NotEmpty(d.Warnings);
            Assert.Equal(2.0, new Cfg(d.Defaults).D("sfStatic", 0));
        }

        [Fact]
        public void Coefficient_table_is_embedded_with_a_matching_hash()
        {
            Assert.False(string.IsNullOrEmpty(Coefficients.Version));
            Assert.Equal(Coefficients.HeaderSha, Coefficients.DataSha);
            foreach (var bc in new[] { "SS4", "C4", "Rails", "Wall-C", "Wall-S" }) Assert.Contains(bc, Coefficients.BoundarySets);
        }

        [Fact]
        public void Generators_reproduce_the_shipped_files_when_python_is_available()
        {
            var root = RepoRoot();
            if (root == null) return;   // not running from a checkout
            // gen_coefficients.py --check regenerates the table (numpy, ~2 min): here only its fast hash check runs
            foreach (var (script, arg) in new[] { ("gen_materials.py", "--check"), ("build_mold_materials.py", "--check"), ("gen_defaults.py", "--check"), ("examples_check.py", "--check"), ("gen_coefficients.py", "--check-hash") })
            {
                var psi = new ProcessStartInfo("python", "-I \"" + Path.Combine(root, "tools", "dev", "mold-data", script) + "\" " + arg)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                Process p;
                try { p = Process.Start(psi); }
                catch (System.ComponentModel.Win32Exception) { return; }   // no python on this machine: the CI diff is the generators' own --check
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(120000);
                Assert.True(p.ExitCode == 0, script + " " + arg + ": " + output);
            }
        }
    }
}
