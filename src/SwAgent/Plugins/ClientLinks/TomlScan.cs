using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SwAgent.Plugins
{
    /// <summary>A TOML file TomlScan can't read safely (an unclosed string or bracket, a broken key).</summary>
    internal sealed class TomlScanException : Exception
    {
        public TomlScanException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// A small TOML lexer for editing Codex's config.toml as text (docs/plugins/local-mcp.md 7.3): it tracks basic, literal and
    /// multi-line strings, comments and bracket depth (arrays and inline tables may span lines), and reports where each table
    /// header and each key = value statement is, by line. It doesn't build values: the edit splices whole lines, so every line
    /// outside SolidWorks Agent's own block stays byte-identical, and no TOML library has to load inside SolidWorks.
    /// </summary>
    internal sealed class TomlScan
    {
        /// <summary>A [table] or [[array]] header.</summary>
        internal sealed class Header
        {
            public int Line;
            public string[] Path;
            public bool IsArray;

            /// <summary>The header as written, without a trailing comment ("[mcp_servers.solidworks-agent]").</summary>
            public string Raw;
        }

        /// <summary>A key = value statement.</summary>
        internal sealed class Statement
        {
            public int Line;
            public int LastLine;

            /// <summary>The header path it belongs to (empty at the root).</summary>
            public string[] Table;
            public string[] Key;
            public string RawValue;
        }

        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Terminators = new List<string>();
        public readonly List<Header> Headers = new List<Header>();
        public readonly List<Statement> Statements = new List<Statement>();

        private readonly string _t;
        private readonly List<int> _lineStarts = new List<int>();
        private int _i;

        private TomlScan(string text)
        {
            _t = text;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                _lineStarts.Add(start);
                Lines.Add(text.Substring(start, end - start));
                Terminators.Add(text.Substring(end, i + 1 - end));
                start = i + 1;
            }
            if (start < text.Length)
            {
                _lineStarts.Add(start);
                Lines.Add(text.Substring(start));
                Terminators.Add("");
            }
        }

        /// <summary>Scan a file's text; throws TomlScanException when it can't be read safely.</summary>
        public static TomlScan Scan(string text)
        {
            var s = new TomlScan(text ?? "");
            s.Run();
            return s;
        }

        /// <summary>The text of lines [from, to) with their line endings.</summary>
        public string Join(int from, int to)
        {
            var sb = new StringBuilder();
            for (var i = from; i < to; i++) sb.Append(Lines[i]).Append(Terminators[i]);
            return sb.ToString();
        }

        private int LineOf(int pos)
        {
            var idx = _lineStarts.BinarySearch(pos);
            return idx >= 0 ? idx : Math.Max(0, ~idx - 1);
        }

        private bool End => _i >= _t.Length;
        private char C => _t[_i];
        private char At(int k) => _i + k < _t.Length ? _t[_i + k] : '\0';

        private TomlScanException Fail(string what) => new TomlScanException($"{what} (line {LineOf(Math.Min(_i, Math.Max(0, _t.Length - 1))) + 1}).");

        private void Run()
        {
            var table = new string[0];
            while (true)
            {
                SkipSpaces();
                if (End) return;
                if (C == '\n' || C == '\r' && At(1) == '\n')
                {
                    _i += C == '\r' ? 2 : 1;
                    continue;
                }
                if (C == '#')
                {
                    SkipComment();
                    continue;
                }
                var line = LineOf(_i);
                var start = _i;
                if (C == '[')
                {
                    var isArray = At(1) == '[';
                    _i += isArray ? 2 : 1;
                    var path = Key();
                    if (End || C != ']' || isArray && At(1) != ']') throw Fail("A table header isn't closed");
                    _i += isArray ? 2 : 1;
                    var raw = _t.Substring(start, _i - start);
                    EndOfLine();
                    Headers.Add(new Header { Line = line, Path = path, IsArray = isArray, Raw = raw });
                    table = path;
                    continue;
                }
                var key = Key();
                SkipSpaces();
                if (End || C != '=') throw Fail("Expected = after a key");
                _i++;
                SkipSpaces();
                var valueStart = _i;
                Value();
                var rawValue = _t.Substring(valueStart, _i - valueStart);
                var last = LineOf(Math.Max(valueStart, _i - 1));
                EndOfLine();
                Statements.Add(new Statement { Line = line, LastLine = last, Table = table, Key = key, RawValue = rawValue });
            }
        }

        private void SkipSpaces()
        {
            while (!End && (C == ' ' || C == '\t')) _i++;
        }

        private void SkipComment()
        {
            while (!End && C != '\n') _i++;
        }

        /// <summary>After a statement: spaces, an optional comment, then a line break or the end.</summary>
        private void EndOfLine()
        {
            SkipSpaces();
            if (!End && C == '#') SkipComment();
            if (End) return;
            if (C == '\n') _i++;
            else if (C == '\r' && At(1) == '\n') _i += 2;
            else throw Fail("Unexpected text after a value");
        }

        /// <summary>Spaces, line breaks and comments (inside arrays and inline tables).</summary>
        private void SkipBlank()
        {
            while (!End)
            {
                if (C == ' ' || C == '\t' || C == '\n' || C == '\r') _i++;
                else if (C == '#') SkipComment();
                else return;
            }
        }

        /// <summary>A dotted key: bare, "basic" or 'literal' parts.</summary>
        private string[] Key()
        {
            var parts = new List<string>();
            while (true)
            {
                SkipSpaces();
                if (End) throw Fail("A key is cut off");
                if (C == '"')
                {
                    if (At(1) == '"' && At(2) == '"') throw Fail("A key can't be a multi-line string");
                    parts.Add(BasicString());
                }
                else if (C == '\'')
                {
                    if (At(1) == '\'' && At(2) == '\'') throw Fail("A key can't be a multi-line string");
                    parts.Add(LiteralString());
                }
                else
                {
                    var s = _i;
                    while (!End && (char.IsLetterOrDigit(C) && C < 128 || C == '_' || C == '-')) _i++;
                    if (_i == s) throw Fail("Expected a key");
                    parts.Add(_t.Substring(s, _i - s));
                }
                SkipSpaces();
                if (!End && C == '.')
                {
                    _i++;
                    continue;
                }
                return parts.ToArray();
            }
        }

        private void Value()
        {
            if (End) throw Fail("A value is missing");
            switch (C)
            {
                case '"':
                    if (At(1) == '"' && At(2) == '"') MultiLine('"');
                    else BasicString();
                    return;
                case '\'':
                    if (At(1) == '\'' && At(2) == '\'') MultiLine('\'');
                    else LiteralString();
                    return;
                case '[':
                    _i++;
                    while (true)
                    {
                        SkipBlank();
                        if (End) throw Fail("An array isn't closed");
                        if (C == ']')
                        {
                            _i++;
                            return;
                        }
                        Value();
                        SkipBlank();
                        if (End) throw Fail("An array isn't closed");
                        if (C == ',') _i++;
                        else if (C != ']') throw Fail("Expected , or ] in an array");
                    }
                case '{':
                    _i++;
                    while (true)
                    {
                        SkipBlank();
                        if (End) throw Fail("An inline table isn't closed");
                        if (C == '}')
                        {
                            _i++;
                            return;
                        }
                        Key();
                        SkipSpaces();
                        if (End || C != '=') throw Fail("Expected = in an inline table");
                        _i++;
                        SkipSpaces();
                        Value();
                        SkipBlank();
                        if (End) throw Fail("An inline table isn't closed");
                        if (C == ',') _i++;
                        else if (C != '}') throw Fail("Expected , or } in an inline table");
                    }
                default:
                {
                    // Numbers, booleans, dates: up to the next separator.
                    var s = _i;
                    while (!End && C != ' ' && C != '\t' && C != '\n' && C != '\r' && C != ',' && C != ']' && C != '}' && C != '#') _i++;
                    if (_i == s) throw Fail("A value is missing");
                    return;
                }
            }
        }

        private string BasicString()
        {
            _i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (End || C == '\n') throw Fail("A string isn't closed");
                var c = C;
                if (c == '"')
                {
                    _i++;
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    _i++;
                    if (End) throw Fail("A string isn't closed");
                    sb.Append(Escape());
                    continue;
                }
                sb.Append(c);
                _i++;
            }
        }

        private string Escape()
        {
            var e = C;
            _i++;
            switch (e)
            {
                case '"': return "\"";
                case '\\': return "\\";
                case 'b': return "\b";
                case 'f': return "\f";
                case 'n': return "\n";
                case 'r': return "\r";
                case 't': return "\t";
                case 'e': return "\u001b";
                case 'u':
                case 'U':
                {
                    var len = e == 'u' ? 4 : 8;
                    if (_i + len > _t.Length) throw Fail("A \\u escape is cut off");
                    var hex = _t.Substring(_i, len);
                    if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw Fail("A \\u escape isn't hex");
                    _i += len;
                    try
                    {
                        return char.ConvertFromUtf32(code);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        throw Fail("A \\u escape isn't a character");
                    }
                }
                default:
                    throw Fail("Unknown escape in a string");
            }
        }

        private string LiteralString()
        {
            _i++;
            var s = _i;
            while (true)
            {
                if (End || C == '\n') throw Fail("A string isn't closed");
                if (C == '\'')
                {
                    var v = _t.Substring(s, _i - s);
                    _i++;
                    return v;
                }
                _i++;
            }
        }

        /// <summary>"""...""" or '''...''' (a backslash in a basic one escapes the next character); up to two extra quotes may end it.</summary>
        private void MultiLine(char q)
        {
            _i += 3;
            while (true)
            {
                if (End) throw Fail("A multi-line string isn't closed");
                if (q == '"' && C == '\\')
                {
                    _i += 2;
                    continue;
                }
                if (C == q && At(1) == q && At(2) == q)
                {
                    _i += 3;
                    for (var k = 0; k < 2 && !End && C == q; k++) _i++;
                    return;
                }
                _i++;
            }
        }

        // ---------------------------------------------------------------- reading simple values

        /// <summary>A single-line "basic" or 'literal' string value; null for anything else.</summary>
        public static string ReadString(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw[0] != '"' && raw[0] != '\'') return null;
            try
            {
                var s = new TomlScan(raw);
                var v = raw[0] == '"' ? s.BasicString() : s.LiteralString();
                return s.End ? v : null;
            }
            catch (TomlScanException)
            {
                return null;
            }
        }

        /// <summary>An array of single-line strings; null for anything else.</summary>
        public static List<string> ReadStrings(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw[0] != '[') return null;
            try
            {
                var s = new TomlScan(raw);
                var list = new List<string>();
                s._i = 1;
                while (true)
                {
                    s.SkipBlank();
                    if (s.End) return null;
                    if (s.C == ']') return s._i + 1 == raw.Length ? list : null;
                    if (s.C == '"') list.Add(s.BasicString());
                    else if (s.C == '\'') list.Add(s.LiteralString());
                    else return null;
                    s.SkipBlank();
                    if (!s.End && s.C == ',') s._i++;
                }
            }
            catch (TomlScanException)
            {
                return null;
            }
        }

        public static bool StartsWith(string[] path, params string[] prefix) =>
            path.Length >= prefix.Length && prefix.Select((p, k) => path[k] == p).All(x => x);
    }
}
