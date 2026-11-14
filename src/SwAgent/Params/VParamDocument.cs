using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using Xarial.XCad;
using Xarial.XCad.Data.Enums;
using Xarial.XCad.Documents;
using Xarial.XCad.Documents.Services;
using Xarial.XCad.SolidWorks.Documents;

namespace SwAgent.Params
{
    /// <summary>
    /// One virtual parameter (docs/virtual-parameters.md): a named design value of a part or assembly, such as "Wall thickness
    /// 2.5 mm", "Hole count 8" or "Engraving text ACME". The agent keeps the model matching it; changing it in the table asks the
    /// agent to rebuild. Drives names the features (or dimensions, "D1@Sketch1") it controls, for the P-key filter and demos.
    /// An assembly's parameter can link to its parts' parameters: setting it sets them too.
    /// </summary>
    internal sealed class VParam
    {
        public string Name;

        /// <summary>length | angle | count | number | text | choice.</summary>
        public string Type = "length";

        public string Value = "";

        /// <summary>mm, in, deg, %, ... ("" for counts and text).</summary>
        public string Unit = "";

        /// <summary>What it controls and why, in a line.</summary>
        public string Note = "";

        public List<string> Drives = new List<string>();

        /// <summary>Allowed values of a choice.</summary>
        public List<string> Options = new List<string>();

        /// <summary>Assembly only: the parts' parameters it sets, as component file title + parameter name.</summary>
        public List<VLink> Links = new List<VLink>();

        public string Updated = "";

        public string Display => string.IsNullOrEmpty(Unit) ? Value : $"{Value} {Unit}";

        public JObject ToJson() => new JObject
        {
            ["name"] = Name,
            ["type"] = Type,
            ["value"] = Value,
            ["unit"] = Unit,
            ["note"] = Note,
            ["drives"] = new JArray(Drives),
            ["options"] = new JArray(Options),
            ["links"] = new JArray(Links.Select(l => new JObject { ["part"] = l.Part, ["param"] = l.Param })),
            ["updated"] = Updated,
        };

        public static VParam FromJson(JObject o) => new VParam
        {
            Name = (string)o["name"] ?? "",
            Type = (string)o["type"] ?? "length",
            Value = (string)o["value"] ?? "",
            Unit = (string)o["unit"] ?? "",
            Note = (string)o["note"] ?? "",
            Drives = (o["drives"] as JArray)?.Select(t => (string)t).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>(),
            Options = (o["options"] as JArray)?.Select(t => (string)t).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>(),
            Links = (o["links"] as JArray)?.OfType<JObject>().Select(l => new VLink { Part = (string)l["part"], Param = (string)l["param"] })
                .Where(l => !string.IsNullOrWhiteSpace(l.Part) && !string.IsNullOrWhiteSpace(l.Param)).ToList() ?? new List<VLink>(),
            Updated = (string)o["updated"] ?? "",
        };
    }

    internal sealed class VLink
    {
        /// <summary>The part's file title, e.g. "Rail Collar.SLDPRT".</summary>
        public string Part;

        public string Param;
    }

    /// <summary>
    /// A document's virtual parameters, kept in the file as the third-party storage stream SwAgent.VParams.v1 (JSON) and
    /// mirrored as "VP: name" custom properties, so they travel with the file and SolidWorks users without the add-in can
    /// read them. Written on every save (SolidWorks drops a stream that isn't rewritten).
    /// </summary>
    internal sealed class VParamDocument
    {
        public const string StreamName = "SwAgent.VParams.v1";
        public const string PropertyPrefix = "VP: ";

        private static readonly List<VParamDocument> Open = new List<VParamDocument>();

        private readonly ISwDocument _doc;
        private byte[] _unreadable; // a stream this version can't parse: written back untouched until replaced

        public VParamDocument(ISwDocument doc)
        {
            _doc = doc;
            lock (Open) Open.Add(this);
        }

        public List<VParam> Params { get; private set; } = new List<VParam>();

        public IModelDoc2 Model => _doc?.Model;

        /// <summary>Raised (on the UI thread) when the parameters change: the panel's table refreshes.</summary>
        public static event Action<VParamDocument> Changed;

