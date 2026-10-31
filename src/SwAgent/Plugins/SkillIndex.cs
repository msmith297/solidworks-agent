using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Plugins
{
    /// <summary>One skill: skills/&lt;skill&gt;/SKILL.md of a plugin, with any other files in its folder.</summary>
    internal sealed class PluginSkill
    {
        public string Plugin;
        public string Name;
        public string Dir;
        public FrontMatter Front;

        /// <summary>"plugin:skill".</summary>
        public string Id => Plugin + ":" + Name;

        public string Description => Front.Get("description") ?? PluginCommand.FirstLine(Front.Body);

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["plugin"] = Plugin,
            ["description"] = Description,
        };
    }

    /// <summary>The skills of enabled plugins (design 4.3): the system prompt's skills index and use_skill.</summary>
    internal static class SkillIndex
    {
        /// <summary>The skills of one plugin (enabled or not), without the hidden ones (the marketplace entry's solidworksAgent.hideSkills).</summary>
        public static List<PluginSkill> For(PluginManifest m, ICollection<string> hidden = null)
        {
            var list = new List<PluginSkill>();
            if (m == null) return list;
            foreach (var path in m.SkillPaths.Where(Directory.Exists))
            {
                // A path is a folder of skill folders, or one skill folder itself.
                var dirs = File.Exists(Path.Combine(path, "SKILL.md")) ? new[] { path } : Directory.GetDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var dir in dirs)
                {
                    var file = Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(file)) continue;
                    FrontMatter fm;
                    try
                    {
                        fm = FrontMatter.Parse(File.ReadAllText(file));
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    var name = fm.Get("name") ?? Path.GetFileName(dir);
                    if (!PluginManifest.NamePattern.IsMatch(name)) name = Path.GetFileName(dir);
                    if (hidden?.Contains(name) == true || list.Any(s => s.Name == name)) continue;
                    list.Add(new PluginSkill { Plugin = m.Name, Name = name, Dir = dir, Front = fm });
                }
            }
            return list;
        }

        public static List<PluginSkill> All(PluginManager pm) =>
            pm.EnabledPlugins().SelectMany(p => For(p.Manifest, PluginManager.HiddenSkills(p))).ToList();

        /// <summary>A skill by "plugin:skill", or by "skill" when only one plugin has it.</summary>
        public static PluginSkill Resolve(PluginManager pm, string name)
        {
            var n = (name ?? "").Trim().TrimStart('/');
            var all = All(pm);
            var exact = all.FirstOrDefault(s => string.Equals(s.Id, n, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var matches = all.Where(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) throw new InvalidOperationException($"Skill \"{n}\" is in several plugins: use {string.Join(" or ", matches.Select(s => s.Id))}.");
            throw new InvalidOperationException($"No skill \"{n}\". The skills index in your instructions lists the enabled ones.");
        }
    }
}
