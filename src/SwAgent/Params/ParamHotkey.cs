using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Voice;

namespace SwAgent.Params
{
    /// <summary>
    /// The v param key (settings "paramsKey", default "vk:80", P): pressed in SolidWorks with faces, edges or components
    /// selected, it opens the parameter table filtered to them. Only while the graphics area or the feature tree has the
    /// keyboard: never while the user types in a field, a dialog or the panel. A key hook of its own (InputHooks), so it
    /// works whether or not voice is on.
    /// </summary>
    internal sealed class ParamHotkey : IDisposable
    {
        private readonly InputHooks _hooks = new InputHooks();
        private readonly Func<JObject> _settings;
        private readonly Control _ui;
        private readonly Action _open;
        private int _vk;

        public ParamHotkey(Func<JObject> settings, Action open)
        {
            _settings = settings;
            _open = open;
            _ui = new Control();
            _ui.CreateControl(); // created on the SolidWorks UI thread: BeginInvoke runs there
            _hooks.Key = OnKey;
            ApplySettings();
        }

        public void ApplySettings()
        {
            var key = (string)_settings()?["paramsKey"] ?? "vk:80";
            _vk = key.StartsWith("vk:") && int.TryParse(key.Substring(3), out var v) ? v : 0; // "off": no key
            _hooks.EnableKeyboard(_vk != 0);
        }

        // On the hook thread: decide at once, post the work.
        private bool OnKey(int vk, bool down, bool injected)
        {
            if (injected || vk != _vk) return false;
            if (!InputHooks.SolidWorksInFront()) return false;
            if (InputHooks.IsDown(0x11) || InputHooks.IsDown(0x12) || InputHooks.IsDown(0x5B) || InputHooks.IsDown(0x5C)) return false; // Ctrl/Alt/Win+P
            if (Typing()) return false;
            if (down)
            {
                try
                {
                    _ui.BeginInvoke((MethodInvoker)(() => Safe.Run("Opening the v params", _open)));
                }
                catch (InvalidOperationException)
                {
                    return false; // shutting down
                }
            }
            return true; // the key's down and up both stay out of SolidWorks
        }

        /// <summary>Whether the keyboard is in a text field, a dialog or the panel (WebView2), where P is a letter.</summary>
        private static bool Typing()
        {
            var fg = GetForegroundWindow();
            var thread = GetWindowThreadProcessId(fg, out _);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (!GetGUIThreadInfo(thread, ref info)) return true;
            if (info.Caret != IntPtr.Zero) return true; // a blinking caret: something takes text
            var focus = info.Focus;
            if (focus == IntPtr.Zero) return true; // the panel: the focus is in WebView2's own process
            var cls = ClassName(focus);
            if (cls.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf("Combo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.StartsWith("Chrome_", StringComparison.Ordinal) || cls.IndexOf("WebView", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // A modal dialog (Options, a message box) is a top-level window of its own class #32770.
            return ClassName(fg) == "#32770";
        }

        private static string ClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        public void Dispose() => _hooks.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Active;
            public IntPtr Focus;
            public IntPtr Capture;
            public IntPtr MenuOwner;
            public IntPtr MoveSize;
            public IntPtr Caret;
            public Rect CaretRect;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    }
}
