using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Tools;

namespace SwAgent.UI
{
    /// <summary>
    /// The "SolidWorks Agent" ribbon tab (and SolidWorks Agent menu/toolbar): the panel commands, then the user's Tools - small, fast
    /// commands with their own icons - and "New Tool". Workflows live in the chat panel, not here.
    ///
    /// Built once, at start-up, with the plain SolidWorks command API. SolidWorks doesn't apply command-group changes
    /// made while it runs (a rebuilt group keeps stale callbacks until restart - a new button can even run another
    /// command), so tools saved during a session reach the ribbon after a restart; IsStale tells the panel to offer
    /// one. Every command keeps the same user ID for as long as it exists, so keyboard shortcuts assigned in
    /// Tools > Customize > Keyboard stay on the right command.
    /// Buttons call back into the add-in object by name: OnAgentCommand("chat") / OnAgentEnable("t104").
    /// </summary>
    internal sealed class AgentRibbon : IDisposable
    {
        public const int GroupId = 7700;
        public const string TabName = "SolidWorks Agent";
        private static readonly string[] StaleTabs = { "Agent", "Unify3D Tools", "Unify3D", TabName };
        private static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };
        private static readonly int[] DocTypes = { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING };

        /// <summary>Fixed commands: callback argument, user ID, title, hint, icon. Tools get IDs from 100 up.</summary>
        private static readonly (string Arg, int Id, string Title, string Hint, Func<byte[]> Icon)[] Fixed =
        {
            ("chat", 1, "SolidWorks Agent", "Open the SolidWorks Agent chat", () => Icons.Agent),
            ("newchat", 2, "New Chat", "Start a new conversation", () => Icons.NewChat),
            ("ask", 3, "Ask About Selection", "Ask the agent about the selected faces, edges or features", () => Icons.Selection),
            ("view", 4, "Attach View", "Attach a screenshot of the current view to your message", () => Icons.Camera),
        };

        private static readonly (string Arg, int Id, string Title, string Hint, Func<byte[]> Icon)[] Trailing =
        {
            ("newtool", 5, "New Tool", "Create a new tool with the agent", () => Icons.NewTool),
            ("options", 6, "Options", "SolidWorks Agent options", () => Icons.Settings),
        };

        private readonly ICommandManager _cmdMgr;
        private readonly Timer _debounce = new Timer { Interval = 400 };
        private Dictionary<int, string> _toolById = new Dictionary<int, string>();
        private bool _created;
        private string _builtSignature;

        /// <summary>Tools were added, removed, renamed or re-iconed since the ribbon was built.</summary>
        public bool IsStale() => _builtSignature != null && Signature() != _builtSignature;

        /// <summary>Tools get a button; a built-in or plugin tool the user hid (they use their own copy, fork_tool) doesn't.</summary>
        private static bool OnRibbon(JObject t) => (string)t["kind"] == "tool" && (bool?)t["hidden"] != true;

        private static string Signature() => string.Join("|", UserTools.ListTools().Where(OnRibbon).Select(t =>
        {
            var png = Path.Combine(UserTools.DirOf((string)t["name"]) ?? "", "icon.png");
            // Category and ribbon title too: re-categorising moves a button to another box, which also needs a restart.
            return $"{t["name"]}:{t["title"]}:{(File.Exists(png) ? File.GetLastWriteTimeUtc(png).Ticks : 0)}:{t["category"]}:{t["ribbonTitle"]}";
        }));

