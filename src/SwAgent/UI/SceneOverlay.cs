using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SwAgent.Infrastructure;

namespace SwAgent.UI
{
    /// <summary>
    /// The agent's sketch-on-the-model: annotations anchored to 3D points (lines, arrows, outlines, circles, dimensions,
    /// labels) drawn on a click-through layer over the graphics area, plus temporary bodies shown in the scene itself.
    /// Annotations are re-projected whenever the camera moves (IModelView.Transform maps model meters to view pixels),
    /// so they stay on the geometry while the user rotates, pans and zooms. Nothing is added to the model or the undo
    /// history; everything goes away with Clear (the agent, the panel's button, or the user's next message).
    /// </summary>
    internal sealed class SceneOverlay : Form
    {
        public sealed class Item
        {
            /// <summary>line | arrow | polygon | circle | marker | dimension | label | tris</summary>
            public string Type;
            /// <summary>Model coordinates in meters.</summary>
            public List<double[]> Points = new List<double[]>();
            public string Text;
            public Color Color = DefaultColor;
            public bool Dashed;
            public bool Fill;
            /// <summary>circle: radius (meters) and plane normal; marker: radius in pixels.</summary>
            public double Radius;
            public double[] Normal;
            /// <summary>tris: model-space triangles (meters, 9 floats each) - a face highlight or a translucent box.</summary>
            public float[] Tris;
            /// <summary>tris: fill opacity 0..1.</summary>
            public double Alpha = 0.3;
            /// <summary>tris: the boundary of the triangle set (pairs of points), drawn always - alone while the camera moves.</summary>
            public List<double[]> Outline;
            /// <summary>tris: over the triangle budget - drawn as the outline only.</summary>
            public bool OutlineOnly;
        }

        public static readonly Color DefaultColor = Color.FromArgb(0xFF, 0x8C, 0x1A);

        /// <summary>
        /// Filled triangles across all face items (critique G3): GDI fills each one on every redraw, so beyond this the
        /// remaining items are drawn as outlines only, and while the camera moves every item is (filled again when it stops).
        /// </summary>
        public const int MaxFilledTriangles = 3000;

        /// <summary>
        /// The boundary edges of a triangle set (edges used by one triangle only, vertices matched within 1 micron), as
        /// pairs of points: the outline of a face highlight. Pure (GeometryOverlayTests).
        /// </summary>
        internal static List<double[]> TriangleOutline(float[] tris)
        {
            var count = new Dictionary<(long, long, long, long, long, long), int>();
            var ends = new Dictionary<(long, long, long, long, long, long), (double[] A, double[] B)>();
            (long, long, long) Key(int k) => ((long)Math.Round(tris[k] * 1e6), (long)Math.Round(tris[k + 1] * 1e6), (long)Math.Round(tris[k + 2] * 1e6));
            for (var t = 0; t + 8 < tris.Length; t += 9)
            {
                for (var e = 0; e < 3; e++)
                {
                    int a = t + e * 3, b = t + (e + 1) % 3 * 3;
                    var ka = Key(a);
                    var kb = Key(b);
                    if (ka.Equals(kb)) continue;
                    var key = ka.CompareTo(kb) < 0 ? (ka.Item1, ka.Item2, ka.Item3, kb.Item1, kb.Item2, kb.Item3) : (kb.Item1, kb.Item2, kb.Item3, ka.Item1, ka.Item2, ka.Item3);
                    count[key] = count.TryGetValue(key, out var c) ? c + 1 : 1;
                    if (!ends.ContainsKey(key)) ends[key] = (new double[] { tris[a], tris[a + 1], tris[a + 2] }, new double[] { tris[b], tris[b + 1], tris[b + 2] });
                }
            }
            var result = new List<double[]>();
            foreach (var kv in count)
            {
                if (kv.Value != 1) continue;
                var (a, b) = ends[kv.Key];
                result.Add(a);
                result.Add(b);
            }
            return result;
        }

        /// <summary>The 12 triangles of a box given by its 8 corners (bit 0 = +x, bit 1 = +y, bit 2 = +z, any frame).</summary>
        internal static float[] BoxTriangles(double[][] c)
        {
            int[][] quads = { new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 } };
            var t = new List<float>();
            foreach (var q in quads)
                foreach (var tri in new[] { new[] { q[0], q[1], q[2] }, new[] { q[0], q[2], q[3] } })
                    foreach (var v in tri)
                    {
                        t.Add((float)c[v][0]);
                        t.Add((float)c[v][1]);
                        t.Add((float)c[v][2]);
                    }
            return t.ToArray();
        }

        private static SceneOverlay _current;
        private static readonly List<IBody2> Bodies = new List<IBody2>();
        private static IModelDoc2 _bodiesModel;

        /// <summary>Raised with the number of items and bodies shown (0 when cleared).</summary>
        public static event Action<int> Changed;

        public static int Count => (_current?._items.Count ?? 0) + Bodies.Count;

