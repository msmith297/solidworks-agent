using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Tools;

namespace SwAgent.Memory
{
    /// <summary>One preferred value: a dated bullet under a topic.</summary>
    internal sealed class Preference
    {
        /// <summary>"p" + 6 hex from the topic and the text: stable while both are unchanged.</summary>
        public string Id;

        public string Topic;

        /// <summary>yyyy-MM-dd, or "" for a bullet the user wrote without a date.</summary>
        public string Date;

        public string Text;

        public JObject ToJson() => new JObject { ["id"] = Id, ["topic"] = Topic, ["date"] = Date, ["text"] = Text };
    }

    /// <summary>
    /// The user's preferred parameters (docs/preferred-parameters.md): design values they like to use, such as "M5 self-tapping
    /// pilot holes in FDM prints: Ø4.6 mm". One markdown file the user can open and edit, grouped by "## Topic" headings with
    /// "- yyyy-MM-dd: text" bullets. The agent adds and updates entries itself; the whole file goes into the system prompt.
    /// Lines that aren't bullets (the user's own notes) are kept where they are.
    /// </summary>
    internal sealed class PreferenceStore
    {
        /// <summary>Characters of bullets before the agent is asked to merge entries.</summary>
        public const int Cap = 6000;

        public const int TextMax = 300;

        private const string Title = "# Preferred parameters";

        private const string Intro =
            "Design values you prefer. SolidWorks Agent uses them by default and keeps this file up to date; you can edit it too.\n" +
            "One topic per \"## \" heading, one value per \"- \" line.";

        private static PreferenceStore _instance;

        /// <summary>The add-in's store (under Paths). Tests may swap it.</summary>
        public static PreferenceStore Instance
        {
            get => _instance ?? (_instance = new PreferenceStore());
            internal set => _instance = value;
        }

        public PreferenceStore(string root = null)
        {
            Root = root ?? Path.Combine(Paths.RoamingDir, "agent-knowledge");
        }

        public string Root { get; }

        public string File => Path.Combine(Root, "preferred-parameters.md");

        private string UndoFile => Path.Combine(Root, "preferred-parameters-undo.json");

        private readonly object _sync = new object();

        public event Action Changed;

        /// <summary>A topic and its lines: a preference, or text the user wrote that isn't a bullet.</summary>
        private sealed class Section
        {
            public string Topic;
            public readonly List<object> Lines = new List<object>();
        }

        internal sealed class SaveResult
        {
            public Preference Saved;
            public bool Existed;
            public List<Preference> Replaced = new List<Preference>();
            public string Warning;
        }

        public List<Preference> All()
        {
            lock (_sync) return Read().SelectMany(s => s.Lines.OfType<Preference>()).ToList();
        }

        /// <summary>Adds a preference under its topic (created when new), replacing the ids given.</summary>
        public SaveResult Save(string topic, string text, IEnumerable<string> replaces = null)
        {
            topic = CleanTopic(topic);
            text = MemoryStore.Clean(text);
            if (text.Length == 0) throw new ToolException("text is empty.");
            if (text.Length > TextMax) throw new ToolException($"Keep it under {TextMax} characters: one value and where it applies.");
            var result = new SaveResult();
            lock (_sync)
            {
                var sections = Read();
                var id = IdFor(topic, text);
                var existing = sections.SelectMany(s => s.Lines.OfType<Preference>()).FirstOrDefault(p => p.Id == id);
                if (existing != null)
                {
                    result.Saved = existing;
                    result.Existed = true;
                    return result;
                }
                foreach (var rid in (replaces ?? Enumerable.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)))
                {
                    foreach (var s in sections)
                    {
                        var hit = s.Lines.OfType<Preference>().FirstOrDefault(p => p.Id == rid.Trim());
                        if (hit == null) continue;
                        s.Lines.Remove(hit);
                        result.Replaced.Add(hit);
                    }
                }
                var section = sections.FirstOrDefault(s => string.Equals(s.Topic, topic, StringComparison.OrdinalIgnoreCase));
                if (section == null)
                {
                    section = new Section { Topic = topic };
                    sections.Add(section);
                }
                var pref = new Preference { Id = id, Topic = section.Topic, Date = DateTime.Now.ToString("yyyy-MM-dd"), Text = text };
                section.Lines.Add(pref);
                sections.RemoveAll(s => s.Lines.Count == 0);
                Write(sections);
                SaveUndo(id, result.Replaced);
                result.Saved = pref;
                var used = Used(sections);
                if (used > Cap)
                    result.Warning = $"The file is getting long ({used} of {Cap} characters): merge entries that overlap, and ask before dropping any.";
            }
            Changed?.Invoke();
            return result;
        }

