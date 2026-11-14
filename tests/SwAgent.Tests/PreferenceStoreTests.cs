using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Memory;
using SwAgent.Params;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// The preferred parameters file: the agent saves, replaces and forgets entries, Undo puts back what a save replaced,
    /// and lines the user wrote by hand survive. Plus the v params' JSON, which is what the file stores.
    /// </summary>
    public class PreferenceStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "swagent-prefs-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Fact]
        public void Saving_groups_by_topic_and_a_replacement_can_be_undone()
        {
            var store = new PreferenceStore(_root);
            var first = store.Save("3D printing (FDM)", "M5 self-tapping pilot holes: Ø4.2 mm");
            Assert.False(first.Existed);
            Assert.StartsWith("p", first.Saved.Id);
            Assert.True(store.Save("3d printing (fdm)", "M5 self-tapping pilot holes: Ø4.2 mm").Existed); // same topic, any case

            var better = store.Save("3D printing (FDM)", "M5 self-tapping pilot holes: Ø4.6 mm", new[] { first.Saved.Id });
            Assert.Single(better.Replaced);
            store.Save("Fasteners", "Socket head cap screws: black oxide");
            var text = File.ReadAllText(store.File);
            Assert.Contains("## 3D printing (FDM)", text);
            Assert.Contains("Ø4.6 mm", text);
            Assert.DoesNotContain("Ø4.2 mm", text);

            Assert.True(store.Undo(better.Saved.Id));
            var all = store.All();
            Assert.Contains(all, p => p.Text.Contains("Ø4.2 mm"));
            Assert.DoesNotContain(all, p => p.Text.Contains("Ø4.6 mm"));
            Assert.Equal(new[] { "3D printing (FDM)", "Fasteners" }, ((JArray)store.Index()["topics"]).Select(t => (string)t["topic"]));
        }

        [Fact]
        public void Lines_the_user_wrote_survive_and_count()
        {
            var store = new PreferenceStore(_root);
            Directory.CreateDirectory(_root);
            File.WriteAllText(store.File, "# Preferred parameters\n\nmy intro\n\n## Walls\nKeep these thin.\n- 2 mm for ABS housings\n");
            Assert.Single(store.All());
            store.Save("Walls", "1.6 mm for PETG brackets");
            var text = File.ReadAllText(store.File);
            Assert.Contains("Keep these thin.", text);
            Assert.Contains("- 2 mm for ABS housings", text);
            var id = store.All().First(p => p.Text.StartsWith("2 mm")).Id;
            Assert.NotNull(store.Edit(id, "2.2 mm for ABS housings"));
            Assert.NotNull(store.Forget(store.All().First(p => p.Text.StartsWith("1.6")).Id));
            Assert.Equal(new[] { "2.2 mm for ABS housings" }, store.All().Select(p => p.Text));
        }

        [Fact]
        public void A_vparam_survives_its_json()
        {
            var p = new VParam
            {
                Name = "Wall thickness", Type = "length", Value = "2.5", Unit = "mm", Note = "the cup's side wall",
                Drives = { "Shell1" }, Links = { new VLink { Part = "Cup.SLDPRT", Param = "Wall" } },
            };
            var back = VParam.FromJson(p.ToJson());
            Assert.Equal("2.5 mm", back.Display);
            Assert.Equal(new[] { "Shell1" }, back.Drives);
            Assert.Equal("Cup.SLDPRT", back.Links.Single().Part);
            Assert.Equal("ACME", VParam.FromJson(new JObject { ["name"] = "Text", ["type"] = "text", ["value"] = "ACME" }).Display);
        }
    }
}
