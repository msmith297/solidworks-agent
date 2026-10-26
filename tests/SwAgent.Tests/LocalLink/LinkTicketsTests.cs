using System;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Plugins.LocalLink;
using Xunit;

namespace SwAgent.Tests.LocalLink
{
    /// <summary>The ticket store (docs/plugins/local-mcp.md 5.6): states, waits, expiry and the cap.</summary>
    public class LinkTicketsTests
    {
        [Fact]
        public async Task A_wait_ends_when_the_ticket_finishes_or_the_time_is_up()
        {
            var tickets = new LinkTickets();
            var t = tickets.Create("u3d_a", "claude-desktop", LinkTickets.Working);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(LinkTickets.Working, (await tickets.WaitAsync(t, TimeSpan.FromMilliseconds(150))).State);
            Assert.True(watch.ElapsedMilliseconds >= 100);

            var wait = tickets.WaitAsync(t, TimeSpan.FromSeconds(30));
            Assert.True(tickets.Finish(new[] { "u3d_a", "u3d_unknown" }, LinkTickets.Done, "Done.", null));
            Assert.Same(t, await wait);
            var result = tickets.Result(t);
            Assert.Equal("done", (string)result["state"]);
            Assert.Equal("Done.", (string)result["answer"]);
            // Finished is final: later states and answers are ignored.
            tickets.SetState(new[] { "u3d_a" }, LinkTickets.Approval);
            tickets.SetState(new[] { "u3d_a" }, LinkTickets.Queued);
            Assert.False(tickets.Finish(new[] { "u3d_a" }, LinkTickets.Failed, null, "x"));
            Assert.Equal(LinkTickets.Done, t.State);

            // An approval card in the panel: the ticket says so until the card closes.
            var b = tickets.Create("u3d_b", "claude-desktop", LinkTickets.Working);
            tickets.SetState(new[] { "u3d_b" }, LinkTickets.Approval);
            Assert.Equal(LinkTickets.Approval, b.State);
            Assert.StartsWith("Unify3D is waiting for the user to approve", (string)tickets.Result(b)["note"]);
            tickets.SetState(new[] { "u3d_b" }, LinkTickets.Working);
            Assert.Equal(LinkTickets.Working, b.State);
            Assert.False(tickets.Finish(new[] { "u3d_a" }, "working", null, null)); // not a final state
        }

        [Fact]
        public void States_have_plain_notes()
        {
            Assert.Equal("Unify3D is still working on it. Call get_unify3d_answer with ticket \"u3d_1\" to get the answer.", LinkTickets.Note("u3d_1", "working", null, null));
            Assert.Equal("The Unify3D chat is busy (or the panel is still loading). Your message goes next. Call get_unify3d_answer with ticket \"u3d_1\" later.", LinkTickets.Note("u3d_1", "queued", null, null));
            Assert.Equal("Unify3D has paused automatic turns. Your message is in the chat and the agent answers it with the user's next message. Call get_unify3d_answer with ticket \"u3d_1\" later.", LinkTickets.Note("u3d_1", "paused", null, null));
            Assert.Equal("Unify3D is waiting for the user to approve a change (or answer a question) in the Unify3D panel in SolidWorks. Tell the user, then call get_unify3d_answer with ticket \"u3d_1\".",
                LinkTickets.Note("u3d_1", "approval", null, null));
            Assert.Equal("The user stopped the agent before it finished.", LinkTickets.Note("u3d_1", "stopped", "", null));
            Assert.Equal("The agent's turn failed: Overloaded.", LinkTickets.Note("u3d_1", "failed", null, "Overloaded."));
        }

        [Fact]
        public void Finished_tickets_expire_after_an_hour_open_ones_after_a_day_and_200_at_most()
        {
            var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
            var tickets = new LinkTickets { Now = () => now };
            tickets.Create("done", "codex", LinkTickets.Working);
            tickets.Finish(new[] { "done" }, LinkTickets.Done, "x", null);
            tickets.Create("open", "codex", LinkTickets.Queued);
            now = now.AddMinutes(61);
            Assert.Null(tickets.Find("done"));
            Assert.NotNull(tickets.Find("open"));
            now = now.AddHours(24);
            Assert.Null(tickets.Find("open"));

            for (var i = 0; i < 250; i++)
            {
                tickets.Create("t" + i, "codex", LinkTickets.Working);
                if (i == 0) tickets.Finish(new[] { "t0" }, LinkTickets.Done, "", null);
                now = now.AddSeconds(1);
            }
            Assert.Equal(LinkTickets.Max, tickets.Count);
            Assert.Null(tickets.Find("t0"));
            Assert.NotNull(tickets.Find("t249"));
        }

        [Fact]
        public void FailWhere_fails_only_unfinished_matches()
        {
            var tickets = new LinkTickets();
            tickets.Create("a", "codex", LinkTickets.Working);
            tickets.Create("b", "codex", LinkTickets.Paused);
            tickets.Create("c", "codex", LinkTickets.Working);
            tickets.Finish(new[] { "c" }, LinkTickets.Done, "ok", null);
            Assert.Equal(2, tickets.FailWhere(t => true, "Gone."));
            Assert.Equal(new[] { "failed", "failed", "done" }, new[] { "a", "b", "c" }.Select(id => tickets.Find(id).State).ToArray());
            Assert.Equal("Gone.", tickets.Find("a").Error);
        }
    }
}
