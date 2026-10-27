using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// Long jobs: the add-in knows which windows the agent opened and which it still uses, and reminds it to save and close
    /// the idle ones before they pile up. Documents the user opened are never the agent's.
    /// </summary>
    [Collection("DocTracker")]
    public class DocTrackerTests : IDisposable
    {
        private DateTime _now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        public DocTrackerTests()
        {
            DocTracker.Reset();
            DocTracker.Now = () => _now;
        }

        public void Dispose()
        {
            DocTracker.Reset();
            DocTracker.Now = () => DateTime.UtcNow;
        }

        private static List<DocTracker.Doc> Docs(string active, params string[] titles) =>
            titles.Select(t => new DocTracker.Doc { Title = t, Path = @"C:\job\" + t, Active = t == active }).ToList();

        [Fact]
        public void Windows_the_agent_opened_are_tracked_and_the_users_are_not()
        {
            var userOpen = Docs("Assy.SLDASM", "Assy.SLDASM", "Notes.SLDPRT");
            var afterOpen = Docs("Rib1.SLDPRT", "Assy.SLDASM", "Notes.SLDPRT", "Rib1.SLDPRT");
            DocTracker.Record(userOpen, afterOpen);
            Assert.NotNull(DocTracker.Get("Rib1.SLDPRT"));
            Assert.Null(DocTracker.Get("Assy.SLDASM"));
            Assert.Null(DocTracker.Get("Notes.SLDPRT"));

            // Closed outside the tool: forgotten.
            DocTracker.Record(afterOpen, userOpen);
            Assert.Null(DocTracker.Get("Rib1.SLDPRT"));
        }

        [Fact]
        public void Idle_windows_pile_up_into_one_reminder_and_the_active_one_never_counts()
        {
            var open = Docs("Assy.SLDASM", "Assy.SLDASM");
            for (var i = 1; i <= 8; i++)
            {
                var next = open.Concat(Docs(null, $"Rib{i}.SLDPRT")).ToList();
                DocTracker.Record(open, next);
                open = next;
            }
            Assert.Null(DocTracker.Nudge(open)); // just opened: in use

            _now = _now.AddMinutes(11);
            // Rib8 is active again: used, so not idle.
            var withActive = open.Select(d => new DocTracker.Doc { Title = d.Title, Path = d.Path, Active = d.Title == "Rib8.SLDPRT" }).ToList();
            DocTracker.Record(withActive, withActive);
            var idle = DocTracker.Idle(withActive).Select(i => i.Title).ToList();
            Assert.Equal(7, idle.Count);
            Assert.DoesNotContain("Rib8.SLDPRT", idle);
            Assert.DoesNotContain("Assy.SLDASM", idle);

            var nudge = DocTracker.Nudge(withActive);
            Assert.NotNull(nudge);
            Assert.Contains("close_documents", nudge);
            Assert.Contains("Rib1.SLDPRT", nudge);
            Assert.Null(DocTracker.Nudge(withActive)); // not again right away

            // Five more pile up: reminded again even within 15 minutes.
            var more = withActive;
            for (var i = 9; i <= 13; i++)
            {
                var next = more.Concat(Docs(null, $"Rib{i}.SLDPRT")).ToList();
                DocTracker.Record(more, next);
                more = next;
            }
            _now = _now.AddMinutes(11);
            Assert.NotNull(DocTracker.Nudge(more));
        }

        [Fact]
        public void A_window_the_agent_changes_counts_as_used()
        {
            var before = Docs(null, "Rib1.SLDPRT");
            DocTracker.Record(new List<DocTracker.Doc>(), before);
            _now = _now.AddMinutes(30);
            var changed = before.Select(d => new DocTracker.Doc { Title = d.Title, Path = d.Path, Dirty = true }).ToList();
            DocTracker.Record(before, changed);
            Assert.Empty(DocTracker.Idle(changed));
        }
    }

    [CollectionDefinition("DocTracker", DisableParallelization = true)]
    public class DocTrackerCollection
    {
    }
}
