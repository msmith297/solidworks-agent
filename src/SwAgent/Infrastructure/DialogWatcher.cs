using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// While an agent tool runs, SolidWorks sometimes stops inside an API call to show a message box ("One of the components
    /// of this assembly is in rollback state..."), and the call - and the agent - wait for a click that never comes. This
    /// watches for such boxes from a background thread with plain Win32 calls (never the SolidWorks API), reads their text
    /// and buttons, and clicks the safest button: the only one, else Cancel, else No, else Close. What it saw and clicked
    /// goes back to the agent with the tool's result, so it can deal with the cause.
    ///
    /// Only standard dialogs (#32770) of this process, only while a tool runs, and only after they have been up for a
    /// moment (Grace), so a box that SolidWorks closes by itself is left alone.
    /// </summary>
    internal sealed class DialogWatcher : IDisposable
    {
        /// <summary>The process whose dialogs are handled (tests point it at themselves; it is SolidWorks in the add-in).</summary>
        internal static int ProcessId = Process.GetCurrentProcess().Id;

        /// <summary>How long a box must stay up before it is answered.</summary>
        internal static TimeSpan Grace = TimeSpan.FromMilliseconds(900);

        private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

        /// <summary>Button preference when a box has more than one: stop the operation rather than confirm it.</summary>
        private static readonly string[] Preference = { "cancel", "no", "close", "abort", "ignore", "skip", "ok" };

        private readonly string _tool;
        private readonly Timer _timer;
        private readonly object _sync = new object();
        private readonly Dictionary<IntPtr, DateTime> _seen = new Dictionary<IntPtr, DateTime>();
        private readonly HashSet<IntPtr> _answered = new HashSet<IntPtr>();
        private readonly List<string> _notes = new List<string>();
        private bool _disposed;

        /// <summary>Watchers running: a tool a code cell calls runs inside the cell's call, and only the outer one watches.</summary>
        private static int _depth;

        private DialogWatcher(string tool)
        {
            _tool = tool;
            if (Interlocked.Increment(ref _depth) == 1) _timer = new Timer(_ => Tick(), null, Poll, Poll);
        }

        /// <summary>Starts watching for the length of one tool call.</summary>
        public static DialogWatcher Watch(string tool) => new DialogWatcher(tool);

        /// <summary>What was seen and clicked, one line per box, for the agent.</summary>
        public List<string> Notes
        {
            get
            {
                lock (_sync) return _notes.ToList();
            }
        }

        private void Tick()
        {
            try
            {
                var now = DateTime.UtcNow;
                var open = new List<IntPtr>();
                EnumWindows((hwnd, _) =>
                {
                    if (IsWindowVisible(hwnd) && IsWindowEnabled(hwnd) && ClassOf(hwnd) == "#32770")
                    {
                        GetWindowThreadProcessId(hwnd, out var pid);
                        if (pid == ProcessId) open.Add(hwnd);
                    }
                    return true;
                }, IntPtr.Zero);
                lock (_sync)
                {
                    if (_disposed) return;
                    foreach (var gone in _seen.Keys.Where(h => !open.Contains(h)).ToList()) _seen.Remove(gone);
                    foreach (var hwnd in open)
                    {
                        if (_answered.Contains(hwnd)) continue;
                        if (!_seen.TryGetValue(hwnd, out var first))
                        {
                            _seen[hwnd] = now;
                            continue;
                        }
                        if (now - first < Grace) continue;
                        _answered.Add(hwnd);
                        _notes.Add(Answer(hwnd));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Watching for SolidWorks message boxes failed: " + ex.Message);
            }
        }

        /// <summary>Reads a box and clicks its safest button; returns the line for the agent.</summary>
        private string Answer(IntPtr hwnd)
        {
            var title = TextOf(hwnd);
            var texts = new List<string>();
            var buttons = new List<(IntPtr hwnd, int id, string label)>();
            EnumChildWindows(hwnd, (child, _) =>
            {
                if (!IsWindowVisible(child)) return true;
                var cls = ClassOf(child);
                var text = TextOf(child).Trim();
                if (cls == "Static" && text.Length > 0) texts.Add(text);
                else if (cls == "Button" && text.Length > 0 && IsWindowEnabled(child)) buttons.Add((child, GetDlgCtrlID(child), text.Replace("&", "")));
                return true;
            }, IntPtr.Zero);
            var message = string.Join(" ", texts).Replace("\r", " ").Replace("\n", " ");
            if (message.Length > 600) message = message.Substring(0, 600) + "...";
            string clicked;
            var pick = Pick(buttons.Select(b => b.label).ToList());
            if (pick >= 0)
            {
                var b = buttons[pick];
                PostMessage(hwnd, WM_COMMAND, (IntPtr)(b.id & 0xFFFF), b.hwnd);
                clicked = b.label;
            }
            else
            {
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                clicked = "Close (the window's X)";
            }
            Log.Info($"Dismissed a SolidWorks message box during {_tool}: \"{title}\" -> {clicked}");
            return $"SolidWorks showed a message box during {_tool} (title \"{title}\"): \"{(message.Length > 0 ? message : "(no readable text)")}\". " +
                   $"Unify3D clicked {clicked} so the call could finish. Check whether this changed what the call did, and deal with the cause.";
        }

        /// <summary>The button to click: the only one, else the first match in Preference, else -1 (close the window).</summary>
        internal static int Pick(IList<string> labels)
        {
            if (labels.Count == 1) return 0;
            foreach (var want in Preference)
            {
                var i = labels.ToList().FindIndex(l => l.Trim().Equals(want, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _timer?.Dispose();
            Interlocked.Decrement(ref _depth);
        }

        private static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(64);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TextOf(IntPtr hwnd)
        {
            var len = GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private const int WM_COMMAND = 0x0111, WM_CLOSE = 0x0010;

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