        /// <summary>Removes one preference; null when there's none with that id.</summary>
        public Preference Forget(string id)
        {
            Preference hit = null;
            lock (_sync)
            {
                var sections = Read();
                foreach (var s in sections)
                {
                    hit = s.Lines.OfType<Preference>().FirstOrDefault(p => p.Id == (id ?? "").Trim());
                    if (hit == null) continue;
                    s.Lines.Remove(hit);
                    break;
                }
                if (hit == null) return null;
                sections.RemoveAll(s => s.Lines.Count == 0);
                Write(sections);
            }
            Changed?.Invoke();
            return hit;
        }

        /// <summary>The panel's edit of one entry's text; the id changes with it. Null when it's gone.</summary>
        public Preference Edit(string id, string text)
        {
            text = MemoryStore.Clean(text);
            if (text.Length == 0) return Forget(id); // emptied: deleted
            Preference hit = null;
            lock (_sync)
            {
                var sections = Read();
                foreach (var s in sections)
                {
                    hit = s.Lines.OfType<Preference>().FirstOrDefault(p => p.Id == id);
                    if (hit == null) continue;
                    hit.Text = text.Length > TextMax ? text.Substring(0, TextMax) : text;
                    hit.Id = IdFor(s.Topic, hit.Text);
                    break;
                }
                if (hit == null) return null;
                Write(sections);
            }
            Changed?.Invoke();
            return hit;
        }

        /// <summary>The chat chip's Undo: removes the saved entry and puts back what it replaced.</summary>
        public bool Undo(string id)
        {
            lock (_sync)
            {
                var undo = ReadUndo();
                var sections = Read();
                var removed = false;
                foreach (var s in sections)
                {
                    var hit = s.Lines.OfType<Preference>().FirstOrDefault(p => p.Id == id);
                    if (hit == null) continue;
                    s.Lines.Remove(hit);
                    removed = true;
                }
                if (undo[id] is JArray back)
                {
                    foreach (var r in back.OfType<JObject>())
                    {
                        var topic = (string)r["topic"];
                        var section = sections.FirstOrDefault(s => string.Equals(s.Topic, topic, StringComparison.OrdinalIgnoreCase));
                        if (section == null) sections.Add(section = new Section { Topic = topic });
                        var text = (string)r["text"];
                        section.Lines.Add(new Preference { Id = IdFor(section.Topic, text), Topic = section.Topic, Date = (string)r["date"] ?? "", Text = text });
                    }
                    undo.Remove(id);
                    WriteText(UndoFile, undo.ToString());
                    removed = true;
                }
                if (!removed) return false;
                sections.RemoveAll(s => s.Lines.Count == 0);
                Write(sections);
            }
            Changed?.Invoke();
            return true;
        }

        /// <summary>prefs.index: the system prompt's section and the Options list (topics in file order).</summary>
        public JObject Index()
        {
            lock (_sync)
            {
                var sections = Read();
                return new JObject
                {
                    ["topics"] = new JArray(sections.Where(s => s.Lines.OfType<Preference>().Any()).Select(s => new JObject
                    {
                        ["topic"] = s.Topic,
                        ["items"] = new JArray(s.Lines.OfType<Preference>().Select(p => p.ToJson())),
                    })),
                    ["used"] = Used(sections),
                    ["cap"] = Cap,
                    ["file"] = File,
                };
            }
        }

