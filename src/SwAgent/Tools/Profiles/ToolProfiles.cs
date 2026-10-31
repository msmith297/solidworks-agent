using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>
    /// A saved set of a tool's input values (docs/tool-profiles.md): { schema, tool, name, created, updated, toolVersion,
    /// values }. Values are text with their unit ("2000 psi"), data choices their row id, Optional inputs { on, value }; a
    /// profile may hold only some inputs. Shipped profiles (a tool's own profiles\ folder) are locked.
    /// </summary>
    internal sealed class ToolProfile
    {
        public const int Schema = 1;

        public string Tool;
        public string Name;
        public string Created;
        public string Updated;

        /// <summary>The tool's TOOL.md "updated" when the profile was saved (null when it has none).</summary>
        public string ToolVersion;

        public JObject Values = new JObject();

        /// <summary>The file it was read from or written to.</summary>
        public string Path;

        /// <summary>Shipped with the tool (built-in or plugin): can be loaded and duplicated, never changed.</summary>
        public bool Locked;

        public JObject ToJson() => new JObject
        {
            ["schema"] = Schema,
            ["tool"] = Tool,
            ["name"] = Name,
            ["created"] = Created,
            ["updated"] = Updated,
            ["toolVersion"] = ToolVersion,
            ["values"] = Values ?? new JObject(),
        };

        /// <summary>A profile file's JSON as written: text that looks like a date ("2026-10-19", a note) stays text.</summary>
        public static JObject ParseJson(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text ?? "")) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        /// <summary>A profile from its JSON; FormatException (with a plain message) when it isn't one.</summary>
        public static ToolProfile Parse(JObject o, string path = null, bool locked = false)
        {
            if (o == null) throw new FormatException("It is not a tool profile (no JSON object).");
            if (!(o["values"] is JObject values)) throw new FormatException("It is not a tool profile (no \"values\").");
            var name = ToolData.Text(o["name"] as JValue)?.Trim();
            if (string.IsNullOrEmpty(name))
                name = path == null ? null : System.IO.Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name)) throw new FormatException("The profile has no name.");
            var schema = (o["schema"] as JValue)?.Value;
            if (schema != null && Convert.ToInt32(schema, CultureInfo.InvariantCulture) > Schema)
                throw new FormatException("The profile was saved by a newer SolidWorks Agent; update the add-in to load it.");
            return new ToolProfile
            {
                Tool = ToolData.Text(o["tool"] as JValue),
                Name = name,
                Created = ToolData.Text(o["created"] as JValue),
                Updated = ToolData.Text(o["updated"] as JValue),
                ToolVersion = ToolData.Text(o["toolVersion"] as JValue),
                Values = values,
                Path = path,
                Locked = locked,
            };
        }
    }

    /// <summary>
    /// The profile store. Per PC: %APPDATA%\SwAgent\tools\profiles\&lt;tool&gt;\&lt;slug&gt;.json (the user's), and
    /// _settings.json there ({ "default": name, "lastUsed": name }). Shipped profiles are the *.json files in the tool's own
    /// profiles\ folder (built-in tools and plugin tools ship them; they are replaced by installs, so always locked). Names
    /// are unique per tool, ignoring case; a user profile can't take a shipped profile's name. Writes go through
    /// AtomicFile; the data folder honours SWAGENT_DATA_DIR. root / toolDir are for tests.
    /// </summary>
    internal static class ToolProfiles
    {
        public const string SettingsFile = "_settings.json";
        public const int MaxName = 60;

        /// <summary>The panel's "(Tool defaults)" entry: not a profile name.</summary>
        public const string ToolDefaults = "(Tool defaults)";

        public static string DefaultRoot => Path.Combine(UserTools.Root, "profiles");

        /// <summary>The user's profiles of a tool ("plugin:tool" as plugin+tool).</summary>
        public static string UserDir(string tool, string root = null) => Path.Combine(root ?? DefaultRoot, UserTools.StorageKey(tool));

        /// <summary>The tool's shipped profiles: its folder's profiles\ (null when the tool has no folder).</summary>
        public static string ShippedDir(string tool, string toolDir = null)
        {
            var dir = toolDir ?? SafeToolDir(tool);
            return dir == null ? null : Path.Combine(dir, "profiles");
        }

        private static string SafeToolDir(string tool)
        {
            try
            {
                return UserTools.DirOf(tool);
            }
            catch (Exception ex)
            {
                Log.Warn($"Tool folder of '{tool}' unavailable: {ex.Message}");
                return null;
            }
        }

        // ---- reading ----

        /// <summary>The user's profiles by name, then the shipped ones by name. Unreadable files are logged and skipped.</summary>
        public static List<ToolProfile> List(string tool, string root = null, string toolDir = null)
        {
            var mine = Read(UserDir(tool, root), locked: false);
            var shipped = Read(ShippedDir(tool, toolDir), locked: true)
                .Where(s => !mine.Any(m => SameName(m.Name, s.Name))).ToList(); // a hand-made clash: the user's wins
            return mine.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Concat(shipped.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
        }

        /// <summary>The names of a tool's profiles (the tool index lists them): the user's, then the shipped ones; never throws.</summary>
        public static List<string> Names(string tool, string root = null, string toolDir = null)
        {
            try
            {
                return List(tool, root, toolDir).Select(p => p.Name).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"Listing the profiles of '{tool}' failed: {ex.Message}");
                return new List<string>();
            }
        }

        private static List<ToolProfile> Read(string dir, bool locked)
        {
            var list = new List<ToolProfile>();
            if (dir == null || !Directory.Exists(dir)) return list;
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(Path.GetFileName(file), SettingsFile, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var p = ToolProfile.Parse(ToolProfile.ParseJson(File.ReadAllText(file)), file, locked);
                    if (list.Any(x => SameName(x.Name, p.Name))) continue; // two files, one name: the first wins
                    list.Add(p);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Skipping the unreadable tool profile {file}: {ex.Message}");
                }
            }
            return list;
        }

        /// <summary>A profile by name (ignoring case): the user's first, then a shipped one; null when there is none.</summary>
        public static ToolProfile Find(string tool, string name, string root = null, string toolDir = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return List(tool, root, toolDir).FirstOrDefault(p => SameName(p.Name, name));
        }

        public static bool SameName(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.CurrentCultureIgnoreCase);

        // ---- settings: the default and the last used profile ----

        public static string DefaultName(string tool, string root = null) => ToolData.Text(ReadSettings(tool, root)["default"] as JValue);

        public static string LastUsed(string tool, string root = null) => ToolData.Text(ReadSettings(tool, root)["lastUsed"] as JValue);

        private static JObject ReadSettings(string tool, string root)
        {
            var path = Path.Combine(UserDir(tool, root), SettingsFile);
            try
            {
                return File.Exists(path) ? ToolProfile.ParseJson(File.ReadAllText(path)) : new JObject();
            }
            catch (Exception ex)
            {
                // The next write replaces it: keep a copy for the user, as the knowledge store does.
                Log.Warn($"Profile settings of '{tool}' unreadable ({ex.Message}); keeping them as {SettingsFile}.bad.");
                try
                {
                    File.Copy(path, path + ".bad", true);
                }
                catch
                {
                    // best effort
                }
                return new JObject();
            }
        }

        private static void WriteSettings(string tool, JObject settings, string root)
        {
            var path = Path.Combine(UserDir(tool, root), SettingsFile);
            foreach (var p in settings.Properties().Where(p => p.Value.Type == JTokenType.Null).ToList()) p.Remove();
            WriteJson(path, settings);
        }

        /// <summary>
        /// The profile the tool's inputs open with (a user or shipped profile), or none when profile is null or empty. Standing
        /// instructions ("always use Shop A for molds") call this. Throws ToolException when there is no such profile.
        /// </summary>
        public static void SetDefault(string tool, string profile, string root = null, string toolDir = null)
        {
            var settings = ReadSettings(tool, root);
            if (string.IsNullOrWhiteSpace(profile))
            {
                settings["default"] = null;
            }
            else
            {
                var p = Find(tool, profile, root, toolDir) ?? throw new ToolException(NoSuch(tool, profile, root, toolDir));
                settings["default"] = p.Name;
            }
            WriteSettings(tool, settings, root);
        }

        /// <summary>The profile a run used (OK with it loaded): shown as the last used one.</summary>
        public static void MarkUsed(string tool, string profile, string root = null)
        {
            try
            {
                var settings = ReadSettings(tool, root);
                if (SameName(ToolData.Text(settings["lastUsed"] as JValue), profile)) return;
                settings["lastUsed"] = profile;
                WriteSettings(tool, settings, root);
            }
            catch (Exception ex)
            {
                Log.Warn($"Recording the last used profile of '{tool}' failed: {ex.Message}");
            }
        }

        // ---- writing ----

        /// <summary>Why a name can't be used (null when it can).</summary>
        public static string CheckName(string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "Give the profile a name.";
            if (n.Length > MaxName) return $"Use a shorter name (up to {MaxName} characters).";
            if (n.Any(char.IsControl)) return "The name can't contain line breaks or tabs.";
            if (SameName(n, ToolDefaults)) return "That name is taken by the tool defaults.";
            return null;
        }

        /// <summary>
        /// Save a new profile of the user's (overwrite: replace the user's profile of that name, keeping when it was created).
        /// A name taken by a shipped profile, or by one of the user's without overwrite, is refused.
        /// </summary>
        public static ToolProfile Save(string tool, string name, JObject values, string toolVersion = null, bool overwrite = false, string root = null, string toolDir = null)
        {
            var bad = CheckName(name);
            if (bad != null) throw new ToolException(bad);
            name = name.Trim();
            var existing = Find(tool, name, root, toolDir);
            if (existing != null && existing.Locked) throw new ToolException($"'{existing.Name}' is a profile that comes with the tool. Pick another name.");
            if (existing != null && !overwrite) throw new ToolException($"There is already a profile named '{existing.Name}'. Update it, or pick another name.");
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var p = new ToolProfile
            {
                Tool = tool,
                Name = existing?.Name ?? name,
                Created = existing?.Created ?? now,
                Updated = now,
                ToolVersion = toolVersion,
                Values = (JObject)(values ?? new JObject()).DeepClone(),
                Path = existing?.Path ?? FreePath(UserDir(tool, root), name),
            };
            WriteJson(p.Path, p.ToJson());
            Changed();
            return p;
        }

        /// <summary>Replace a user profile's values (a shipped profile can't be updated: save it under a new name).</summary>
        public static ToolProfile Update(string tool, string name, JObject values, string toolVersion = null, string root = null, string toolDir = null)
        {
            var existing = Find(tool, name, root, toolDir) ?? throw new ToolException(NoSuch(tool, name, root, toolDir));
            if (existing.Locked) throw new ToolException($"'{existing.Name}' comes with the tool and can't be changed. Save it under a new name.");
            return Save(tool, existing.Name, values, toolVersion, overwrite: true, root: root, toolDir: toolDir);
        }

        public static ToolProfile Rename(string tool, string name, string newName, string root = null, string toolDir = null)
        {
            var p = Find(tool, name, root, toolDir) ?? throw new ToolException(NoSuch(tool, name, root, toolDir));
            if (p.Locked) throw new ToolException($"'{p.Name}' comes with the tool and can't be renamed. Duplicate it instead.");
            var bad = CheckName(newName);
            if (bad != null) throw new ToolException(bad);
            newName = newName.Trim();
            var other = Find(tool, newName, root, toolDir);
            if (other != null && other.Path != p.Path) throw new ToolException($"There is already a profile named '{other.Name}'.");
            var renamed = new ToolProfile
            {
                Tool = p.Tool ?? tool, Name = newName, Created = p.Created, Updated = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ToolVersion = p.ToolVersion, Values = p.Values, Path = FreePath(UserDir(tool, root), newName, p.Path),
            };
            WriteJson(renamed.Path, renamed.ToJson());
            if (!string.Equals(renamed.Path, p.Path, StringComparison.OrdinalIgnoreCase)) File.Delete(p.Path);
            var settings = ReadSettings(tool, root);
            var changed = false;
            foreach (var key in new[] { "default", "lastUsed" })
            {
                if (!SameName(ToolData.Text(settings[key] as JValue), p.Name)) continue;
                settings[key] = newName;
                changed = true;
            }
            if (changed) WriteSettings(tool, settings, root);
            Changed();
            return renamed;
        }

        /// <summary>A copy under a new name (shipped profiles too: that is how one is changed). newName null: "Name copy".</summary>
        public static ToolProfile Duplicate(string tool, string name, string newName = null, string root = null, string toolDir = null)
        {
            var p = Find(tool, name, root, toolDir) ?? throw new ToolException(NoSuch(tool, name, root, toolDir));
            newName = string.IsNullOrWhiteSpace(newName) ? FreeName(tool, p.Name + " copy", root, toolDir) : newName.Trim();
            return Save(tool, newName, p.Values, p.ToolVersion, root: root, toolDir: toolDir);
        }

        public static void Delete(string tool, string name, string root = null, string toolDir = null)
        {
            var p = Find(tool, name, root, toolDir) ?? throw new ToolException(NoSuch(tool, name, root, toolDir));
            if (p.Locked) throw new ToolException($"'{p.Name}' comes with the tool and can't be deleted.");
            File.Delete(p.Path);
            var settings = ReadSettings(tool, root);
            var changed = false;
            foreach (var key in new[] { "default", "lastUsed" })
            {
                if (!SameName(ToolData.Text(settings[key] as JValue), p.Name)) continue;
                settings[key] = null;
                changed = true;
            }
            if (changed) WriteSettings(tool, settings, root);
            Changed();
        }

        /// <summary>Write a profile to a file of the user's choice (to share it).</summary>
        public static void Export(string tool, string name, string path, string root = null, string toolDir = null)
        {
            var p = Find(tool, name, root, toolDir) ?? throw new ToolException(NoSuch(tool, name, root, toolDir));
            var json = p.ToJson();
            json["tool"] = p.Tool ?? tool;
            WriteJson(path, json);
        }

        /// <summary>
        /// A profile file someone shared, saved as one of the user's profiles (a name already used gets " (2)"). Returns it, with
        /// a note when it was made for another tool. Throws ToolException when the text isn't a profile.
        /// </summary>
        public static ToolProfile Import(string tool, string json, out string note, string root = null, string toolDir = null)
        {
            ToolProfile p;
            try
            {
                p = ToolProfile.Parse(ToolProfile.ParseJson(json ?? ""));
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException)
            {
                throw new ToolException("That file isn't a tool profile: " + ex.Message);
            }
            note = null;
            if (!string.IsNullOrWhiteSpace(p.Tool) && !SameTool(p.Tool, tool)) note = $"It was saved for the tool '{p.Tool}'; inputs this tool doesn't have are ignored.";
            var name = p.Name.Length > MaxName ? p.Name.Substring(0, MaxName).Trim() : p.Name;
            if (CheckName(name) != null) name = "Imported profile";
            return Save(tool, FreeName(tool, name, root, toolDir), p.Values, p.ToolVersion, root: root, toolDir: toolDir);
        }

        /// <summary>"design-compression-mold" and "plugin:design-compression-mold" name the same tool for a profile file.</summary>
        internal static bool SameTool(string a, string b)
        {
            string Last(string s) => (s ?? "").Trim().ToLowerInvariant().Split(':').Last();
            return Last(a) == Last(b);
        }

        /// <summary>
        /// A fork gets the user's profiles of the source tool and its settings (the default, the last used); shipped profiles
        /// travel with the tool folder. Profiles the fork already has are kept, unless fresh (a brand-new tool: anything
        /// there is left from a deleted tool of that name). except: profile names not copied (they shipped with the tool).
        /// </summary>
        public static int CopyAll(string fromTool, string toTool, string root = null, bool fresh = false, ICollection<string> except = null)
        {
            if (fresh) RemoveAll(toTool, root);
            var from = UserDir(fromTool, root);
            if (!Directory.Exists(from)) return 0;
            var to = UserDir(toTool, root);
            Directory.CreateDirectory(to);
            var copied = 0;
            foreach (var file in Directory.GetFiles(from, "*.json"))
            {
                var target = Path.Combine(to, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                try
                {
                    var o = ToolProfile.ParseJson(File.ReadAllText(file));
                    var settings = string.Equals(Path.GetFileName(file), SettingsFile, StringComparison.OrdinalIgnoreCase);
                    if (!settings && except != null && except.Any(n => SameName(n, (string)o["name"]))) continue;
                    if (!settings) o["tool"] = toTool;
                    WriteJson(target, o);
                    copied++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Copying the profile {file} to '{toTool}' failed: {ex.Message}");
                }
            }
            return copied;
        }

        /// <summary>A deleted tool's profiles and settings go with it (UserTools.Delete), so a new tool of that name starts clean.</summary>
        public static void RemoveAll(string tool, string root = null)
        {
            var key = UserTools.StorageKey(tool);
            if (key.Length == 0 || key.Contains("..") || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return; // only ever one tool's folder
            var dir = UserDir(tool, root);
            if (!Directory.Exists(dir)) return;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Removing the profiles of '{tool}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The profiles that came with a tool folder name the tool they now live in (a fork's copy of the original's shipped
        /// profiles), so their exports and duplicates load without a "saved for another tool" note. Returns how many changed.
        /// </summary>
        public static int Retarget(string toolDir, string toTool)
        {
            var dir = Path.Combine(toolDir, "profiles");
            if (!Directory.Exists(dir)) return 0;
            var changed = 0;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var o = ToolProfile.ParseJson(File.ReadAllText(file));
                    if ((string)o["tool"] == toTool) continue;
                    o["tool"] = toTool;
                    WriteJson(file, o);
                    changed++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Profile {file} not renamed to '{toTool}': {ex.Message}");
                }
            }
            return changed;
        }

        /// <summary>
        /// The user's profiles of a tool become profiles that ship with a tool folder (plugin_add_tool with profiles): written to
        /// its profiles\ for toTool, locked there for whoever installs it. Names the folder already has are skipped; the user's
        /// settings (default, last used) stay theirs. Returns the names written.
        /// </summary>
        public static List<string> Ship(string fromTool, string toolDir, string toTool, string root = null)
        {
            var dir = Path.Combine(toolDir, "profiles");
            var taken = new HashSet<string>(List(toTool, root, toolDir).Where(p => p.Locked).Select(p => p.Name), StringComparer.CurrentCultureIgnoreCase);
            var shipped = new List<string>();
            foreach (var p in List(fromTool, root).Where(p => !p.Locked))
            {
                if (!taken.Add(p.Name)) continue;
                var o = ToolProfile.ParseJson(File.ReadAllText(p.Path));
                o["tool"] = toTool;
                WriteJson(FreePath(dir, p.Name), o);
                shipped.Add(p.Name);
            }
            return shipped;
        }

        /// <summary>
        /// The checks plugin_validate runs on a tool folder's shipped profiles (profiles\*.json): "error: ..." for a file that
        /// isn't a profile or has a name used twice, "warning: ..." for a value the tool would ignore or keep at its default.
        /// </summary>
        public static List<string> CheckShipped(string toolDir, string toolName)
        {
            var findings = new List<string>();
            var dir = Path.Combine(toolDir, "profiles");
            if (!Directory.Exists(dir)) return findings;
            var label = $"tools/{Path.GetFileName(toolDir)}/profiles/";
            var spec = new ToolSpec { Name = toolName, Dir = toolDir, Inputs = ToolSpec.LoadInputs(toolDir) };
            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var at = label + Path.GetFileName(file);
                ToolProfile p;
                try
                {
                    p = ToolProfile.Parse(ToolProfile.ParseJson(File.ReadAllText(file)), file, locked: true);
                }
                catch (Exception ex)
                {
                    findings.Add($"error: {at}: {ex.Message}");
                    continue;
                }
                if (!names.Add(p.Name)) findings.Add($"error: {at}: another profile of this tool is named \"{p.Name}\".");
                if (CheckName(p.Name) is string bad) findings.Add($"error: {at}: {bad}");
                if (!string.IsNullOrWhiteSpace(p.Tool) && !SameTool(p.Tool, toolName)) findings.Add($"warning: {at} says it is for the tool \"{p.Tool}\".");
                var form = new ToolForm(spec, new ToolData(toolDir), imperial: false);
                findings.AddRange(ProfileValues.Apply(form, p.Values).Warnings.Select(w => $"warning: {at}: {w}"));
            }
            return findings;
        }

        // ---- runs: a profile asked for, and what the latest run used ----

        private static readonly ConcurrentDictionary<string, (string Profile, DateTime At)> Requests =
            new ConcurrentDictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The values of each tool's latest run in this session, as a profile holds them (save_tool_profile from_last_run).</summary>
        internal static readonly ConcurrentDictionary<string, JObject> LastRun = new ConcurrentDictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The next run of the tool opens with this profile (/run --profile, run_tool); it is forgotten after a minute.</summary>
        public static void Request(string tool, string profile) => Requests[tool] = (profile, DateTime.UtcNow);

        public static string TakeRequest(string tool)
        {
            if (tool == null || !Requests.TryRemove(tool, out var r)) return null;
            return DateTime.UtcNow - r.At < TimeSpan.FromMinutes(1) ? r.Profile : null;
        }

        // ---- files ----

        /// <summary>The tool index lists profile names: the panel gets the tools again.</summary>
        private static void Changed()
        {
            try
            {
                UserTools.NotifyChanged();
            }
            catch (Exception ex)
            {
                Log.Warn("Announcing the profile change failed: " + ex.Message);
            }
        }

        /// <summary>"Shop A - PU on the 50 t press" -> "shop-a-pu-on-the-50-t-press".</summary>
        internal static string Slug(string name)
        {
            var s = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            if (s.Length > 48) s = s.Substring(0, 48).Trim('-');
            return s.Length == 0 || s == "_settings" ? "profile" : s;
        }

        /// <summary>The file for a name: its slug, numbered when another profile has it (keep: the file the profile has now).</summary>
        private static string FreePath(string dir, string name, string keep = null)
        {
            var slug = Slug(name);
            for (var k = 1; ; k++)
            {
                var path = Path.Combine(dir, (k == 1 ? slug : slug + "-" + k) + ".json");
                if (!File.Exists(path) || string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) return path;
            }
        }

        /// <summary>The name, or "name (2)", "name (3)"... when it is taken.</summary>
        internal static string FreeName(string tool, string name, string root = null, string toolDir = null)
        {
            var taken = Names(tool, root, toolDir);
            var candidate = name;
            for (var k = 2; taken.Any(t => SameName(t, candidate)); k++) candidate = $"{name} ({k})";
            return candidate;
        }

        private static string NoSuch(string tool, string name, string root, string toolDir)
        {
            var names = Names(tool, root, toolDir);
            return $"There is no profile '{name}' for this tool." + (names.Count == 0 ? " It has no profiles yet." : $" Its profiles: {string.Join(", ", names)}.");
        }

        private static void WriteJson(string path, JObject json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(tmp, json.ToString(Formatting.Indented), new UTF8Encoding(false));
                AtomicFile.Replace(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
