using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Infrastructure;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// A SolidWorks message box that pops up inside an agent tool call is answered so the call can finish, and the agent
    /// gets its text. Real Win32 message boxes, shown by this test process (the watcher looks at its own process here).
    /// </summary>
    /// <summary>Never alongside other tests: a tool call elsewhere would watch this process's message boxes too.</summary>
    [CollectionDefinition("DialogWatcher", DisableParallelization = true)]
    public class DialogWatcherCollection
    {
    }

    [Collection("DialogWatcher")]
    public class DialogWatcherTests
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

        private const uint MB_OK = 0, MB_OKCANCEL = 1, MB_YESNO = 4, MB_ICONWARNING = 0x30;
        private const int IDOK = 1, IDCANCEL = 2, IDNO = 7;

        /// <summary>Shows a message box on its own thread (as SolidWorks would, inside a call) and returns the button clicked.</summary>
        private static Task<int> Show(string text, uint type)
        {
            var done = new TaskCompletionSource<int>();
            var t = new Thread(() => done.SetResult(MessageBox(IntPtr.Zero, text, "SOLIDWORKS Design", type | MB_ICONWARNING))) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return done.Task;
        }

        [Fact]
        public async Task An_ok_box_is_clicked_and_its_text_reported()
        {
            using (var watch = DialogWatcher.Watch("execute_csharp"))
            {
                var answer = Show("One of the components of this assembly is in rollback state. The assembly is inaccessible now.", MB_OK);
                Assert.Same(answer, await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(10))));
                Assert.Equal(IDOK, answer.Result);
                await Task.Delay(300);
                var note = Assert.Single(watch.Notes);
                Assert.Contains("rollback state", note);
                Assert.Contains("SOLIDWORKS Design", note);
                Assert.Contains("clicked OK", note);
            }
        }

        [Fact]
        public async Task A_question_is_answered_with_the_safe_choice()
        {
            using (var watch = DialogWatcher.Watch("execute_csharp"))
            {
                var cancel = Show("Rebuild errors. Continue?", MB_OKCANCEL);
                Assert.Same(cancel, await Task.WhenAny(cancel, Task.Delay(TimeSpan.FromSeconds(10))));
                Assert.Equal(IDCANCEL, cancel.Result);
                var no = Show("Do you want to save the changes?", MB_YESNO);
                Assert.Same(no, await Task.WhenAny(no, Task.Delay(TimeSpan.FromSeconds(10))));
                Assert.Equal(IDNO, no.Result);
                await Task.Delay(300);
                Assert.Equal(2, watch.Notes.Count);
            }
        }

        [Fact]
        public async Task Nothing_is_touched_outside_a_tool_call_and_a_nested_call_does_not_answer_twice()
        {
            // No watcher: the box stays up.
            var left = Show("A box the user opened.", MB_OK);
            await Task.Delay(1800);
            Assert.False(left.IsCompleted);
            using (var outer = DialogWatcher.Watch("execute_csharp"))
            using (var inner = DialogWatcher.Watch("capture_views"))
            {
                Assert.Same(left, await Task.WhenAny(left, Task.Delay(TimeSpan.FromSeconds(10))));
                await Task.Delay(300);
                Assert.Single(outer.Notes);
                Assert.Empty(inner.Notes);
            }
        }

        [Fact]
        public void Button_choice_prefers_stopping_over_confirming()
        {
            Assert.Equal(0, DialogWatcher.Pick(new[] { "OK" }));
            Assert.Equal(1, DialogWatcher.Pick(new[] { "OK", "Cancel" }));
            Assert.Equal(1, DialogWatcher.Pick(new[] { "Yes", "No" }));
            Assert.Equal(2, DialogWatcher.Pick(new[] { "Yes", "No", "Cancel" }));
            Assert.Equal(-1, DialogWatcher.Pick(new[] { "Rebuild", "Continue" })); // unknown: close the window instead
        }
    }
}