        public AgentRibbon(ICommandManager cmdMgr)
        {
            _cmdMgr = cmdMgr;
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                Rebuild();
            };
        }

        /// <summary>The tool behind a callback argument like "t104", or null.</summary>
        public string ToolFor(string arg) =>
            arg != null && arg.StartsWith("t") && int.TryParse(arg.Substring(1), out var id) && _toolById.TryGetValue(id, out var name) ? name : null;

        /// <summary>Rebuild soon (several changes in a row become one rebuild).</summary>
        public void Invalidate()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        public void Rebuild()
        {
            try
            {
                _builtSignature = Signature();
                var tools = UserTools.ListTools().Where(OnRibbon).ToList();
                var ids = StableIds(tools.Select(t => (string)t["name"]).ToList());

                var commands = new List<(string Arg, int Id, string Title, string Hint, byte[] Icon)>();
                commands.AddRange(Fixed.Select(f => (f.Arg, f.Id, f.Title, f.Hint, f.Icon())));
                foreach (var t in tools)
                {
                    var name = (string)t["name"];
                    commands.Add(($"t{ids[name]}", ids[name], RibbonLayout.Title(t), RibbonLayout.Hint(t), LoadIcon(name) ?? Icons.ToolDefault));
                }
                commands.AddRange(Trailing.Select(f => (f.Arg, f.Id, f.Title, f.Hint, f.Icon())));

                Remove();
                var dir = Directory.CreateDirectory(Path.Combine(Paths.LocalDir, "ribbon")).FullName;
                foreach (var old in Directory.GetFiles(dir, "*.png")) TryDelete(old);
                var stamp = DateTime.Now.Ticks.ToString("x");
                var icons = commands.Select(c => c.Icon).ToList();
                var strips = Sizes.Select(s => SaveStrip(icons, s, Path.Combine(dir, $"cmds-{stamp}-{s}.png"))).ToArray();
                var main = Sizes.Select(s => SaveStrip(new List<byte[]> { Icons.Agent }, s, Path.Combine(dir, $"group-{stamp}-{s}.png"))).ToArray();

                // Keep SolidWorks' saved customisation unless the set of commands changed.
                var userIds = commands.Select(c => c.Id).ToArray();
                var unchanged = _cmdMgr.GetGroupDataFromRegistry(GroupId, out var saved) && saved is int[] previous && previous.SequenceEqual(userIds);

                var errors = 0;
                var group = _cmdMgr.CreateCommandGroup2(GroupId, TabName, "SolidWorks Agent", "SolidWorks Agent", -1, !unchanged, ref errors);
                group.IconList = strips;
                group.MainIconList = main;
                var options = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
                var indexes = new Dictionary<int, int>();
                for (var i = 0; i < commands.Count; i++)
                {
                    var c = commands[i];
                    indexes[c.Id] = group.AddCommandItem2(c.Title, -1, c.Hint, c.Title, i, $"OnAgentCommand({c.Arg})", $"OnAgentEnable({c.Arg})", c.Id, options);
                }
                group.HasToolbar = true;
                group.HasMenu = true;
                if (!group.Activate()) Log.Warn("SolidWorks Agent command group did not activate");
                _toolById = tools.ToDictionary(t => ids[(string)t["name"]], t => (string)t["name"]);
                _created = true;

                // One tab: SolidWorks Agent commands + Options | a box per category and the uncategorised tools (RibbonLayout)
                // | the user's uncategorised tools + New Tool. Command ids stay keyed by tool name, so moving a tool to
                // another box keeps its hotkey.
                int Cmd(int id) => group.get_CommandID(indexes[id]);
                var toolBoxes = RibbonLayout.Boxes(tools);
                var boxes = new List<int[]> { Fixed.Select(f => Cmd(f.Id)).Concat(new[] { Cmd(6) }).ToArray() };
                for (var b = 0; b < toolBoxes.Count; b++)
                {
                    var box = toolBoxes[b].Select(n => Cmd(ids[n]));
                    boxes.Add((b == toolBoxes.Count - 1 ? box.Concat(new[] { Cmd(5) }) : box).ToArray());
                }
                boxes = boxes.Where(b => b.Length > 0).ToList();
                foreach (var type in DocTypes)
                {
                    var tab = _cmdMgr.AddCommandTab(type, TabName);
                    if (tab == null) continue;
                    foreach (var box in boxes)
                    {
                        var styles = box.Select(_ => (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow).ToArray();
                        tab.AddCommandTabBox().AddCommands(box, styles);
                    }
                }
                Log.Info($"SolidWorks Agent ribbon: {tools.Count} tool(s)");
            }
            catch (Exception ex)
            {
                Log.Error("Building the SolidWorks Agent ribbon failed", ex);
            }
        }

        /// <summary>Take the group and its tab out of SolidWorks (before a rebuild and on shutdown), plus tabs of older versions.</summary>
        public void Remove()
        {
            try
            {
                foreach (var type in DocTypes)
                {
                    foreach (var name in StaleTabs)
                    {
                        var tab = _cmdMgr.GetCommandTab(type, name);
                        if (tab != null) _cmdMgr.RemoveCommandTab(tab);
                    }
                }
                if (_created) _cmdMgr.RemoveCommandGroup2(GroupId, true);
                _created = false;
            }
            catch (Exception ex)
            {
                Log.Warn("Removing the SolidWorks Agent ribbon failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Command user IDs per tool, kept in tools\command-ids.json so a tool keeps its ID (and hotkey) when others are
        /// added or removed. IDs of deleted tools are never reused.
        /// </summary>
        private static Dictionary<string, int> StableIds(List<string> names)
        {
            var path = Path.Combine(UserTools.Root, "command-ids.json");
            var map = new Dictionary<string, int>();
            var next = 100;
            try
            {
                if (File.Exists(path))
                {
                    var json = JObject.Parse(File.ReadAllText(path));
                    next = (int?)json["next"] ?? 100;
                    foreach (var p in (json["ids"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()) map[p.Name] = (int)p.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("command-ids.json unreadable, starting fresh: " + ex.Message);
            }
            var changed = false;
            foreach (var n in names.Where(n => !map.ContainsKey(n)))
            {
                map[n] = Math.Max(next, map.Values.DefaultIfEmpty(99).Max() + 1);
                next = map[n] + 1;
                changed = true;
            }
            if (changed || !File.Exists(path))
            {
                File.WriteAllText(path, new JObject { ["next"] = next, ["ids"] = JObject.FromObject(map) }.ToString());
            }
            return map;
        }

        private static byte[] LoadIcon(string name)
        {
            try
            {
                var png = Path.Combine(UserTools.DirOf(name) ?? "", "icon.png");
                return File.Exists(png) ? File.ReadAllBytes(png) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>One horizontal strip of square icons at the given size (the format SolidWorks' IconList expects).</summary>
        private static string SaveStrip(List<byte[]> icons, int size, string path)
        {
            using (var strip = new Bitmap(size * icons.Count, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(strip))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    for (var i = 0; i < icons.Count; i++)
                    {
                        try
                        {
                            using (var ms = new MemoryStream(icons[i]))
                            using (var img = Image.FromStream(ms))
                                g.DrawImage(img, new Rectangle(i * size, 0, size, size));
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Bad command icon #{i}: {ex.Message}");
                        }
                    }
                }
                strip.Save(path, ImageFormat.Png);
            }
            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // In use by SolidWorks until the group is rebuilt; cleaned up next time.
            }
        }

        public void Dispose()
        {
            _debounce.Dispose();
            Remove();
        }
    }

    /// <summary>
    /// How tools are laid out on the SolidWorks Agent tab (no SolidWorks types, so it is tested headless). Tab boxes have no
    /// captions, so a category shows as its own box and in each button's hint.
    /// </summary>
    internal static class RibbonLayout
    {
        /// <summary>The button title: the tool's short ribbon_title when it has one.</summary>
        public static string Title(JObject t) => (string)t["ribbonTitle"] is string r && !string.IsNullOrWhiteSpace(r) ? r : (string)t["title"];

        /// <summary>The tooltip: "[Category] description".</summary>
        public static string Hint(JObject t) => (string)t["category"] is string c && !string.IsNullOrWhiteSpace(c) ? $"[{c}] {t["description"]}" : (string)t["description"];

        /// <summary>
        /// Tool names per tab box, in order: one box per built-in category (first-seen order), the uncategorised built-ins,
        /// one box per user category (a user tool in a built-in category's name joins that box), then the user's
        /// uncategorised tools - always the last entry, possibly empty, as New Tool goes there. Categories match trimmed and
        /// case-insensitively; empty boxes are dropped except the last.
        /// </summary>
        public static List<List<string>> Boxes(IEnumerable<JObject> tools)
        {
            var categories = new List<(string Key, List<string> Names)>();
            List<string> CategoryBox(string category)
            {
                var key = category.Trim().ToLowerInvariant();
                var box = categories.FirstOrDefault(c => c.Key == key).Names;
                if (box == null) categories.Add((key, box = new List<string>()));
                return box;
            }

            var list = tools.ToList();
            bool IsBuiltIn(JObject t) => (bool?)t["builtIn"] == true;
            string CategoryOf(JObject t) => (string)t["category"] is string c && !string.IsNullOrWhiteSpace(c) ? c : null;

            var builtInPlain = new List<string>();
            foreach (var t in list.Where(IsBuiltIn))
            {
                if (CategoryOf(t) is string c) CategoryBox(c).Add((string)t["name"]);
                else builtInPlain.Add((string)t["name"]);
            }
            var builtInBoxes = categories.Count;
            var userPlain = new List<string>();
            foreach (var t in list.Where(t => !IsBuiltIn(t)))
            {
                if (CategoryOf(t) is string c) CategoryBox(c).Add((string)t["name"]);
                else userPlain.Add((string)t["name"]);
            }

            var boxes = categories.Take(builtInBoxes).Select(c => c.Names).ToList();
            boxes.Add(builtInPlain);
            boxes.AddRange(categories.Skip(builtInBoxes).Select(c => c.Names));
            boxes = boxes.Where(b => b.Count > 0).ToList();
            boxes.Add(userPlain);
            return boxes;
        }
    }
}
