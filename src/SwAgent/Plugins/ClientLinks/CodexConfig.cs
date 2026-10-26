using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unify3D.LocalLink;

namespace SwAgent.Plugins
{
    /// <summary>
    /// [mcp_servers.unify3d] in Codex's config.toml (docs/plugins/local-mcp.md 7.3), edited as text with TomlScan: Codex writes the
    /// file itself (comments, quoted keys, agent-bridge's managed block), so Unify3D appends one marked block and later removes
    /// exactly those lines. Every other line stays byte-identical; add then remove gives back the original bytes.
    /// </summary>
    internal static class CodexConfig
    {
        public const string Label = "Codex";
        public const string StartMarker = "# >>> unify3d (managed by Unify3D Agent: the Codex MCP plugin; turn it off in Unify3D to remove) >>>";
        public const string EndMarker = "# <<< unify3d <<<";
        public const string HeaderText = "[mcp_servers.unify3d]";

        public static string ConflictText =>
            "Codex already has an MCP server named \"unify3d\" that Unify3D didn't add. Rename or remove it in Codex's settings, then try again.";

        public static string UnreadableText => "Codex's config.toml couldn't be read safely. Unify3D won't change it.";

        /// <summary>A TOML string: 'literal' unless the text holds a quote; then "basic" with \\ and \" escaped.</summary>
        public static string TomlString(string s) =>
            s.IndexOf('\'') < 0 && s.IndexOf('\n') < 0 ? "'" + s + "'" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>The managed block, each line ending with eol.</summary>
        public static string Block(string exe, string eol) => string.Join(eol, new[]
        {
            StartMarker,
            HeaderText,
            "command = " + TomlString(exe),
            "args = [\"--client\", \"" + LinkProtocol.Codex + "\"]",
            "startup_timeout_sec = 20",
            "tool_timeout_sec = 120",
            EndMarker,
        }) + eol;

        /// <summary>What the file says about "unify3d".</summary>
        private sealed class Analysis
        {
            public bool Conflict;

            /// <summary>Unify3D's own [mcp_servers.unify3d] header, when there is one.</summary>
            public TomlScan.Header Main;
            public TomlScan.Statement Command;

            /// <summary>Line ranges [from, to) that are Unify3D's: its tables, and its markers when they sit directly around them.</summary>
            public List<(int From, int To)> Regions = new List<(int, int)>();
        }

        private static TomlScan Scan(string text)
        {
            try
            {
                return TomlScan.Scan(text);
            }
            catch (TomlScanException)
            {
                throw new ClientConfigException(UnreadableText);
            }
        }

        private static bool IsOurs(string[] path) => TomlScan.StartsWith(path, "mcp_servers", "unify3d");

        private static Analysis Analyze(TomlScan scan)
        {
            var a = new Analysis();
            var ours = scan.Headers.Where(h => IsOurs(h.Path)).ToList();
            var mains = ours.Where(h => h.Path.Length == 2).ToList();

            // Any other definition of mcp_servers.unify3d is someone else's.
            foreach (var s in scan.Statements)
            {
                // A dotted key that reaches mcp_servers.unify3d from outside its table (the root, [mcp_servers]), or an inline table.
                if (IsOurs(s.Table.Concat(s.Key).ToArray()) && !IsOurs(s.Table)) a.Conflict = true;
                if (s.Table.Length == 0 && s.Key.Length == 1 && s.Key[0] == "mcp_servers" && s.RawValue.Contains("unify3d")) a.Conflict = true;
            }
            if (mains.Count > 1 || mains.Any(h => h.IsArray || h.Raw != HeaderText) || ours.Count > 0 && mains.Count == 0) a.Conflict = true;
            if (a.Conflict || mains.Count == 0) return a;

            var main = mains[0];
            var mine = scan.Statements.Where(s => s.Table.Length == 2 && IsOurs(s.Table)).ToList();
            var command = mine.FirstOrDefault(s => s.Key.Length == 1 && s.Key[0] == "command");
            var args = mine.FirstOrDefault(s => s.Key.Length == 1 && s.Key[0] == "args");
            if (!ClientLinks.IsOwned(command == null ? null : TomlScan.ReadString(command.RawValue), args == null ? null : TomlScan.ReadStrings(args.RawValue), LinkProtocol.Codex))
            {
                a.Conflict = true;
                return a;
            }
            a.Main = main;
            a.Command = command;

            // Our tables, each from its header to its last statement; ours that follow each other (no other header between)
            // make one range, so blank lines between them go too.
            var headers = scan.Headers.OrderBy(h => h.Line).ToList();
            (int From, int To)? open = null;
            for (var k = 0; k < headers.Count; k++)
            {
                var h = headers[k];
                if (!IsOurs(h.Path))
                {
                    if (open != null) a.Regions.Add(open.Value);
                    open = null;
                    continue;
                }
                var next = k + 1 < headers.Count ? headers[k + 1].Line : scan.Lines.Count;
                var last = scan.Statements.Where(s => s.Line > h.Line && s.Line < next).Select(s => s.LastLine).DefaultIfEmpty(h.Line).Max();
                open = open == null ? (h.Line, last + 1) : (open.Value.From, last + 1);
            }
            if (open != null) a.Regions.Add(open.Value);

            // The markers, when directly around a range.
            for (var r = 0; r < a.Regions.Count; r++)
            {
                var (from, to) = a.Regions[r];
                if (from > 0 && scan.Lines[from - 1].Trim() == StartMarker) from--;
                if (to < scan.Lines.Count && scan.Lines[to].Trim() == EndMarker) to++;
                a.Regions[r] = (from, to);
            }
            return a;
        }

