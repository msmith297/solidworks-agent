using System;
using System.Drawing;
using System.Windows.Forms;

namespace SwAgent.UI
{
    /// <summary>
    /// The small box that opens when the user double-clicks a tag on the agent's overlay (SceneOverlay): the value and a
    /// note. Enter saves, Esc cancels. It runs as a modal dialog so typed keys reach it, not SolidWorks' shortcuts.
    /// </summary>
    internal sealed class OverlayTagEditor : Form
    {
        private readonly TextBox _value;
        private readonly TextBox _note;
        private readonly Color _accent;

        public string Value => _value.Text;
        public string Note => _note.Text;

        public OverlayTagEditor(string valueLabel, string value, string note, Color accent, float dpi, Point at)
        {
            _accent = accent;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(0x24, 0x27, 0x2B);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);
            int S(int px) => (int)Math.Round(px * dpi);

            int pad = S(8), labelWidth = S(38), boxWidth = S(210), row = S(26);
            _value = Box(value, new Rectangle(pad + labelWidth, pad, boxWidth, row));
            _note = Box(note, new Rectangle(pad + labelWidth, pad + row + S(4), boxWidth, row));
            Controls.Add(Caption(valueLabel, new Rectangle(pad, pad, labelWidth, row), 255));
            Controls.Add(Caption("Note", new Rectangle(pad, pad + row + S(4), labelWidth, row), 255));
            Controls.Add(_value);
            Controls.Add(_note);
            Controls.Add(Caption("Enter saves  ·  Esc cancels", new Rectangle(pad, pad + 2 * row + S(8), labelWidth + boxWidth, S(20)), 170));
            Size = new Size(2 * pad + labelWidth + boxWidth, 2 * pad + 2 * row + S(28));

            var screen = Screen.FromPoint(at).WorkingArea;
            Location = new Point(Math.Max(screen.Left, Math.Min(at.X, screen.Right - Width)), Math.Max(screen.Top, Math.Min(at.Y, screen.Bottom - Height)));
        }

        private static TextBox Box(string text, Rectangle bounds) =>
            new TextBox
            {
                Text = text ?? "",
                Bounds = bounds,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(0x33, 0x37, 0x3D),
                ForeColor = Color.White,
            };

        private static Label Caption(string text, Rectangle bounds, int alpha) =>
            new Label
            {
                Text = text,
                Bounds = bounds,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = alpha == 255 ? Color.White : Color.FromArgb(0xB4, 0xB8, 0xBE),
            };

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            _value.Focus();
            _value.SelectAll();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter)
            {
                DialogResult = DialogResult.OK;
                return true;
            }
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(_accent, 1.4f))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
