using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Tools;

namespace SwAgent.Plugins
{
    /// <summary>One slash command: commands/&lt;name&gt;.md of a plugin (a sub-folder adds "folder:" to its name, as in Claude Code).</summary>
    internal sealed class PluginCommand
    {
        public string Plugin;
        public string Name;
        public string File;
        public FrontMatter Front;

        /// <summary>"plugin:command".</summary>
        public string Id => Plugin + ":" + Name;

        public string Description => Front.Get("description") ?? FirstLine(Front.Body);

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["plugin"] = Plugin,
            ["description"] = Description,
            ["argumentHint"] = Front.Text("argument-hint"),
            ["model"] = Front.Get("model"),
            ["allowedTools"] = Front.List("allowed-tools") is string[] t ? new JArray(t) : null,
            ["disableModelInvocation"] = Front.Flag("disable-model-invocation"),
        };

        internal static string FirstLine(string body)
        {
            var line = (body ?? "").Split('\n').Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return line.Length > 120 ? line.Substring(0, 117) + "..." : line;
        }
    }

    /// <summary>
    /// The slash commands of enabled plugins (design 4.2), and their expansion: $ARGUMENTS, $1..$9, ${CLAUDE_PLUGIN_ROOT}
    /// and @relative/path includes from the plugin's folder. !`shell` lines are never run: they stay as text, with a note.
    /// </summary>
    internal static class CommandIndex
    {
        private static readonly Regex CommandName = new Regex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$");

        /// <summary>The commands of one plugin (enabled or not).</summary>
        public static List<PluginCommand> For(PluginManifest m)
        {
            var list = new List<PluginCommand>();
            if (m == null) return list;
            foreach (var path in m.CommandPaths)
            {
                if (File.Exists(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) Add(list, m, path, null);
                else if (Directory.Exists(path))
                {
                    foreach (var f in Directory.GetFiles(path, "*.md", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var rel = Path.GetDirectoryName(f.Substring(path.Length).TrimStart('\\', '/'));
                        Add(list, m, f, string.IsNullOrEmpty(rel) ? null : rel.Replace('\\', ':').Replace('/', ':'));
                    }
                }
            }
            return list;
        }

        private static void Add(List<PluginCommand> list, PluginManifest m, string file, string prefix)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!CommandName.IsMatch(name) || name.Equals("README", StringComparison.OrdinalIgnoreCase)) return;
            if (prefix != null) name = prefix + ":" + name;
            if (list.Any(c => c.Name == name)) return;
            string text;
            try
            {
                text = System.IO.File.ReadAllText(file);
            }
            catch (IOException)
            {
                return;
            }
            list.Add(new PluginCommand { Plugin = m.Name, Name = name, File = file, Front = FrontMatter.Parse(text) });
        }

        /// <summary>The commands of every enabled plugin.</summary>
        public static List<PluginCommand> All(PluginManager pm) => pm.EnabledPlugins().SelectMany(p => For(p.Manifest)).ToList();

        /// <summary>A command by "plugin:command", or by "command" when only one plugin has it. Throws a readable message otherwise.</summary>
        public static PluginCommand Resolve(PluginManager pm, string name)
        {
            var n = (name ?? "").Trim().TrimStart('/');
            var all = All(pm);
            var exact = all.FirstOrDefault(c => string.Equals(c.Id, n, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var short_ = all.Where(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (short_.Count == 1) return short_[0];
            if (short_.Count > 1) throw new InvalidOperationException($"/{n} is in several plugins: use {string.Join(" or ", short_.Select(c => "/" + c.Id))}.");
            throw new InvalidOperationException($"No command /{n} (is its plugin enabled?).");
        }

        /// <summary>plugins.expandCommand: the text to send as the user's message, the model from its front matter, and a note.</summary>
        public static JObject Expand(PluginManager pm, string name, string args)
        {
            var cmd = Resolve(pm, name);
            var plugin = pm.Find(cmd.Plugin) ?? throw new InvalidOperationException($"No installed plugin \"{cmd.Plugin}\".");
            var runtime = pm.BuildRuntime(plugin);
            var notes = new List<string>();
            var text = ExpandBody(cmd.Front.Body, args ?? "", plugin.Root, runtime.Expand, notes);
            var result = new JObject { ["text"] = text.Trim() };
            if (cmd.Front.Get("model") is string model && model != "inherit") result["model"] = model;
            if (notes.Count > 0) result["note"] = string.Join(" ", notes);
            return result;
        }

        private static readonly Regex Shell = new Regex(@"!`[^`\n]+`");
        private static readonly Regex Arguments = new Regex(@"\$(ARGUMENTS|[1-9])(?![0-9])");
        private static readonly Regex FileRef = new Regex(@"(?<=^|[\s(])@([A-Za-z0-9_][A-Za-z0-9_./\\-]*[A-Za-z0-9_])");

        /// <summary>The expansion itself (headless; tested).</summary>
        internal static string ExpandBody(string body, string args, string root, Func<string, string> expand, List<string> notes)
        {
            var text = body ?? "";
            // ${...} first, so the user's arguments are never expanded as variables
            if (expand != null) text = expand(text);
            var positional = SplitArgs(args);
            var referenced = Arguments.IsMatch(text);
            // One pass: what the user typed ("why does $1 cost more than $2") is put in, never scanned again.
            text = Arguments.Replace(text, m =>
            {
                if (m.Groups[1].Value == "ARGUMENTS") return args;
                var i = int.Parse(m.Groups[1].Value) - 1;
                return i < positional.Count ? positional[i] : "";
            });
            // as Claude Code does: arguments the body never mentions still reach the agent
            if (!referenced && args.Trim().Length > 0) text = text.TrimEnd() + "\n\nARGUMENTS: " + args.Trim();
            if (Shell.IsMatch(text)) notes.Add("Shell lines (!`...`) in this command were not run: SolidWorks Agent doesn't run shell commands from plugins.");
            text = FileRef.Replace(text, m =>
            {
                var rel = m.Groups[1].Value;
                string path;
                try
                {
                    path = UserTools.SafePath(root, rel);
                }
                catch (ToolException)
                {
                    return m.Value;
                }
                if (!System.IO.File.Exists(path)) return m.Value; // an @mention, not a file
                var content = System.IO.File.ReadAllText(path);
                if (content.Length > 100000) content = content.Substring(0, 100000) + "\n... (truncated)";
                return $"\n<file path=\"{rel}\">\n{content.TrimEnd()}\n</file>\n";
            });
            return text;
        }

        /// <summary>Arguments split like a shell: spaces separate, quotes group.</summary>
        internal static List<string> SplitArgs(string args)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            var any = false;
            foreach (var c in args ?? "")
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else sb.Append(c);
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                    any = true;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 || any) list.Add(sb.ToString());
                    sb.Clear();
                    any = false;
                }
                else sb.Append(c);
            }
            if (sb.Length > 0 || any) list.Add(sb.ToString());
            return list;
        }
    }
}
