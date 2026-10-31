using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins.Hooks
{
    /// <summary>
    /// A program a plugin runs: a hook command or an MCP server. They run outside SolidWorks and never touch it.
    ///
    /// Every one goes into the add-in's Job object (kill on close), so nothing outlives SolidWorks, not even after a crash;
    /// each also gets a job of its own, so a timeout or a disable kills its whole tree (on net48 Process.Kill ends only
    /// cmd.exe and leaves node and its PowerShell children running). The process is created suspended and only resumed
    /// once it is in both jobs: otherwise cmd.exe can start node before the assignment and node escapes the job. Only its
    /// three pipe ends are inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so parallel hooks never hold each other's
    /// stdin open. Hook commands run through exactly one shell (Git Bash's usr\bin\bash.exe -c, as Claude Code; without Git
    /// for Windows cmd.exe /d /s /c: no AutoRun, quotes kept), so SolidWorks is the hook's grandparent; MCP servers that
    /// resolve to an .exe start directly, so SolidWorks is their parent
    /// (agent-bridge binds its hooks to its MCP peer by those pids). stdout and stderr are read as UTF-8 on their own
    /// threads, so a full pipe never stalls the program.
    /// </summary>
    internal sealed class PluginProcess : IDisposable
    {
        private IntPtr _job;
        private readonly ProcessWait _handle;
        private RegisteredWaitHandle _registration;
        private readonly OutputCapture _stdout;
        private readonly OutputCapture _stderr;
        private readonly TaskCompletionSource<bool> _exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _killed;

        private PluginProcess(int id, IntPtr process, Stream input, StreamReader output, StreamReader error, bool captureStdout, int captureLimit, bool keepTail)
        {
            Id = id;
            _handle = new ProcessWait(process);
            Input = input;
            Output = captureStdout ? null : output;
            if (captureStdout) _stdout = new OutputCapture(output, captureLimit, "stdout", false);
            _stderr = new OutputCapture(error, captureLimit, "stderr", keepTail);
        }

        public int Id { get; }

        /// <summary>The program's stdin.</summary>
        public Stream Input { get; }

        /// <summary>The program's stdout, when the caller reads it itself (the MCP client); else null.</summary>
        public StreamReader Output { get; }

        /// <summary>Completes when the process has exited (not necessarily when its output is drained).</summary>
        public Task Exited => _exited.Task;

        public bool Killed => _killed != 0;

        /// <summary>What it wrote to stdout so far (null when the caller reads stdout itself, as the MCP client does).</summary>
        public string Stdout => _stdout?.Text;

        public string Stderr => _stderr.Text;

        public int? ExitCode
        {
            get
            {
                try
                {
                    return Native.GetExitCodeProcess(_handle.SafeWaitHandle, out var code) && code != StillActive ? (int)code : (int?)null;
                }
                catch
                {
                    return null; // disposed
                }
            }
        }

        private const uint StillActive = 259;

        /// <summary>
        /// A hook command line, run the way Claude Code runs it on Windows: bash.exe -c "&lt;command&gt;" with Git Bash when Git
        /// for Windows is installed (so $VAR, ${VAR:-x}, 2&gt;/dev/null, .sh scripts and single quotes work), else one
        /// cmd.exe /d /s /c "&lt;command&gt;". Either way the shell is the hook's parent and SolidWorks its grandparent.
        /// </summary>
        public static PluginProcess StartShell(string command, IDictionary<string, string> env, string cwd, int captureLimit = 1 << 20) =>
            StartShell(command, env, cwd, HookShell.Bash, captureLimit);

        /// <summary>The same with a given shell: a bash.exe, or null for cmd.exe (tests run both).</summary>
        internal static PluginProcess StartShell(string command, IDictionary<string, string> env, string cwd, string bash, int captureLimit)
        {
            return bash == null
                ? Start(CmdExe, "/d /s /c \"" + command + "\"", env, cwd, true, captureLimit)
                : Start(bash, "-c " + QuoteArg(command, always: true), HookShell.BashEnv(env, bash), cwd, true, captureLimit);
        }

        /// <summary>
        /// A program and its arguments (an MCP server). An .exe starts directly; a .cmd/.bat (npx, ...) needs cmd.exe.
        /// `env` must hold PATH (ChildEnv.Base), which is where the command is looked up. Its stderr keeps the last 64 KB:
        /// a server that runs for days and then dies must report why, not what it said at start-up.
        /// </summary>
        public static PluginProcess StartProgram(string command, IEnumerable<string> args, IDictionary<string, string> env, string cwd, bool captureStdout)
        {
            env.TryGetValue("PATH", out var path);
            var resolved = ChildEnv.Resolve(command, path, cwd) ?? throw new FileNotFoundException($"'{command}' was not found on PATH.");
            var argLine = string.Join(" ", args.Select(a => QuoteArg(a)));
            var ext = Path.GetExtension(resolved).ToLowerInvariant();
            if (ext == ".exe" || ext == ".com") return Start(resolved, argLine, env, cwd, captureStdout, 1 << 16, keepTail: true);
            return Start(CmdExe, "/d /s /c \"" + QuoteArg(resolved) + (argLine.Length > 0 ? " " + argLine : "") + "\"", env, cwd, captureStdout, 1 << 16, keepTail: true);
        }

        private static string CmdExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        private static PluginProcess Start(string file, string arguments, IDictionary<string, string> env, string cwd, bool captureStdout, int captureLimit, bool keepTail = false)
        {
            cwd = Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            SafeFileHandle inRead = null, inWrite = null, outRead = null, outWrite = null, errRead = null, errWrite = null;
            var attributes = IntPtr.Zero;
            var handles = IntPtr.Zero;
            var block = IntPtr.Zero;
            var pi = new Native.PROCESS_INFORMATION();
            try
            {
                // Pipes: the child's ends inheritable, ours not.
                var sa = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(Native.SECURITY_ATTRIBUTES)), bInheritHandle = true };
                if (!Native.CreatePipe(out inRead, out inWrite, ref sa, 0) || !Native.CreatePipe(out outRead, out outWrite, ref sa, 0) ||
                    !Native.CreatePipe(out errRead, out errWrite, ref sa, 0))
                    throw new Win32Exception();
                foreach (var mine in new[] { inWrite, outRead, errRead })
                {
                    if (!Native.SetHandleInformation(mine, Native.HANDLE_FLAG_INHERIT, 0)) throw new Win32Exception();
                }

                // Inherit exactly those three handles.
                var size = IntPtr.Zero;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
                handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(handles, 0, inRead.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, IntPtr.Size, outWrite.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errWrite.DangerousGetHandle());
                if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();

                var si = new Native.STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf(typeof(Native.STARTUPINFOEX));
                si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
                si.StartupInfo.hStdInput = inRead.DangerousGetHandle();
                si.StartupInfo.hStdOutput = outWrite.DangerousGetHandle();
                si.StartupInfo.hStdError = errWrite.DangerousGetHandle();
                si.lpAttributeList = attributes;

                block = EnvironmentBlock(env);
                var commandLine = new StringBuilder(QuoteArg(file) + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments));
                var flags = Native.CREATE_SUSPENDED | Native.CREATE_NO_WINDOW | Native.CREATE_UNICODE_ENVIRONMENT | Native.EXTENDED_STARTUPINFO_PRESENT;
                if (!Native.CreateProcess(file, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, block, cwd, ref si, out pi))
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error());
                    throw new Win32Exception(error.NativeErrorCode, $"Couldn't start {Path.GetFileName(file)}: {error.Message}");
                }
            }
            finally
            {
                // The child has its own copies now (or never started).
                inRead?.Dispose();
                outWrite?.Dispose();
                errWrite?.Dispose();
                if (attributes != IntPtr.Zero)
                {
                    Native.DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes);
                }
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
                if (pi.hProcess == IntPtr.Zero)
                {
                    inWrite?.Dispose();
                    outRead?.Dispose();
                    errRead?.Dispose();
                }
            }

            var job = Jobs.Adopt(pi.hProcess, pi.dwProcessId); // still suspended: nothing it starts can escape the job
            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);

            var utf8 = new UTF8Encoding(false);
            var pp = new PluginProcess(pi.dwProcessId, pi.hProcess,
                new FileStream(inWrite, FileAccess.Write, 4096, false),
                new StreamReader(new FileStream(outRead, FileAccess.Read, 4096, false), utf8),
                new StreamReader(new FileStream(errRead, FileAccess.Read, 4096, false), utf8),
                captureStdout, captureLimit, keepTail) { _job = job };
            pp._registration = ThreadPool.RegisterWaitForSingleObject(pp._handle, (state, timedOut) => pp._exited.TrySetResult(true), null, Timeout.Infinite, true);
            return pp;
        }

        /// <summary>A Unicode environment block: NAME=value\0 ... \0\0, sorted as Windows expects.</summary>
        private static IntPtr EnvironmentBlock(IDictionary<string, string> env)
        {
            var sb = new StringBuilder();
            foreach (var kv in env.Where(e => e.Value != null && e.Key.Length > 0 && e.Key.IndexOf('=') < 0).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            sb.Append('\0');
            return Marshal.StringToHGlobalUni(sb.ToString());
        }

        /// <summary>Writes stdin as UTF-8 without a BOM and closes it (hook.mjs reads to EOF and JSON.parses it).</summary>
        public void WriteInputAndClose(string text)
        {
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(text ?? "");
                Input.Write(bytes, 0, bytes.Length);
                Input.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // The program exited (or was killed) without reading its input.
            }
            finally
            {
                CloseInput();
            }
        }

        public void CloseInput()
        {
            try
            {
                Input.Dispose();
            }
            catch
            {
                // Already closed.
            }
        }

        /// <summary>Waits for the output to be read to the end, at most `max` (a detached grandchild can hold the pipe open).</summary>
        public Task DrainAsync(TimeSpan max)
        {
            var all = Task.WhenAll(new[] { _stdout?.Done, _stderr.Done }.Where(t => t != null));
            return Task.WhenAny(all, Task.Delay(max));
        }

        /// <summary>Ends the process and everything it started.</summary>
        public void Kill()
        {
            if (Interlocked.Exchange(ref _killed, 1) != 0) return;
            if (_job != IntPtr.Zero && Jobs.Terminate(_job)) return;
            if (Exited.IsCompleted) return;
            // Not in a job of its own (assignment failed): kill the tree by pid.
            try
            {
                using (var kill = Process.Start(new ProcessStartInfo("taskkill.exe", $"/T /F /PID {Id}") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    kill?.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Killing plugin process {Id} failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
            if (job != IntPtr.Zero) Jobs.Close(job);
            CloseInput();
            try
            {
                _registration?.Unregister(null);
                _handle.Dispose(); // the process handle
            }
            catch
            {
                // Best effort.
            }
        }

        /// <summary>The process handle as a WaitHandle (for the exit wait and the exit code).</summary>
        private sealed class ProcessWait : WaitHandle
        {
            public ProcessWait(IntPtr process)
            {
                SafeWaitHandle = new SafeWaitHandle(process, true);
            }
        }

        /// <summary>
        /// One argument quoted the way CommandLineToArgvW (and node, and the MSYS runtime of Git Bash) splits it again.
        /// `always` quotes even a plain word (MSYS would expand wildcards in an unquoted one).
        /// </summary>
        internal static string QuoteArg(string arg, bool always = false)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (!always && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            for (var i = 0; i < arg.Length; i++)
            {
                var backslashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    backslashes++;
                    i++;
                }
                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
                else sb.Append('\\', backslashes).Append(arg[i]);
            }
            return sb.Append('"').ToString();
        }

        /// <summary>
        /// Reads one output stream on its own thread, keeping the first `limit` characters and draining the rest, or with
        /// keepTail the last `limit` characters (a long-running server's stderr: the newest lines say why it stopped).
        /// </summary>
        private sealed class OutputCapture
        {
            private readonly StringBuilder _text = new StringBuilder();
            private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public OutputCapture(StreamReader reader, int limit, string name, bool keepTail)
            {
                new Thread(() =>
                {
                    var buffer = new char[4096];
                    try
                    {
                        int n;
                        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            lock (_text)
                            {
                                if (keepTail)
                                {
                                    _text.Append(buffer, 0, n);
                                    if (_text.Length > limit) _text.Remove(0, _text.Length - limit);
                                }
                                else if (_text.Length < limit) _text.Append(buffer, 0, Math.Min(n, limit - _text.Length));
                            }
                        }
                    }
                    catch
                    {
                        // The process was killed or the pipe closed.
                    }
                    _done.TrySetResult(true);
                }, 256 * 1024) { IsBackground = true, Name = "plugin-" + name }.Start();
            }

            public Task Done => _done.Task;

            public string Text
            {
                get
                {
                    lock (_text) return _text.ToString();
                }
            }
        }
    }

    /// <summary>The environment plugin programs start with, and where their commands are found.</summary>
    internal static class ChildEnv
    {
        /// <summary>
        /// This process's environment without secrets or another agent's session markers (a SolidWorks started from a
        /// Claude Code terminal must not hand its session to plugins), and with PATH re-read from the registry: SolidWorks'
        /// PATH is stale after the user installs Node while it runs.
        /// </summary>
        public static Dictionary<string, string> Base()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                var key = (string)e.Key;
                if (Hidden(key)) continue;
                env[key] = (string)e.Value;
            }
            env["PATH"] = FreshPath();
            return env;
        }

        private static bool Hidden(string key) =>
            SecretEnv.IsProviderSecret(key) // ANTHROPIC_*, OPENAI_*: the user's AI provider keys
            || key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CLAUDE_PLUGIN_", StringComparison.OrdinalIgnoreCase)
            || key.Equals("CLAUDE_PROJECT_DIR", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SWAGENT_DATA_DIR", StringComparison.OrdinalIgnoreCase);

        /// <summary>The process PATH plus the machine and user PATH from the registry, without duplicates.</summary>
        public static string FreshPath()
        {
            var parts = new List<string>();
            foreach (var source in new[]
                     {
                         Environment.GetEnvironmentVariable("PATH"),
                         Read(EnvironmentVariableTarget.Machine),
                         Read(EnvironmentVariableTarget.User),
                     })
            {
                foreach (var dir in (source ?? "").Split(';'))
                {
                    var d = Environment.ExpandEnvironmentVariables(dir.Trim());
                    if (d.Length > 0 && !parts.Contains(d, StringComparer.OrdinalIgnoreCase)) parts.Add(d);
                }
            }
            return string.Join(";", parts);
        }

        private static string Read(EnvironmentVariableTarget target)
        {
            try
            {
                return Environment.GetEnvironmentVariable("PATH", target);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The full path of a command as cmd.exe would find it (PATHEXT), or null.</summary>
        public static string Resolve(string command, string path, string cwd)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = command.Trim().Trim('"');
            var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';').Where(e => e.Length > 0).ToList();
            IEnumerable<string> Candidates(string baseName) =>
                Path.HasExtension(baseName) ? new[] { baseName }.Concat(exts.Select(e => baseName + e)) : exts.Select(e => baseName + e);
            try
            {
                if (command.IndexOfAny(new[] { '\\', '/' }) >= 0 || Path.IsPathRooted(command))
                {
                    var full = Path.IsPathRooted(command) ? command : Path.Combine(cwd ?? "", command);
                    return Candidates(full).FirstOrDefault(File.Exists);
                }
                foreach (var dir in (path ?? "").Split(';').Where(d => d.Length > 0))
                {
                    var hit = Candidates(Path.Combine(dir, command)).FirstOrDefault(File.Exists);
                    if (hit != null) return hit;
                }
            }
            catch (ArgumentException)
            {
                // Illegal characters in a PATH entry or the command.
            }
            return null;
        }

        private static readonly Regex Placeholder = new Regex(@"\$\{([^}]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// Replaces every ${NAME} with the variable's value (case-insensitive; unknown or empty names become empty, or the
        /// default of ${NAME:-default}). cmd.exe doesn't expand ${...}, so the runtime does it before anything runs.
        /// </summary>
        public static string Expand(string text, IDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("${", StringComparison.Ordinal) < 0) return text;
            return Placeholder.Replace(text, m =>
            {
                var key = m.Groups[1].Value;
                string fallback = null;
                var dash = key.IndexOf(":-", StringComparison.Ordinal);
                if (dash > 0)
                {
                    fallback = key.Substring(dash + 2);
                    key = key.Substring(0, dash);
                }
                return vars.TryGetValue(key.Trim(), out var v) && !string.IsNullOrEmpty(v) ? v : fallback ?? "";
            });
        }
    }

    /// <summary>
    /// The shell hook commands run in. Claude Code on Windows runs them with Git Bash (bash -c), and plugins are written for
    /// that: $CLAUDE_PLUGIN_ROOT without braces, ${VAR:-x}, 2&gt;/dev/null, .sh scripts, single quotes. So SolidWorks Agent does the
    /// same when Git for Windows is installed, and falls back to cmd.exe (with ${...} expanded beforehand) when it isn't.
    /// usr\bin\bash.exe is started directly - bin\bash.exe is a launcher that would put one more process between the hook
    /// and SolidWorks (agent-bridge finds its MCP peer through the hook's parent and grandparent) - with Git's tool folders
    /// in front of PATH, as Git Bash has them.
    /// </summary>
    internal static class HookShell
    {
        private static readonly Lazy<string> Found = new Lazy<string>(() =>
        {
            var bash = FindGitBash();
            Log.Info(bash == null ? "Plugin hooks run with cmd.exe (Git for Windows not found)" : "Plugin hooks run with " + bash);
            return bash;
        });

        /// <summary>The bash.exe hooks run with, or null for cmd.exe.</summary>
        public static string Bash => Found.Value;

        /// <summary>Git Bash's usr\bin\bash.exe: from CLAUDE_CODE_GIT_BASH_PATH (as Claude Code), git.exe on PATH, or the usual install folders.</summary>
        internal static string FindGitBash()
        {
            var starts = new List<string>();
            var set = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
            if (!string.IsNullOrWhiteSpace(set)) starts.Add(set.Trim().Trim('"'));
            var git = ChildEnv.Resolve("git", ChildEnv.FreshPath(), null);
            if (git != null) starts.Add(git);
            foreach (var pf in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                var dir = Environment.GetEnvironmentVariable(pf);
                if (!string.IsNullOrEmpty(dir)) starts.Add(Path.Combine(dir, "Git", "cmd", "git.exe"));
            }
            starts.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe"));
            foreach (var start in starts)
            {
                try
                {
                    // ...\Git\cmd\git.exe, ...\Git\bin\bash.exe, ...\Git\mingw64\bin\git.exe: the Git root is a few levels up.
                    var dir = Path.GetDirectoryName(Path.GetFullPath(start));
                    for (var i = 0; i < 3 && dir != null; i++, dir = Path.GetDirectoryName(dir))
                    {
                        var bash = Path.Combine(dir, "usr", "bin", "bash.exe");
                        if (File.Exists(bash)) return bash;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
                {
                    // A malformed path: try the next.
                }
            }
            return set != null && File.Exists(set.Trim().Trim('"')) ? Path.GetFullPath(set.Trim().Trim('"')) : null;
        }

        /// <summary>The hook's environment with Git's mingw64\bin and usr\bin in front of PATH (cat, sed, grep... as in Git Bash).</summary>
        public static Dictionary<string, string> BashEnv(IDictionary<string, string> env, string bash)
        {
            var result = new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase);
            var usrBin = Path.GetDirectoryName(bash);
            var root = Path.GetDirectoryName(Path.GetDirectoryName(usrBin) ?? "");
            var front = new[] { root == null ? null : Path.Combine(root, "mingw64", "bin"), usrBin }.Where(d => d != null && Directory.Exists(d)).ToList();
            result.TryGetValue("PATH", out var path);
            result["PATH"] = string.Join(";", front.Concat((path ?? "").Split(';').Where(p => p.Length > 0 && !front.Contains(p, StringComparer.OrdinalIgnoreCase))));
            return result;
        }
    }

    /// <summary>Win32 Job objects: the add-in's (kill on close) and one per process, nested in it.</summary>
    internal static class Jobs
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;

        private static readonly Lazy<IntPtr> AddInJob = new Lazy<IntPtr>(() =>
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
                Log.Warn("Setting up the plugin process job failed: " + new Win32Exception().Message);
            return job; // never closed: closing it is what kills the plugin processes when SolidWorks exits
        });

        /// <summary>Puts a (suspended) process in the add-in job and in a job of its own; returns that job (or zero when that failed).</summary>
        public static IntPtr Adopt(IntPtr process, int pid)
        {
            if (AddInJob.Value != IntPtr.Zero && !AssignProcessToJobObject(AddInJob.Value, process))
                Log.Warn($"Plugin process {pid}: not in the add-in job ({new Win32Exception().Message})");
            var own = CreateJobObject(IntPtr.Zero, null);
            if (own == IntPtr.Zero) return IntPtr.Zero;
            if (AssignProcessToJobObject(own, process)) return own;
            Log.Warn($"Plugin process {pid}: no job of its own ({new Win32Exception().Message}); killed by process tree instead");
            CloseHandle(own);
            return IntPtr.Zero;
        }

        public static bool Terminate(IntPtr job) => TerminateJobObject(job, 1);

        /// <summary>Kills every plugin process (SessionEnd, after the polite shutdown).</summary>
        public static void TerminateAll()
        {
            if (AddInJob.IsValueCreated && AddInJob.Value != IntPtr.Zero) TerminateJobObject(AddInJob.Value, 1);
        }

        public static void Close(IntPtr job) => CloseHandle(job);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>CreateProcess and pipes (PluginProcess).</summary>
    internal static class Native
    {
        public const uint CREATE_SUSPENDED = 0x4;
        public const uint CREATE_NO_WINDOW = 0x08000000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000;
        public const int STARTF_USESTDHANDLES = 0x100;
        public const uint HANDLE_FLAG_INHERIT = 1;
        public const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002;

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION info);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