        /// <summary>Creates the file with its heading when missing (for "Open file"); returns its path.</summary>
        public string EnsureFile()
        {
            lock (_sync)
            {
                if (!System.IO.File.Exists(File)) Write(new List<Section>());
            }
            return File;
        }

        // ---- the file ----

        private List<Section> Read()
        {
            var sections = new List<Section>();
            if (!System.IO.File.Exists(File)) return sections;
            Section current = null;
            foreach (var raw in System.IO.File.ReadAllLines(File, Encoding.UTF8))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("# ") || (current == null && !line.StartsWith("## "))) continue; // title and intro
                if (line.StartsWith("## "))
                {
                    current = new Section { Topic = CleanTopic(line.Substring(3)) };
                    sections.Add(current);
                    continue;
                }
                var m = Regex.Match(line, @"^\s*[-*]\s+(?:(\d{4}-\d{2}-\d{2}):\s*)?(.+)$");
                if (m.Success)
                {
                    var text = MemoryStore.Clean(m.Groups[2].Value);
                    current.Lines.Add(new Preference { Id = IdFor(current.Topic, text), Topic = current.Topic, Date = m.Groups[1].Value, Text = text });
                }
                else if (line.Trim().Length > 0)
                {
                    current.Lines.Add(line);
                }
            }
            return sections;
        }

        private void Write(List<Section> sections)
        {
            var sb = new StringBuilder();
            sb.Append(Title).Append("\n\n").Append(Intro).Append("\n");
            foreach (var s in sections)
            {
                sb.Append("\n## ").Append(s.Topic).Append("\n");
                foreach (var l in s.Lines)
                {
                    if (l is Preference p) sb.Append("- ").Append(string.IsNullOrEmpty(p.Date) ? "" : p.Date + ": ").Append(p.Text).Append("\n");
                    else sb.Append(l).Append("\n");
                }
            }
            WriteText(File, sb.ToString());
        }

        private JObject ReadUndo()
        {
            try
            {
                return System.IO.File.Exists(UndoFile) ? JObject.Parse(System.IO.File.ReadAllText(UndoFile)) : new JObject();
            }
            catch
            {
                return new JObject();
            }
        }

        /// <summary>What a save replaced, kept for its Undo (the last 50 saves).</summary>
        private void SaveUndo(string id, List<Preference> replaced)
        {
            var undo = ReadUndo();
            undo[id] = new JArray(replaced.Select(r => new JObject { ["topic"] = r.Topic, ["date"] = r.Date, ["text"] = r.Text }));
            while (undo.Count > 50) undo.Remove(undo.Properties().First().Name);
            WriteText(UndoFile, undo.ToString());
        }

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                System.IO.File.WriteAllText(tmp, text, new UTF8Encoding(false));
                AtomicFile.Replace(tmp, path);
            }
            finally
            {
                if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
            }
        }

        private static int Used(IEnumerable<Section> sections) =>
            sections.Sum(s => s.Topic.Length + 4 + s.Lines.OfType<Preference>().Sum(p => p.Text.Length + 14));

        private static string CleanTopic(string topic)
        {
            var t = Regex.Replace(topic ?? "", @"\s+", " ").Trim().TrimStart('#').Trim();
            return t.Length == 0 ? "General" : t.Length > 60 ? t.Substring(0, 60) : t;
        }

        internal static string IdFor(string topic, string text)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(topic.ToLowerInvariant() + "\n" + text.ToLowerInvariant()));
                return "p" + string.Concat(hash.Take(3).Select(b => b.ToString("x2")));
            }
        }
    }
}
