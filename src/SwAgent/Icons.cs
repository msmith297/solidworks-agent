using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace SwAgent
{
    /// <summary>
    /// Ribbon and task pane icons, drawn in code at 128 px (xCAD scales them to every SolidWorks icon size).
    /// Flat glyphs in the SolidWorks blue so they sit naturally next to the native commands.
    /// </summary>
    public static class Icons
    {
        private static readonly Color Blue = Color.FromArgb(0x1E, 0x6F, 0xD9);
        private static readonly Color Ink = Color.FromArgb(0x3B, 0x42, 0x4C);
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();

        /// <summary>The SolidWorks Agent red cube mark (assets/brand/mark-128.png).</summary>
        public static byte[] Agent
        {
            get
            {
                lock (Cache)
                {
                    if (Cache.TryGetValue(nameof(Agent), out var cached)) return cached;
                    using (var stream = typeof(Icons).Assembly.GetManifestResourceStream("Brand.mark-128.png"))
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        return Cache[nameof(Agent)] = ms.ToArray();
                    }
                }
            }
        }

        // Command icons: brushed steel and graphite - no colour accents (the red belongs to the logo).
        private static readonly Color SteelTop = Color.FromArgb(0xF4, 0xF5, 0xF6);
        private static readonly Color SteelBot = Color.FromArgb(0x9A, 0xA0, 0xA6);
        private static readonly Color GraphiteTop = Color.FromArgb(0x55, 0x5A, 0x60);
        private static readonly Color GraphiteBot = Color.FromArgb(0x1C, 0x1F, 0x22);
        private static readonly Color Edge = Color.FromArgb(0x26, 0x29, 0x2D);

        /// <summary>Fill a shape with a vertical brushed-metal gradient and a dark edge.</summary>
        private static void Metal(Graphics g, GraphicsPath path, bool graphite, float edge = 5)
        {
            var bounds = path.GetBounds();
            using (var brush = new LinearGradientBrush(new RectangleF(bounds.X, bounds.Y - 1, bounds.Width, bounds.Height + 2),
                       graphite ? GraphiteTop : SteelTop, graphite ? GraphiteBot : SteelBot, LinearGradientMode.Vertical))
                g.FillPath(brush, path);
            using (var pen = new Pen(Edge, edge) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
        }

        private static GraphicsPath Poly(params PointF[] points)
        {
            var p = new GraphicsPath();
            p.AddPolygon(points);
            return p;
        }

        public static byte[] Chat => Draw(nameof(Chat), g =>
        {
            using (var bubble = Bubble()) Metal(g, bubble, graphite: true);
            Sparkle(g, SteelTop, 64, 56, 22);
        });

        public static byte[] NewChat => Draw(nameof(NewChat), g =>
        {
            using (var bubble = Bubble()) Metal(g, bubble, graphite: false);
            using (var pen = Pen(GraphiteBot, 12))
            {
                g.DrawLine(pen, 64, 34, 64, 78);
                g.DrawLine(pen, 42, 56, 86, 56);
            }
        });

        public static byte[] Selection => Draw(nameof(Selection), g =>
        {
            using (var pen = Pen(Color.FromArgb(0x86, 0x8D, 0x94), 8)) // mid grey: visible on light and dark ribbons
            {
                pen.DashPattern = new[] { 2.2f, 1.6f };
                g.DrawRectangle(pen, 12, 12, 80, 80);
            }
            using (var arrow = Poly(new PointF(54, 48), new PointF(118, 76), new PointF(90, 86), new PointF(106, 114),
                       new PointF(92, 121), new PointF(77, 93), new PointF(56, 110)))
                Metal(g, arrow, graphite: false, edge: 6);
        });

        public static byte[] Camera => Draw(nameof(Camera), g =>
        {
            using (var hump = Poly(new PointF(40, 38), new PointF(50, 18), new PointF(78, 18), new PointF(88, 38))) Metal(g, hump, graphite: true);
            using (var body = Rounded(new RectangleF(8, 34, 112, 78), 14)) Metal(g, body, graphite: false);
            using (var ring = new GraphicsPath())
            {
                ring.AddEllipse(38, 44, 52, 52);
                Metal(g, ring, graphite: true);
            }
            using (var glass = new LinearGradientBrush(new RectangleF(50, 56, 28, 28), Color.FromArgb(0x8C, 0x93, 0x9A), Color.FromArgb(0x2E, 0x32, 0x36), LinearGradientMode.ForwardDiagonal))
                g.FillEllipse(glass, 50, 56, 28, 28);
            using (var shine = new SolidBrush(Color.FromArgb(200, 255, 255, 255))) g.FillEllipse(shine, 56, 61, 8, 8);
        });

        public static byte[] Settings => Draw(nameof(Settings), g =>
        {
            using (var gear = new GraphicsPath())
            {
                const int teeth = 8;
                var pts = new List<PointF>();
                for (var i = 0; i < teeth * 4; i++)
                {
                    var a = (i - 0.5) * Math.PI * 2 / (teeth * 4);
                    var r = (i % 4 == 0 || i % 4 == 1) ? 58 : 44;
                    pts.Add(new PointF((float)(64 + Math.Cos(a) * r), (float)(64 + Math.Sin(a) * r)));
                }
                gear.AddPolygon(pts.ToArray());
                gear.AddEllipse(46, 46, 36, 36); // alternate fill makes this a hole
                Metal(g, gear, graphite: false);
            }
            using (var hole = new GraphicsPath())
            {
                hole.AddEllipse(46, 46, 36, 36);
                Metal(g, hole, graphite: true, edge: 4);
            }
        });

        /// <summary>Toolbox: tools that have no icon of their own yet.</summary>
        public static byte[] ToolDefault => Draw(nameof(ToolDefault), g => Toolbox(g));

        public static byte[] NewTool => Draw(nameof(NewTool), g =>
        {
            Toolbox(g);
            using (var disc = new GraphicsPath())
            {
                disc.AddEllipse(72, 68, 54, 54);
                Metal(g, disc, graphite: true, edge: 4);
            }
            using (var pen = Pen(SteelTop, 8))
            {
                g.DrawLine(pen, 99, 82, 99, 108);
                g.DrawLine(pen, 86, 95, 112, 95);
            }
        });

        /// <summary>Smart Rebuild: a stack of mold plates with a rebuild arrow wrapping around it.</summary>
        public static byte[] SmartRebuild => Draw(nameof(SmartRebuild), g =>
        {
            var plates = new[] { (y: 26f, graphite: false), (y: 52f, graphite: true), (y: 78f, graphite: false) };
            foreach (var p in plates)
                using (var slab = Rounded(new RectangleF(10, p.y, 78, 22), 5))
                    Metal(g, slab, p.graphite, edge: 4);
            // The rebuild arrow: an arc on the right running from the top plate down round to the bottom one.
            using (var shadow = Pen(Edge, 15)) g.DrawArc(shadow, 52, 18, 66, 92, -80, 175);
            using (var arc = Pen(SteelTop, 9)) g.DrawArc(arc, 52, 18, 66, 92, -80, 175);
            using (var head = Poly(new PointF(62, 110), new PointF(88, 96), new PointF(88, 124)))
                Metal(g, head, graphite: false, edge: 4);
        });

        private static void Toolbox(Graphics g)
        {
            using (var handle = Rounded(new RectangleF(42, 16, 44, 30), 8))
            using (var pen = Pen(Edge, 9)) g.DrawPath(pen, handle);
            using (var body = Rounded(new RectangleF(8, 40, 112, 76), 10)) Metal(g, body, graphite: false);
            using (var band = new LinearGradientBrush(new RectangleF(10, 64, 108, 12), GraphiteTop, GraphiteBot, LinearGradientMode.Vertical))
                g.FillRectangle(band, 10, 64, 108, 12);
            using (var latch = Rounded(new RectangleF(52, 58, 24, 24), 4)) Metal(g, latch, graphite: false, edge: 4);
        }

        private static GraphicsPath Bubble()
        {
            var path = Rounded(new RectangleF(10, 12, 108, 86), 22);
            path.AddPolygon(new[] { new PointF(30, 92), new PointF(26, 120), new PointF(58, 96) });
            path.FillMode = FillMode.Winding;
            return path;
        }

        private static void ChatBubble(Graphics g, Color color, bool fill)
        {
            using (var path = Rounded(new RectangleF(10, 12, 108, 86), 22))
            {
                path.AddPolygon(new[] { new PointF(30, 92), new PointF(26, 120), new PointF(58, 96) });
                if (fill)
                {
                    using (var b = new SolidBrush(color)) g.FillPath(b, path);
                }
                else
                {
                    using (var pen = Pen(color, 9)) g.DrawPath(pen, Rounded(new RectangleF(12, 14, 104, 82), 20));
                    using (var pen = Pen(color, 9))
                    {
                        g.DrawLine(pen, 34, 95, 28, 118);
                        g.DrawLine(pen, 28, 118, 58, 96);
                    }
                }
            }
        }

        /// <summary>Four-pointed star used as the "AI" accent.</summary>
        private static void Sparkle(Graphics g, Color color, float cx, float cy, float r)
        {
            var k = r * 0.28f;
            using (var b = new SolidBrush(color))
            {
                g.FillPolygon(b, new[]
                {
                    new PointF(cx, cy - r), new PointF(cx + k, cy - k), new PointF(cx + r, cy), new PointF(cx + k, cy + k),
                    new PointF(cx, cy + r), new PointF(cx - k, cy + k), new PointF(cx - r, cy), new PointF(cx - k, cy - k),
                });
            }
        }

        private static Pen Pen(Color c, float w) => new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        private static GraphicsPath Rounded(RectangleF r, float radius)
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

        private static byte[] Draw(string key, Action<Graphics> paint)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                using (var bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        paint(g);
                    }
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        return Cache[key] = ms.ToArray();
                    }
                }
            }
        }
    }
}