        // ---------- clearing when the drawing goes stale ----------
        //
        // What the agent draws describes the model as it was. When that model changes - a rebuild, a feature added or
        // deleted, undo or redo, by the agent or the user - the drawing no longer matches (a parting plane left floating
        // after the part was turned), so it goes: the annotation layer on its next timer tick (no SolidWorks call needed),
        // temporary bodies at the next safe moment (ClearIfStale, before the agent's next tool call). Rebuilds that change
        // nothing (compare_design, rebuild checks) run inside QuietRebuild. show_overlay keep: true opts out.

        private static IModelDoc2 _watched;
        private static bool _keep;
        private static bool _stale;
        private static int _quiet;

        /// <summary>The drawing is out of date (the model changed since it was drawn) and will be cleared.</summary>
        public static bool Stale => _stale;

        /// <summary>Run a rebuild that changes nothing (a check) without clearing what's drawn.</summary>
        public static T QuietRebuild<T>(Func<T> rebuild)
        {
            _quiet++;
            try
            {
                return rebuild();
            }
            finally
            {
                _quiet--;
            }
        }

        /// <summary>At a safe moment (not inside a SolidWorks event or command): clear everything if the model changed since it was drawn.</summary>
        public static void ClearIfStale()
        {
            if (_stale) ClearAll();
        }

        private static void Watch(IModelDoc2 model)
        {
            if (ReferenceEquals(_watched, model)) return;
            Unwatch();
            try
            {
                switch (model)
                {
                    case PartDoc part:
                        part.RegenPostNotify2 += OnRegen;
                        part.AddItemNotify += OnItem;
                        part.DeleteItemNotify += OnItem;
                        part.UndoPostNotify += OnModelChanged;
                        part.RedoPostNotify += OnModelChanged;
                        part.DestroyNotify2 += OnDestroy;
                        break;
                    case AssemblyDoc asm:
                        asm.RegenPostNotify2 += OnRegen;
                        asm.AddItemNotify += OnItem;
                        asm.DeleteItemNotify += OnItem;
                        asm.UndoPostNotify += OnModelChanged;
                        asm.RedoPostNotify += OnModelChanged;
                        asm.DestroyNotify2 += OnDestroy;
                        break;
                    default:
                        return;
                }
                _watched = model;
            }
            catch (Exception ex)
            {
                Log.Warn("Overlay: watching the model for changes failed: " + ex.Message);
            }
        }

