using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>
    /// Which open documents the agent opened (or created) and when it last worked in each, so it can save and close what
    /// it no longer needs on long jobs instead of piling up dozens of part windows. Compares the open windows before and
    /// after every model tool call (on the UI thread, like the call itself): a window that appeared during a call is the
    /// agent's; the one active after a call, or one a call changed, is in use. Documents the user opened are never the
    /// agent's. Only visible windows count: parts an assembly loaded in the background aren't windows.
    /// </summary>
    internal static class DocTracker
    {
        /// <summary>One open document window.</summary>
        internal sealed class Doc
        {
            public string Title;
            public string Path;
            public bool Dirty;
            public bool Active;
        }

        internal sealed class Info
        {
            public string Title;
            public DateTime Opened;
            public DateTime LastUsed;
        }

        /// <summary>Idle windows of the agent's before it is reminded to close them.</summary>
        internal const int NudgeAt = 6;

        /// <summary>A window not used for this long is idle.</summary>
        internal static TimeSpan IdleAfter = TimeSpan.FromMinutes(10);

        private static readonly TimeSpan NudgeEvery = TimeSpan.FromMinutes(15);

        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Info> Agents = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastNudge = DateTime.MinValue;
        private static int _lastNudgeCount;

        /// <summary>The visible document windows (null when SolidWorks can't be asked).</summary>
        public static List<Doc> Snapshot(ISldWorks sw)
        {
            if (sw == null) return null;
            try
            {
                var active = sw.ActiveDoc as IModelDoc2;
                return ((sw.GetDocuments() as object[]) ?? new object[0]).OfType<IModelDoc2>().Where(d => d.Visible)
                    .Select(d => new Doc { Title = d.GetTitle(), Path = d.GetPathName(), Dirty = d.GetSaveFlag(), Active = ReferenceEquals(d, active) })
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Info("Listing the open documents failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>One model tool call's before and after: new windows are the agent's, the active or changed ones are in use.</summary>
        public static void Record(IReadOnlyCollection<Doc> before, IReadOnlyCollection<Doc> after)
        {
            if (before == null || after == null) return;
            var now = Now();
            var was = before.ToDictionary(d => d.Title, StringComparer.OrdinalIgnoreCase);
            lock (Sync)
            {
                foreach (var d in after)
                {
                    if (!was.TryGetValue(d.Title, out var prior))
                    {
                        Agents[d.Title] = new Info { Title = d.Title, Opened = now, LastUsed = now };
                    }
                    else if ((d.Active || (d.Dirty && !prior.Dirty)) && Agents.TryGetValue(d.Title, out var info))
                    {
                        info.LastUsed = now;
                    }
                }
                var open = new HashSet<string>(after.Select(d => d.Title), StringComparer.OrdinalIgnoreCase);
                foreach (var gone in Agents.Keys.Where(k => !open.Contains(k)).ToList()) Agents.Remove(gone);
            }
        }

        public static Info Get(string title)
        {
            lock (Sync) return title != null && Agents.TryGetValue(title, out var i) ? i : null;
        }

        /// <summary>The agent's windows not used for IdleAfter, except the active one.</summary>
        public static List<Info> Idle(IEnumerable<Doc> open)
        {
            var now = Now();
            lock (Sync)
            {
                return open.Where(d => !d.Active).Select(d => Agents.TryGetValue(d.Title, out var i) ? i : null)
                    .Where(i => i != null && now - i.LastUsed >= IdleAfter).OrderBy(i => i.LastUsed).ToList();
            }
        }

        /// <summary>A reminder to close idle windows, at most every 15 minutes unless 5 more piled up; null when not needed.</summary>
        public static string Nudge(IReadOnlyCollection<Doc> open)
        {
            if (open == null) return null;
            var idle = Idle(open);
            lock (Sync)
            {
                if (idle.Count < NudgeAt) return null;
                if (Now() - _lastNudge < NudgeEvery && idle.Count < _lastNudgeCount + 5) return null;
                _lastNudge = Now();
                _lastNudgeCount = idle.Count;
            }
            var names = string.Join(", ", idle.Take(8).Select(i => i.Title)) + (idle.Count > 8 ? $" and {idle.Count - 8} more" : "");
            return $"Housekeeping: {open.Count} documents are open, and {idle.Count} you opened haven't been used for {(int)IdleAfter.TotalMinutes}+ minutes ({names}). " +
                   "Save and close the ones you're done with: close_documents with idle: true (or name them). Keep the assembly and the parts you're still editing open.";
        }

        /// <summary>Forget everything (tests).</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                Agents.Clear();
                _lastNudge = DateTime.MinValue;
                _lastNudgeCount = 0;
            }
        }
    }
}
