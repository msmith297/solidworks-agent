using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SwAgent.Plugins.LocalLink
{
    /// <summary>
    /// The tickets of messages from Claude Desktop and Codex (docs/plugins/local-mcp.md 5.6): in memory only, thread-safe,
    /// at most 200. A ticket follows its wake through the panel (queued, working, approval, paused) until the turn's answer arrives
    /// (done, stopped, failed). Finished tickets live an hour, unfinished ones until SolidWorks closes or a day.
    /// </summary>
    internal sealed class LinkTickets
    {
        public const int Max = 200;
        public const string Queued = "queued", Working = "working", Approval = "approval", Paused = "paused", Done = "done", Stopped = "stopped", Failed = "failed";

        private static readonly TimeSpan FinishedLife = TimeSpan.FromHours(1), OpenLife = TimeSpan.FromHours(24);
        private readonly object _sync = new object();
        private readonly Dictionary<string, Ticket> _tickets = new Dictionary<string, Ticket>(StringComparer.Ordinal);

        /// <summary>The clock (tests move it).</summary>
        internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

        internal sealed class Ticket
        {
            public string Id;
            public string Client;
            public string State;
            public string Answer;
            public string Error;
            public DateTime CreatedAt;
            public DateTime? DoneAt;
            public readonly TaskCompletionSource<bool> Finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool IsFinished => State == Done || State == Stopped || State == Failed;
        }

        public static bool IsFinal(string state) => state == Done || state == Stopped || state == Failed;

        public Ticket Create(string id, string client, string state)
        {
            lock (_sync)
            {
                Prune();
                var t = new Ticket { Id = id, Client = client, State = state, CreatedAt = Now() };
                _tickets[id] = t;
                return t;
            }
        }

        public Ticket Find(string id)
        {
            lock (_sync)
            {
                Prune();
                return id != null && _tickets.TryGetValue(id, out var t) ? t : null;
            }
        }

        /// <summary>
        /// The panel moved unfinished tickets on (queued, working, paused), or its turn waits for the user (approval: an approval
        /// or question card is open). Unknown and finished ones are left.
        /// </summary>
        public void SetState(IEnumerable<string> ids, string state)
        {
            if (state != Queued && state != Working && state != Approval && state != Paused) return;
            lock (_sync)
            {
                foreach (var id in ids ?? Enumerable.Empty<string>())
                {
                    if (id != null && _tickets.TryGetValue(id, out var t) && !t.IsFinished) t.State = state;
                }
            }
        }

        /// <summary>The turn ended: done, stopped or failed, with the answer (or partial reply) and the error. True when any was known.</summary>
        public bool Finish(IEnumerable<string> ids, string state, string answer, string error)
        {
            if (!IsFinal(state)) return false;
            var finished = new List<Ticket>();
            lock (_sync)
            {
                foreach (var id in ids ?? Enumerable.Empty<string>())
                {
                    if (id == null || !_tickets.TryGetValue(id, out var t) || t.IsFinished) continue;
                    t.State = state;
                    t.Answer = answer;
                    t.Error = error;
                    t.DoneAt = Now();
                    finished.Add(t);
                }
            }
            foreach (var t in finished) t.Finished.TrySetResult(true);
            return finished.Count > 0;
        }

        /// <summary>Every ticket in this state fails with the error (the panel was reloaded, SolidWorks is closing).</summary>
        public int FailWhere(Func<Ticket, bool> which, string error)
        {
            List<string> ids;
            lock (_sync)
            {
                ids = _tickets.Values.Where(t => !t.IsFinished && which(t)).Select(t => t.Id).ToList();
            }
            Finish(ids, Failed, null, error);
            return ids.Count;
        }

        /// <summary>Waits until the ticket is finished or the time is up (or cancelled); the ticket as it is then.</summary>
        public async Task<Ticket> WaitAsync(Ticket t, TimeSpan wait, CancellationToken cancel = default(CancellationToken))
        {
            if (!t.IsFinished && wait > TimeSpan.Zero)
            {
                var delay = Task.Delay(wait, cancel);
                await Task.WhenAny(t.Finished.Task, delay).ConfigureAwait(false);
            }
            return t;
        }

        public int Count
        {
            get
            {
                lock (_sync) return _tickets.Count;
            }
        }

        /// <summary>Drops expired tickets, then the oldest (finished first) past the cap. Under _sync.</summary>
        private void Prune()
        {
            var now = Now();
            foreach (var t in _tickets.Values.Where(t => t.IsFinished ? now - (t.DoneAt ?? t.CreatedAt) > FinishedLife : now - t.CreatedAt > OpenLife).ToList())
            {
                _tickets.Remove(t.Id);
            }
            if (_tickets.Count < Max) return;
            foreach (var t in _tickets.Values.OrderBy(t => t.IsFinished ? 0 : 1).ThenBy(t => t.CreatedAt).Take(_tickets.Count - Max + 1).ToList())
            {
                _tickets.Remove(t.Id);
                t.Finished.TrySetResult(false);
            }
        }

        /// <summary>The ask / answer result (5.3): ticket, state, the answer when done, else the note the exe passes on.</summary>
        public JObject Result(Ticket t)
        {
            string state, answer, error;
            lock (_sync)
            {
                state = t.State;
                answer = t.Answer;
                error = t.Error;
            }
            var result = new JObject { ["ticket"] = t.Id, ["state"] = state };
            if (state == Done) result["answer"] = answer ?? "";
            else result["note"] = Note(t.Id, state, answer, error);
            return result;
        }

        /// <summary>The plain text for a ticket that isn't done (5.6), for the client's model.</summary>
        public static string Note(string ticket, string state, string answer, string error)
        {
            switch (state)
            {
                case Working:
                    return $"Unify3D is still working on it. Call get_unify3d_answer with ticket \"{ticket}\" to get the answer.";
                case Approval:
                    return "Unify3D is waiting for the user to approve a change (or answer a question) in the Unify3D panel in SolidWorks. " +
                           $"Tell the user, then call get_unify3d_answer with ticket \"{ticket}\".";
                case Queued:
                    return $"The Unify3D chat is busy (or the panel is still loading). Your message goes next. Call get_unify3d_answer with ticket \"{ticket}\" later.";
                case Paused:
                    return $"Unify3D has paused automatic turns. Your message is in the chat and the agent answers it with the user's next message. Call get_unify3d_answer with ticket \"{ticket}\" later.";
                case Stopped:
                    return "The user stopped the agent before it finished." + (string.IsNullOrWhiteSpace(answer) ? "" : " Partial reply: " + answer);
                default:
                    return $"The agent's turn failed: {(string.IsNullOrWhiteSpace(error) ? "no reason given" : error.TrimEnd('.'))}.";
            }
        }
    }
}