        public static LinkEntryState Inspect(string text)
        {
            Analysis a;
            try
            {
                a = Analyze(Scan(text));
            }
            catch (ClientConfigException)
            {
                return LinkEntryState.Unreadable;
            }
            return a.Conflict ? LinkEntryState.Conflict : a.Main != null ? LinkEntryState.Present : LinkEntryState.Missing;
        }

        /// <summary>
        /// Append the block (after a blank line) when there is none; refresh the command of Unify3D's own entry when the exe moved.
        /// With addIfMissing false only an existing entry is refreshed. A foreign "unify3d" throws the conflict text.
        /// </summary>
        public static string Add(string text, string exe, bool addIfMissing)
        {
            var scan = Scan(text);
            var a = Analyze(scan);
            if (a.Conflict) throw new ClientConfigException(ConflictText);
            var eol = ClientConfigFile.EolOf(text);
            if (a.Main != null)
            {
                var c = a.Command;
                if (string.Equals(TomlScan.ReadString(c.RawValue), exe, StringComparison.OrdinalIgnoreCase)) return text;
                if (c.LastLine != c.Line) throw new ClientConfigException(UnreadableText);
                return scan.Join(0, c.Line) + "command = " + TomlString(exe) + scan.Terminators[c.Line] + scan.Join(c.Line + 1, scan.Lines.Count);
            }
            if (!addIfMissing) return text;
            if (text.Length == 0) return Block(exe, eol);
            return text + (text.EndsWith("\n") ? "" : eol) + eol + Block(exe, eol);
        }

        /// <summary>Remove Unify3D's lines and nothing else (a foreign "unify3d" stays: conflict is set).</summary>
        public static string Remove(string text, out bool conflict)
        {
            var scan = Scan(text);
            var a = Analyze(scan);
            conflict = a.Conflict;
            if (a.Conflict || a.Main == null) return text;
            var drop = new HashSet<int>();
            foreach (var (from, to) in a.Regions)
            {
                for (var k = from; k < to; k++) drop.Add(k);
                // The blank line Add put before a block at the end of the file.
                var atEnd = Enumerable.Range(to, scan.Lines.Count - to).All(drop.Contains);
                if (atEnd && from > 0 && scan.Lines[from - 1].Trim().Length == 0) drop.Add(from - 1);
            }
            var sb = new StringBuilder();
            for (var k = 0; k < scan.Lines.Count; k++)
            {
                if (!drop.Contains(k)) sb.Append(scan.Lines[k]).Append(scan.Terminators[k]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The line-level guarantee: after must scan, and the lines that differ (between the common head and tail, compared
        /// without line endings) may only be blank, comments, or lines of Unify3D's own tables.
        /// </summary>
        public static void Check(string before, string after)
        {
            TomlScan b, f;
            try
            {
                b = TomlScan.Scan(before);
                f = TomlScan.Scan(after);
            }
            catch (TomlScanException ex)
            {
                throw new ClientConfigException(ex.Message);
            }
            var head = 0;
            while (head < b.Lines.Count && head < f.Lines.Count && b.Lines[head] == f.Lines[head]) head++;
            var tail = 0;
            while (tail < b.Lines.Count - head && tail < f.Lines.Count - head && b.Lines[b.Lines.Count - 1 - tail] == f.Lines[f.Lines.Count - 1 - tail]) tail++;
            void Allowed(TomlScan s, int from, int to)
            {
                var mine = OwnLines(s);
                for (var k = from; k < to; k++)
                {
                    var t = s.Lines[k].Trim();
                    if (t.Length == 0 || t.StartsWith("#") || mine.Contains(k)) continue;
                    throw new ClientConfigException($"Line {k + 1} would change.");
                }
            }
            Allowed(b, head, b.Lines.Count - tail);
            Allowed(f, head, f.Lines.Count - tail);
        }

        /// <summary>Every line inside a [mcp_servers.unify3d...] table: from its header to the next other header.</summary>
        private static HashSet<int> OwnLines(TomlScan s)
        {
            var set = new HashSet<int>();
            var headers = s.Headers.OrderBy(h => h.Line).ToList();
            for (var k = 0; k < headers.Count; k++)
            {
                if (!IsOurs(headers[k].Path)) continue;
                var next = k + 1 < headers.Count ? headers[k + 1].Line : s.Lines.Count;
                for (var l = headers[k].Line; l < next; l++) set.Add(l);
            }
            return set;
        }
    }
}