        private static void Unwatch()
        {
            var model = _watched;
            _watched = null;
            if (model == null) return;
            try
            {
                switch (model)
                {
                    case PartDoc part:
                        part.RegenPostNotify2 -= OnRegen;
                        part.AddItemNotify -= OnItem;
                        part.DeleteItemNotify -= OnItem;
                        part.UndoPostNotify -= OnModelChanged;
                        part.RedoPostNotify -= OnModelChanged;
                        part.DestroyNotify2 -= OnDestroy;
                        break;
                    case AssemblyDoc asm:
                        asm.RegenPostNotify2 -= OnRegen;
                        asm.AddItemNotify -= OnItem;
                        asm.DeleteItemNotify -= OnItem;
                        asm.UndoPostNotify -= OnModelChanged;
                        asm.RedoPostNotify -= OnModelChanged;
                        asm.DestroyNotify2 -= OnDestroy;
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Overlay: detaching from the model failed: " + ex.Message);
            }
        }

        // SolidWorks event handlers: only set a flag (no SolidWorks calls, nothing that can throw).
        private static int OnRegen(object stopFeature) => OnModelChanged();

        private static int OnItem(int entityType, string itemName) => OnModelChanged();

        private static int OnDestroy(int destroyType)
        {
            _stale = true;
            return 0;
        }

        internal static int OnModelChanged()
        {
            if (_quiet == 0) MarkChanged();
            return 0;
        }

        /// <summary>The drawn model changed (the events above; tests call it directly).</summary>
        internal static void MarkChanged()
        {
            if (!_keep) _stale = true;
        }

        /// <summary>For tests: the stale state as a fresh drawing leaves it.</summary>
        internal static void ResetStale(bool keep)
        {
            _stale = false;
            _keep = keep;
        }

        /// <summary>Show annotations on this document's view (replacing the current ones unless append). keep: stays when the model changes.</summary>
        public static void ShowItems(ISldWorks sw, IModelDoc2 model, IEnumerable<Item> items, bool append, bool keep = false)
        {
            ClearIfStale();
            // Appended items keep the set only if they all asked to be kept.
            _keep = append && _current != null ? _keep && keep : keep;
            _stale = false;
            Watch(model);
            if (_current != null && (!ReferenceEquals(_current._model, model) || !append))
            {
                if (!ReferenceEquals(_current._model, model)) ClearAll();
                else _current._items.Clear();
            }
            if (_current == null)
            {
                var view = model.ActiveView as IModelView ?? throw new InvalidOperationException("The document has no view to draw on.");
                var owner = new IntPtr(((IFrame)sw.Frame()).GetHWndx64());
                _current = new SceneOverlay(sw, model, new IntPtr(view.GetViewHWndx64()), owner);
                _current.Start();
            }
            _current._items.AddRange(items);
            BudgetTriangles(_current._items);
            _current.CaptureMesh(); // a safe moment: the agent is drawing, SolidWorks isn't in the middle of a command
            _current._dirty = true;
            _current.Tick();
            Changed?.Invoke(Count);
        }

        /// <summary>Triangle items in order: filled while the running total stays within MaxFilledTriangles, outline only after that.</summary>
        internal static int BudgetTriangles(IEnumerable<Item> items)
        {
            var filled = 0;
            foreach (var it in items)
            {
                if (it.Type != "tris" || it.Tris == null) continue;
                if (it.Outline == null) it.Outline = TriangleOutline(it.Tris);
                var n = it.Tris.Length / 9;
                it.OutlineOnly = filled + n > MaxFilledTriangles;
                if (!it.OutlineOnly) filled += n;
            }
            return filled;
        }

        /// <summary>Show a temporary body in the scene (part documents). It belongs to no part and changes nothing.</summary>
        public static void ShowBody(IModelDoc2 model, IBody2 body, int rgb)
        {
            if (!(model is IPartDoc)) throw new InvalidOperationException("Temporary bodies can only be shown in a part document.");
            ClearIfStale();
            Watch(model);
            if (_bodiesModel != null && !ReferenceEquals(_bodiesModel, model)) HideBodies();
            _bodiesModel = model;
            Bodies.AddRange(PreviewDisplay.Show(model, new[] { (body, rgb, 0.5) }));
            Changed?.Invoke(Count);
        }

        public static void ClearAll()
        {
            CloseLayer();
            HideBodies();
            Unwatch();
            _stale = false;
            _keep = false;
            Changed?.Invoke(0);
        }

        /// <summary>Remove the annotation layer only (no SolidWorks calls, so the layer's own timer may do it).</summary>
        private static void CloseLayer()
        {
            if (_current == null) return;
            var c = _current;
            _current = null;
            c._timer.Stop();
            c.Close();
            c.Dispose();
        }

        private static void HideBodies()
        {
            if (_bodiesModel != null) PreviewDisplay.Hide(_bodiesModel, Bodies);
            Bodies.Clear();
            _bodiesModel = null;
        }

        // ---------- the layer ----------

        private readonly ISldWorks _sw;
        private readonly IModelDoc2 _model;
        private readonly IntPtr _viewHwnd;
        private readonly IntPtr _ownerHwnd;
        private readonly List<Item> _items = new List<Item>();
        private readonly Timer _timer = new Timer { Interval = 30 };
        private Rectangle _screenRect;
        private double[] _lastTransform;
        private bool _dirty;
        private bool _shown;
        /// <summary>The camera moved since the last frame (face fills are skipped), and the last frame was drawn that way.</summary>
        private bool _moving, _movingFrame;
        private DateTime _hiddenSince = DateTime.MaxValue;
        private string _hideReason;

        // Depth: which samples of each item are hidden behind the model (ray casts toward the viewer).
        private readonly Dictionary<Item, bool[]> _hidden = new Dictionary<Item, bool[]>();
        private double[] _depthTransform;
        private DateTime _depthAt = DateTime.MinValue;

        private SceneOverlay(ISldWorks sw, IModelDoc2 model, IntPtr viewHwnd, IntPtr ownerHwnd)
        {
            _sw = sw;
            _model = model;
            _viewHwnd = viewHwnd;
            _ownerHwnd = ownerHwnd;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            _timer.Tick += Safe.Tick("Scene overlay", Tick);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Layered, click-through and never activated: the user keeps working in SolidWorks underneath.
                cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
                cp.Parent = _ownerHwnd;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        private void Start()
        {
            _timer.Start();
        }

        private bool IsOurDocActive()
        {
            var active = _sw.ActiveDoc as IModelDoc2;
            if (active == null) return false;
            if (ReferenceEquals(active, _model)) return true;
            try
            {
                // A different wrapper for the same document (e.g. after a notification re-entered SolidWorks).
                return active.GetTitle() == _model.GetTitle() && active.GetPathName() == _model.GetPathName();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Follow the view: re-project when the camera or the window changed, re-check depth when the camera moved, hide
        /// only when the view has really gone (a pop-up or a notification can make SolidWorks look busy for a moment).
        /// </summary>
        private void Tick()
        {
            if (_stale && ReferenceEquals(_current, this))
            {
                // The model changed under the drawing: take the annotations down now; bodies and the event hookup go at the
                // next safe moment (ClearIfStale).
                CloseLayer();
                Changed?.Invoke(Count);
                return;
            }
            if (_items.Count == 0) return;
            string reason = null;
            IModelView view = null;
            if (IsIconic(_ownerHwnd)) reason = "SolidWorks minimized";
            else if (!IsWindowVisible(_viewHwnd)) reason = "view window hidden";
            else if (!IsOurDocActive()) reason = "another document is active";
            else if ((view = _model.ActiveView as IModelView) == null) reason = "no active view";
            if (reason != null)
            {
                if (_hiddenSince == DateTime.MaxValue) _hiddenSince = DateTime.UtcNow;
                if (_shown && DateTime.UtcNow - _hiddenSince > TimeSpan.FromSeconds(1.2))
                {
                    Hide();
                    _shown = false;
                    if (_hideReason != reason) Log.Info("Scene overlay hidden: " + reason);
                    _hideReason = reason;
                }
                return;
            }
            _hiddenSince = DateTime.MaxValue;
            _hideReason = null;

            if (!GetClientRect(_viewHwnd, out var rc)) return;
            var origin = new Point(0, 0);
            ClientToScreen(_viewHwnd, ref origin);
            var rect = new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
            var t = (double[])((IMathTransform)view.Transform).ArrayData;
            var moved = _lastTransform == null || !t.SequenceEqual(_lastTransform);
            // Depth follows the camera at up to ~10 Hz while it moves, and once more when it stops.
            var depthStale = _depthTransform == null || !t.SequenceEqual(_depthTransform);
            var depthDue = depthStale && (DateTime.UtcNow - _depthAt).TotalMilliseconds > 100;
            if (depthDue) UpdateDepth(t);
            // Face fills only once the camera rests; a frame drawn while it moved (outlines only) is redrawn when it stops.
            var changed = _dirty || depthDue || !_shown || rect != _screenRect || moved || (_movingFrame && !moved);
            _moving = moved && !_dirty && _shown;
            if (!changed)
            {
                KeepOnTop();
                return;
            }
            _screenRect = rect;
            _lastTransform = t;
            _dirty = false;
            if (!_shown)
            {
                Bounds = rect;
                Show();
                _shown = true;
            }
            KeepOnTop();
            Render(t);
        }

        /// <summary>Stay above the SolidWorks window (a pop-up can reorder the owned windows).</summary>
        private void KeepOnTop()
        {
            // Only while SolidWorks is in front - otherwise this would lift the layer above other applications.
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
            SetWindowPos(Handle, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }

        /// <summary>Model meters -> view pixels (x right, y down), from IModelView.Transform: s * (p · R) + T.</summary>
        internal static PointF Project(double[] t, double[] p) =>
            new PointF((float)(t[12] * (p[0] * t[0] + p[1] * t[3] + p[2] * t[6]) + t[9]), (float)(t[12] * (p[0] * t[1] + p[1] * t[4] + p[2] * t[7]) + t[10]));

        // ---------- sampling: every item as 3D points, so each piece can be bright (in front) or faded (behind) ----------

        private static List<double[]> Samples(Item item)
        {
            switch (item.Type)
            {
                case "circle":
                {
                    if (item.Points.Count < 1 || item.Radius <= 0) return new List<double[]>();
                    var c = item.Points[0];
                    var n = Unit(item.Normal ?? new double[] { 0, 0, 1 });
                    var u = Unit(Math.Abs(n[0]) > 0.9 ? Cross(n, new double[] { 0, 1, 0 }) : Cross(n, new double[] { 1, 0, 0 }));
                    var v = Cross(n, u);
                    return Enumerable.Range(0, 72).Select(k =>
                    {
                        var a = k * Math.PI * 2 / 72;
                        double cu = Math.Cos(a) * item.Radius, cv = Math.Sin(a) * item.Radius;
                        return new[] { c[0] + cu * u[0] + cv * v[0], c[1] + cu * u[1] + cv * v[1], c[2] + cu * u[2] + cv * v[2] };
                    }).ToList();
                }
                case "marker":
                case "label":
                    return item.Points.Take(1).ToList();
                case "tris":
                {
                    // One depth sample per triangle (its centroid): a filled triangle is bright or faded as a whole.
                    var r = new List<double[]>();
                    var tr = item.Tris ?? new float[0];
                    for (var k = 0; k + 8 < tr.Length; k += 9)
                        r.Add(new double[] { (tr[k] + tr[k + 3] + tr[k + 6]) / 3.0, (tr[k + 1] + tr[k + 4] + tr[k + 7]) / 3.0, (tr[k + 2] + tr[k + 5] + tr[k + 8]) / 3.0 });
                    return r;
                }
                default:
                {
                    // Paths: a point every ~2 mm (at most 40 per segment), so the depth changes along the path show.
                    var pts = item.Points;
                    var closed = item.Type == "polygon";
                    var result = new List<double[]>();
                    var segments = closed ? pts.Count : pts.Count - 1;
                    for (var i = 0; i < segments; i++)
                    {
                        var a = pts[i];
                        var b = pts[(i + 1) % pts.Count];
                        var len = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
                        var n = Math.Max(1, Math.Min(40, (int)Math.Ceiling(len / 0.002)));
                        for (var k = 0; k < n; k++)
                        {
                            var f = (double)k / n;
                            result.Add(new[] { a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f });
                        }
                    }
                    if (!closed && pts.Count > 0) result.Add(pts[pts.Count - 1]);
                    return result;
                }
            }
        }

        /// <summary>The model's display triangles in meters (9 floats each), read when the agent draws - see CaptureMesh.</summary>
        private float[] _tris;

        /// <summary>+1 or -1: which way the view transform's depth grows toward the viewer.</summary>
        private int _depthSign = 1;

        /// <summary>
        /// Read the model's display triangles. Only ever called from ShowItems - when the agent draws, from a tool call or
        /// its own code - never from the timer: a SolidWorks geometry call fired by a timer can land in the middle of a
        /// command (a PropertyManager, a rebuild) and crash SolidWorks with an error no try/catch can stop. That is exactly
        /// how an earlier version (RayIntersections on the timer) took SolidWorks down during a Smart Rebuild.
        /// </summary>
        private void CaptureMesh()
        {
            try
            {
                using (CrashGuard.Enter("tool", null, "Reading the model for the agent's overlay", null))
                {
                    if (_model.ActiveView is IModelView view)
                    {
                        // Toward the viewer, as the selection tool reports it; its sign against the transform's depth axis.
                        var mu = (IMathUtility)_sw.GetMathUtility();
                        var inv = (MathTransform)view.Orientation3.Inverse();
                        var toward = (double[])((IMathVector)((IMathVector)mu.CreateVector(new double[] { 0, 0, 1 })).MultiplyTransform(inv)).ArrayData;
                        var t = (double[])((IMathTransform)view.Transform).ArrayData;
                        _depthSign = toward[0] * t[2] + toward[1] * t[5] + toward[2] * t[8] >= 0 ? 1 : -1;
                    }
                    var mesh = ModelFiles.LiveModelMesh.For(_sw, _model, null).Mesh;
                    var pos = mesh.Positions;
                    var idx = mesh.Indices;
                    var tris = new float[mesh.TriangleCount * 9];
                    for (var i = 0; i < mesh.TriangleCount * 3; i++)
                    {
                        var v = idx[i];
                        tris[i * 3] = pos[v * 3] / 1000f; // the mesh is in mm
                        tris[i * 3 + 1] = pos[v * 3 + 1] / 1000f;
                        tris[i * 3 + 2] = pos[v * 3 + 2] / 1000f;
                    }
                    _tris = tris;
                }
            }
            catch (Exception ex)
            {
                _tris = null; // the overlay just stays bright
                Log.Warn("Scene overlay: couldn't read the model for the depth test: " + ex.Message);
            }
            _depthTransform = null;
        }

        /// <summary>Which samples the model hides in the current view - pure arithmetic on the cached triangles, safe on the timer.</summary>
        private void UpdateDepth(double[] t)
        {
            _depthAt = DateTime.UtcNow;
            _depthTransform = t;
            _hidden.Clear();
            if (_tris == null || _screenRect.Width < 2 || _screenRect.Height < 2) return;
            foreach (var kv in ComputeHidden(_tris, t, _depthSign, _screenRect.Width, _screenRect.Height, _items.Select(i => (i, Samples(i))).ToList()))
                _hidden[kv.Key] = kv.Value;
        }

        /// <summary>
        /// A quarter-resolution depth image of the triangles in this view (closest surface per pixel); a sample is hidden
        /// when a surface sits more than ~0.8 mm in front of it at its pixel.
        /// </summary>
        internal static Dictionary<Item, bool[]> ComputeHidden(float[] tris, double[] t, int sign, int width, int height, List<(Item Item, List<double[]> Points)> samples)
        {
            const float k = 0.25f;
            int w = Math.Max(1, (int)Math.Ceiling(width * k)), h = Math.Max(1, (int)Math.Ceiling(height * k));
            var depth = new float[w * h];
            for (var i = 0; i < depth.Length; i++) depth[i] = float.NegativeInfinity;
            var s = t[12];
            float Sx(double x, double y, double z) => (float)((s * (x * t[0] + y * t[3] + z * t[6]) + t[9]) * k);
            float Sy(double x, double y, double z) => (float)((s * (x * t[1] + y * t[4] + z * t[7]) + t[10]) * k);
            float Sz(double x, double y, double z) => (float)(sign * (s * (x * t[2] + y * t[5] + z * t[8]) + t[11]));

            for (var i = 0; i + 8 < tris.Length; i += 9)
            {
                float x0 = Sx(tris[i], tris[i + 1], tris[i + 2]), y0 = Sy(tris[i], tris[i + 1], tris[i + 2]), z0 = Sz(tris[i], tris[i + 1], tris[i + 2]);
                float x1 = Sx(tris[i + 3], tris[i + 4], tris[i + 5]), y1 = Sy(tris[i + 3], tris[i + 4], tris[i + 5]), z1 = Sz(tris[i + 3], tris[i + 4], tris[i + 5]);
                float x2 = Sx(tris[i + 6], tris[i + 7], tris[i + 8]), y2 = Sy(tris[i + 6], tris[i + 7], tris[i + 8]), z2 = Sz(tris[i + 6], tris[i + 7], tris[i + 8]);
                var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
                if (Math.Abs(area) < 1e-6f) continue;
                int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)))), maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
                int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)))), maxY = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
                for (var py = minY; py <= maxY; py++)
                    for (var px = minX; px <= maxX; px++)
                    {
                        float cx = px + 0.5f, cy = py + 0.5f;
                        var a = ((x1 - cx) * (y2 - cy) - (x2 - cx) * (y1 - cy)) / area;
                        var b = ((x2 - cx) * (y0 - cy) - (x0 - cx) * (y2 - cy)) / area;
                        var c = 1 - a - b;
                        if (a < -1e-4f || b < -1e-4f || c < -1e-4f) continue;
                        var z = a * z0 + b * z1 + c * z2;
                        var o = py * w + px;
                        if (z > depth[o]) depth[o] = z;
                    }
            }

            var eps = (float)(s * 0.0008); // ~0.8 mm, so points lying on a visible face count as visible
            var result = new Dictionary<Item, bool[]>();
            foreach (var (item, points) in samples)
            {
                var flags = new bool[points.Count];
                for (var j = 0; j < points.Count; j++)
                {
                    var p = points[j];
                    int px = (int)Sx(p[0], p[1], p[2]), py = (int)Sy(p[0], p[1], p[2]);
                    if (px < 0 || py < 0 || px >= w || py >= h) continue;
                    var front = depth[py * w + px];
                    flags[j] = !float.IsNegativeInfinity(front) && front > Sz(p[0], p[1], p[2]) + eps;
                }
                result[item] = flags;
            }
            return result;
        }

        private bool[] HiddenOf(Item item) => _hidden.TryGetValue(item, out var h) ? h : null;

        private void Render(double[] t)
        {
            _movingFrame = _moving;
            if (_screenRect.Width < 2 || _screenRect.Height < 2) return;
            using (var bmp = new Bitmap(_screenRect.Width, _screenRect.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    var dpi = g.DpiX / 96f;
                    // Shapes first, labels on top.
                    foreach (var item in _items.Where(i => i.Type != "label")) DrawShape(g, t, item, dpi);
                    foreach (var item in _items.Where(i => i.Type == "label")) DrawLabel(g, t, item, dpi);
                    DrawBadge(g, dpi);
                }
                Push(bmp);
            }
        }

        /// <summary>
        /// A path drawn in runs: in front of the model bright with a dark halo, behind it faded and dashed - so it reads like
        /// a hidden line instead of floating on top.
        /// </summary>
        private static void Path(Graphics g, Item item, PointF[] pts, bool[] hidden, float w, bool closed)
        {
            if (pts.Length < 2) return;
            var n = pts.Length;
            var segs = closed ? n : n - 1;
            var i = 0;
            while (i < segs)
            {
                // A segment is behind the model when both its ends are.
                bool Behind(int k) => hidden != null && hidden[k % n] && hidden[(k + 1) % n];
                var behind = Behind(i);
                var run = new List<PointF> { pts[i] };
                while (i < segs && Behind(i) == behind)
                {
                    run.Add(pts[(i + 1) % n]);
                    i++;
                }
                var arr = run.ToArray();
                if (behind)
                {
                    using (var faint = new Pen(Color.FromArgb(95, item.Color), Math.Max(1.2f, w * 0.7f)) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round })
                        g.DrawLines(faint, arr);
                }
                else
                {
                    using (var halo = new Pen(Color.FromArgb(120, 0, 0, 0), w + 3) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    using (var pen = new Pen(item.Color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round, DashStyle = item.Dashed ? DashStyle.Dash : DashStyle.Solid })
                    {
                        g.DrawLines(halo, arr);
                        g.DrawLines(pen, arr);
                    }
                }
            }
        }

        private static void ArrowHead(Graphics g, Color color, PointF from, PointF to, float size, bool behind)
        {
            var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
            var p1 = new PointF((float)(to.X - size * Math.Cos(angle - 0.42)), (float)(to.Y - size * Math.Sin(angle - 0.42)));
            var p2 = new PointF((float)(to.X - size * Math.Cos(angle + 0.42)), (float)(to.Y - size * Math.Sin(angle + 0.42)));
            using (var halo = new Pen(Color.FromArgb(behind ? 0 : 120, 0, 0, 0), 2f))
            using (var brush = new SolidBrush(behind ? Color.FromArgb(95, color) : color))
            {
                if (!behind) g.DrawPolygon(halo, new[] { to, p1, p2 });
                g.FillPolygon(brush, new[] { to, p1, p2 });
            }
        }

        private void DrawShape(Graphics g, double[] t, Item item, float dpi)
        {
            var w = 2.5f * dpi;
            if (item.Type == "tris")
            {
                DrawTriangles(g, t, item, dpi);
                return;
            }
            var samples = Samples(item);
            var pts = samples.Select(p => Project(t, p)).ToArray();
            var hidden = HiddenOf(item);
            if (hidden != null && hidden.Length != pts.Length) hidden = null;
            var mostlyHidden = hidden != null && hidden.Count(h => h) * 2 > hidden.Length;
            switch (item.Type)
            {
                case "line":
                    Path(g, item, pts, hidden, w, false);
                    break;
                case "arrow":
                    if (pts.Length < 2) return;
                    Path(g, item, pts, hidden, w, false);
                    ArrowHead(g, item.Color, pts[pts.Length - 2], pts[pts.Length - 1], 14f * dpi, hidden != null && hidden[hidden.Length - 1]);
                    break;
                case "polygon":
                case "circle":
                    if (pts.Length < 3) return;
                    if (item.Fill)
                        using (var fill = new SolidBrush(Color.FromArgb(mostlyHidden ? 30 : 70, item.Color)))
                            g.FillPolygon(fill, pts);
                    Path(g, item, pts, hidden, w, true);
                    break;
                case "marker":
                {
                    // A ring of fixed screen size around a point ("this hole", "here").
                    if (pts.Length < 1) return;
                    var r = (float)(item.Radius > 0 ? item.Radius : 14) * dpi;
                    var p = pts[0];
                    var behind = hidden != null && hidden[0];
                    using (var halo = new Pen(Color.FromArgb(behind ? 0 : 120, 0, 0, 0), w + 3))
                    using (var pen = new Pen(behind ? Color.FromArgb(95, item.Color) : item.Color, w) { DashStyle = behind ? DashStyle.Dash : DashStyle.Solid })
                    {
                        if (!behind) g.DrawEllipse(halo, p.X - r, p.Y - r, 2 * r, 2 * r);
                        g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
                    }
                    break;
                }
                case "dimension":
                {
                    // Arrows both ways between two points, the text in the middle.
                    if (pts.Length < 2) return;
                    Path(g, item, pts, hidden, 1.8f * dpi, false);
                    ArrowHead(g, item.Color, pts[1], pts[0], 11f * dpi, hidden != null && hidden[0]);
                    ArrowHead(g, item.Color, pts[pts.Length - 2], pts[pts.Length - 1], 11f * dpi, hidden != null && hidden[hidden.Length - 1]);
                    if (!string.IsNullOrEmpty(item.Text))
                    {
                        var a = Project(t, item.Points[0]);
                        var b = Project(t, item.Points[1]);
                        DrawTag(g, item.Text, new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2), item.Color, dpi, center: true, faded: mostlyHidden);
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// A translucent face highlight or box: triangles filled at the item's alpha (a third of it where the model hides
        /// them), then the outline. While the camera moves - or past the triangle budget - only the outline is drawn.
        /// </summary>
        private void DrawTriangles(Graphics g, double[] t, Item item, float dpi)
        {
            var tr = item.Tris;
            if (tr == null || tr.Length < 9) return;
            var hidden = HiddenOf(item);
            if (hidden != null && hidden.Length != tr.Length / 9) hidden = null;
            var alpha = Math.Max(0, Math.Min(1, item.Alpha));
            if (!_movingFrame && !item.OutlineOnly && alpha > 0)
            {
                var front = (int)Math.Round(255 * alpha);
                var back = Math.Max(1, front / 3);
                g.SmoothingMode = SmoothingMode.None; // anti-aliased edges would show seams between the triangles
                using (var brushFront = new SolidBrush(Color.FromArgb(front, item.Color)))
                using (var brushBack = new SolidBrush(Color.FromArgb(back, item.Color)))
                {
                    var p = new PointF[3];
                    for (int k = 0, n = 0; k + 8 < tr.Length; k += 9, n++)
                    {
                        p[0] = Project(t, new double[] { tr[k], tr[k + 1], tr[k + 2] });
                        p[1] = Project(t, new double[] { tr[k + 3], tr[k + 4], tr[k + 5] });
                        p[2] = Project(t, new double[] { tr[k + 6], tr[k + 7], tr[k + 8] });
                        g.FillPolygon(hidden != null && hidden[n] ? brushBack : brushFront, p);
                    }
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
            if (item.Outline != null && item.Outline.Count >= 2)
            {
                var mostlyHidden = hidden != null && hidden.Count(h => h) * 2 > hidden.Length;
                using (var pen = new Pen(Color.FromArgb(mostlyHidden ? 110 : 230, item.Color), 1.6f * dpi) { DashStyle = mostlyHidden ? DashStyle.Dash : DashStyle.Solid })
                {
                    for (var k = 0; k + 1 < item.Outline.Count; k += 2)
                        g.DrawLine(pen, Project(t, item.Outline[k]), Project(t, item.Outline[k + 1]));
                }
            }
        }

        /// <summary>A label: a tag offset from its anchor with a leader line, kept inside the view; faded when the anchor is hidden.</summary>
        private void DrawLabel(Graphics g, double[] t, Item item, float dpi)
        {
            if (item.Points.Count < 1 || string.IsNullOrEmpty(item.Text)) return;
            var behind = HiddenOf(item) is bool[] h && h.Length > 0 && h[0];
            var anchor = Project(t, item.Points[0]);
            // Optional second point = where the tag sits; otherwise up and to the right of the anchor.
            var at = item.Points.Count > 1 ? Project(t, item.Points[1]) : new PointF(anchor.X + 46 * dpi, anchor.Y - 40 * dpi);
            using (var halo = new Pen(Color.FromArgb(behind ? 0 : 120, 0, 0, 0), 4f))
            using (var pen = new Pen(behind ? Color.FromArgb(110, item.Color) : item.Color, 1.6f * dpi) { DashStyle = behind ? DashStyle.Dash : DashStyle.Solid })
            {
                if (!behind) g.DrawLine(halo, anchor, at);
                g.DrawLine(pen, anchor, at);
            }
            using (var dot = new SolidBrush(behind ? Color.FromArgb(110, item.Color) : item.Color))
                g.FillEllipse(dot, anchor.X - 3.5f * dpi, anchor.Y - 3.5f * dpi, 7 * dpi, 7 * dpi);
            DrawTag(g, item.Text, at, item.Color, dpi, center: false, faded: behind);
        }

        private void DrawTag(Graphics g, string text, PointF at, Color accent, float dpi, bool center, bool faded = false)
        {
            using (var font = new Font("Segoe UI", 9f, FontStyle.Regular))
            {
                var size = g.MeasureString(text, font, (int)(260 * dpi));
                var pad = 6 * dpi;
                var r = new RectangleF(center ? at.X - size.Width / 2 - pad : at.X, at.Y - size.Height / 2 - pad / 2, size.Width + pad * 2, size.Height + pad);
                // Keep it inside the view.
                r.X = Math.Max(2, Math.Min(r.X, _screenRect.Width - r.Width - 2));
                r.Y = Math.Max(2, Math.Min(r.Y, _screenRect.Height - r.Height - 2));
                using (var bg = new SolidBrush(Color.FromArgb(faded ? 120 : 225, 0x24, 0x27, 0x2B)))
                using (var border = new Pen(faded ? Color.FromArgb(110, accent) : accent, 1.4f * dpi))
                using (var path = RoundRect(r, 4 * dpi))
                using (var ink = new SolidBrush(Color.FromArgb(faded ? 150 : 255, 255, 255, 255)))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                    g.DrawString(text, font, ink, new RectangleF(r.X + pad, r.Y + pad / 2, size.Width + 1, size.Height + 1));
                }
            }
        }

        /// <summary>A small note so the user knows what this is and how it goes away.</summary>
        private void DrawBadge(Graphics g, float dpi)
        {
            const string text = "SolidWorks Agent overlay  ·  clears with your next message";
            using (var font = new Font("Segoe UI", 8f))
            {
                var size = g.MeasureString(text, font);
                var r = new RectangleF(8 * dpi, _screenRect.Height - size.Height - 14 * dpi, size.Width + 14 * dpi, size.Height + 6 * dpi);
                using (var bg = new SolidBrush(Color.FromArgb(170, 0x24, 0x27, 0x2B)))
                using (var path = RoundRect(r, 4 * dpi))
                    g.FillPath(bg, path);
                using (var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    g.DrawString(text, font, brush, r.X + 7 * dpi, r.Y + 3 * dpi);
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static double[] Unit(double[] a)
        {
            var l = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
            return l < 1e-12 ? new double[] { 0, 0, 1 } : new[] { a[0] / l, a[1] / l, a[2] / l };
        }

        private void Push(Bitmap bmp)
        {
            var screenDc = GetDC(IntPtr.Zero);
            var memDc = CreateCompatibleDC(screenDc);
            var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(memDc, hBitmap);
            try
            {
                var size = new NativeSize { cx = bmp.Width, cy = bmp.Height };
                var src = new NativePoint();
                var dst = new NativePoint { x = _screenRect.X, y = _screenRect.Y };
                var blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, UlwAlpha);
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        private const int WsExLayered = 0x80000;
        private const int WsExTransparent = 0x20;
        private const int WsExToolWindow = 0x80;
        private const int WsExNoActivate = 0x8000000;
        private const int UlwAlpha = 2;
        private static readonly IntPtr HwndTop = IntPtr.Zero;
        private const int SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int x, y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int cx, cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
            IntPtr hdcSrc, ref NativePoint pprSrc, int crKey, ref BlendFunction pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);
    }
}