        /// <summary>The parameters of an open document (parts loaded by an assembly included); null if the add-in hasn't seen it.</summary>
        public static VParamDocument For(IModelDoc2 model)
        {
            if (model == null) return null;
            lock (Open)
            {
                var hit = Open.FirstOrDefault(d => ReferenceEquals(d.Model, model));
                if (hit != null) return hit;
                string path = null, title = null;
                try
                {
                    path = model.GetPathName();
                    title = model.GetTitle();
                }
                catch
                {
                    return null;
                }
                return Open.FirstOrDefault(d => Same(d, path, title));
            }
        }

        private static bool Same(VParamDocument d, string path, string title)
        {
            try
            {
                var m = d.Model;
                if (m == null) return false;
                if (!string.IsNullOrEmpty(path)) return string.Equals(m.GetPathName(), path, StringComparison.OrdinalIgnoreCase);
                return string.Equals(m.GetTitle(), title, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public VParam Find(string name) => Params.FirstOrDefault(p => string.Equals(p.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>After a change: the custom properties follow, the file is marked changed (so a save writes the stream), the table refreshes.</summary>
        public void Commit(IEnumerable<string> removedNames = null)
        {
            _unreadable = null;
            var model = Model;
            if (model != null)
            {
                try
                {
                    var cpm = model.Extension.CustomPropertyManager[""];
                    foreach (var gone in removedNames ?? Enumerable.Empty<string>())
                        if (Find(gone) == null) cpm.Delete2(PropertyPrefix + gone);
                    foreach (var p in Params)
                        cpm.Add3(PropertyPrefix + p.Name, (int)swCustomInfoType_e.swCustomInfoText, p.Display, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    model.SetSaveFlag();
                }
                catch (Exception ex)
                {
                    Log.Warn("Updating the v param custom properties failed: " + ex.Message);
                }
            }
            Changed?.Invoke(this);
        }

        public void ReadFromFile(IXDocument doc)
        {
            try
            {
                using (var stream = doc.TryOpenStream(StreamName, AccessType_e.Read))
                {
                    if (stream == null) return;
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        var bytes = ms.ToArray();
                        try
                        {
                            var o = JObject.Parse(Encoding.UTF8.GetString(bytes));
                            Params = (o["params"] as JArray)?.OfType<JObject>().Select(VParam.FromJson).Where(p => p.Name.Length > 0).ToList() ?? new List<VParam>();
                        }
                        catch (Exception ex)
                        {
                            _unreadable = bytes;
                            Log.Warn($"The v params in '{doc.Title}' couldn't be read ({ex.Message}); they're kept as they are.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Reading v params from '{doc.Title}' failed", ex);
            }
        }

        /// <summary>Called during every save. Must always write, or SolidWorks discards the stream.</summary>
        public void WriteToFile(IXDocument doc)
        {
            try
            {
                byte[] bytes;
                if (_unreadable != null) bytes = _unreadable;
                else if (Params.Count == 0) return;
                else bytes = Encoding.UTF8.GetBytes(new JObject { ["version"] = 1, ["params"] = new JArray(Params.Select(p => p.ToJson())) }.ToString(Formatting.None));
                using (var stream = doc.OpenStream(StreamName, AccessType_e.Write))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Writing v params into '{doc.Title}' failed", ex);
            }
        }

        public void OnClosed()
        {
            lock (Open) Open.Remove(this);
        }
    }

    /// <summary>Created by xCAD for every document SolidWorks loads (hidden assembly components too): reads and writes the v params.</summary>
    internal sealed class VParamDocumentHandler : IDocumentHandler
    {
        private IXDocument _doc;
        private VParamDocument _params;

        public void Init(IXApplication app, IXDocument doc)
        {
            _doc = doc;
            _params = new VParamDocument((ISwDocument)doc);
            doc.StreamReadAvailable += OnStreamRead;
            doc.StreamWriteAvailable += OnStreamWrite;
        }

        // SolidWorks calls these on its own: an exception escaping them would close SolidWorks (see Safe).
        private void OnStreamRead(IXDocument doc) => Safe.Run("Reading the v params from the file", () => _params.ReadFromFile(doc));

        private void OnStreamWrite(IXDocument doc) => Safe.Run("Writing the v params into the file", () => _params.WriteToFile(doc));

        public void Dispose()
        {
            try
            {
                if (_doc != null)
                {
                    _doc.StreamReadAvailable -= OnStreamRead;
                    _doc.StreamWriteAvailable -= OnStreamWrite;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Detaching v param events failed: " + ex.Message);
            }
            _params?.OnClosed();
        }
    }
}
