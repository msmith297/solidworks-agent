using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Plugins
{
    /// <summary>One sub-agent: agents/&lt;name&gt;.md (front matter name, description, tools, model; the body is its system prompt).</summary>
    internal sealed class PluginAgent
    {
        public string Plugin;
        public string Name;
        public string File;
        public FrontMatter Front;

        /// <summary>"plugin:agent".</summary>
        public string Id => Plugin + ":" + Name;

        public string Description => Front.Get("description") ?? PluginCommand.FirstLine(Front.Body);

        /// <summary>The tools it may use, as written (Claude Code names such as Read or Bash included: run_agent drops what SolidWorks Agent lacks); null = not limited.</summary>
        public string[] Tools => Front.List("tools");

        /// <summary>
        /// plugins.agents: { id, name, plugin, description, tools: string[] | null, model, prompt } (design critique 22).
        /// </summary>
        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["plugin"] = Plugin,
            ["description"] = Description,
            ["tools"] = Tools is string[] t ? new JArray(t) : null,
            ["model"] = Front.Get("model") ?? "inherit",
            ["prompt"] = Front.Body.Trim(),
        };
    }

    /// <summary>The sub-agents of enabled plugins (design 4.4): the agents index in the prompt and run_agent.</summary>
    internal static class AgentIndex
    {
        public static List<PluginAgent> For(PluginManifest m)
        {
            var list = new List<PluginAgent>();
            if (m == null) return list;
            foreach (var path in m.AgentPaths)
            {
                var files = File.Exists(path) ? new[] { path } : Directory.Exists(path) ? Directory.GetFiles(path, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray() : new string[0];
                foreach (var f in files.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
                {
                    FrontMatter fm;
                    try
                    {
                        fm = FrontMatter.Parse(File.ReadAllText(f));
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    var name = fm.Get("name") ?? Path.GetFileNameWithoutExtension(f);
                    if (!PluginManifest.NamePattern.IsMatch(name)) name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (!PluginManifest.NamePattern.IsMatch(name) || list.Any(a => a.Name == name)) continue;
                    list.Add(new PluginAgent { Plugin = m.Name, Name = name, File = f, Front = fm });
                }
            }
            return list;
        }

        public static List<PluginAgent> All(PluginManager pm) => pm.EnabledPlugins().SelectMany(p => For(p.Manifest)).ToList();
    }
}
