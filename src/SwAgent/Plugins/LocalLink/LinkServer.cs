using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using Unify3D.LocalLink;

namespace SwAgent.Plugins.LocalLink
{
    /// <summary>A refusal the exe passes on as it is: error.code (LinkProtocol.Err*) and the plain text for the model.</summary>
    internal sealed class LinkError : Exception
    {
        public LinkError(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>Who is on the other end of a connection (for the log), and the client its hello named.</summary>
    internal sealed class LinkPeer
    {
        public int ProcessId;
        public string Image;
        public string Client;
        public string ClientInfo;
    }

    /// <summary>What the pipe serves (LinkHost). Called on pipe threads: never touches SolidWorks itself.</summary>
    internal interface ILinkHandler
    {
        /// <summary>The first request of a connection; throws LinkError to refuse it (the connection then closes).</summary>
        JObject Hello(JObject p, LinkPeer peer);

        /// <summary>Any later request; throws LinkError for a refusal.</summary>
        Task<JObject> HandleAsync(LinkPeer peer, string method, JObject p, CancellationToken cancel);
    }

    /// <summary>
    /// The local link's named pipe (docs/plugins/local-mcp.md 5.2, 5.3): one per Windows user and session, the current
    /// user only (NETWORK denied, nothing for app containers), served by one SolidWorks at a time (a named mutex; a second
    /// SolidWorks waits on it and takes over when the first closes). Newline-delimited JSON, hello first, one request at
    /// a time per connection, at most 8 connections, 10 s to read a request line, 1 MB per line.
    ///
    /// Its own background thread holds the mutex and accepts connections; nothing here calls SolidWorks.
    /// </summary>
    internal sealed class LinkServer : IDisposable
    {
        public const int MaxConnections = 8;
        private const int BufferSize = 64 * 1024;

        private readonly ILinkHandler _handler;
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(MaxConnections, MaxConnections);
        private readonly HashSet<NamedPipeServerStream> _open = new HashSet<NamedPipeServerStream>();
        private Thread _thread;
        private ManualResetEvent _stop;
        private CancellationTokenSource _cts;
        private volatile bool _serving;

        public LinkServer(ILinkHandler handler, string pipeName = null, string mutexName = null)
        {
            _handler = handler;
            PipeName = pipeName ?? LinkProtocol.DefaultPipeName();
            MutexName = mutexName ?? LinkProtocol.MutexName();
        }

        public string PipeName { get; }

        public string MutexName { get; }

        /// <summary>This SolidWorks holds the mutex and listens.</summary>
        public bool Serving => _serving;

        /// <summary>Time to read one request line (tests shorten it).</summary>
        internal TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Wait before trying again when a pipe instance can't be created.</summary>
        internal TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(30);

        public bool Running
        {
            get
            {
                lock (_sync) return _thread != null;
            }
        }

        /// <summary>Starts the listener thread (once): it waits for the mutex, then serves.</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_thread != null) return;
                _stop = new ManualResetEvent(false);
                _cts = new CancellationTokenSource();
                var stop = _stop;
                var cts = _cts;
                _thread = new Thread(() => Run(stop, cts)) { IsBackground = true, Name = "Unify3D local link" };
                _thread.Start();
            }
        }

        /// <summary>Stops listening, closes open connections and releases the mutex (on the listener thread).</summary>
        public void Stop()
        {
            Thread thread;
            lock (_sync)
            {
                if (_thread == null) return;
                thread = _thread;
                _thread = null;
                _stop.Set();
                _cts.Cancel();
                foreach (var pipe in _open) Quietly(pipe.Dispose);
            }
            if (!thread.Join(TimeSpan.FromSeconds(5))) Log.Warn("The local link's listener didn't stop within 5 s");
        }

        private void Run(ManualResetEvent stop, CancellationTokenSource cts)
        {
            Mutex mutex = null;
            var owned = false;
            try
            {
                mutex = new Mutex(false, MutexName);
                try
                {
                    owned = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    owned = true; // the SolidWorks that served it died: ours now
                }
                if (!owned)
                {
                    Log.Info("Another SolidWorks window serves the Claude/Codex link");
                    try
                    {
                        owned = WaitHandle.WaitAny(new WaitHandle[] { stop, mutex }) == 1;
                    }
                    catch (AbandonedMutexException ex) when (ex.MutexIndex == 1)
                    {
                        owned = true;
                    }
                    if (!owned) return;
                }
                _serving = true;
                Log.Info("Serving the Claude/Codex link");
                Accept(stop, cts.Token);
            }
            catch (Exception ex)
            {
                Log.Warn("The local link stopped: " + ex.Message);
            }
            finally
            {
                _serving = false;
                if (owned) Quietly(mutex.ReleaseMutex);
                mutex?.Dispose();
            }
        }

        private void Accept(ManualResetEvent stop, CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    _slots.Wait(cancel);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                NamedPipeServerStream pipe;
                try
                {
                    pipe = Create(PipeName);
                }
                catch (Exception ex)
                {
                    _slots.Release();
                    Log.Warn("The local link's pipe couldn't be created: " + ex.Message);
                    if (stop.WaitOne(RetryInterval)) return;
                    continue;
                }
                lock (_sync) _open.Add(pipe);
                try
                {
                    pipe.WaitForConnectionAsync(cancel).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Close(pipe);
                    if (cancel.IsCancellationRequested) return;
                    Log.Warn("The local link's pipe failed: " + ex.Message);
                    continue;
                }
                _ = Task.Run(() => ServeAsync(pipe, cancel)).ContinueWith(_ => Close(pipe), TaskScheduler.Default);
            }
        }

        private void Close(NamedPipeServerStream pipe)
        {
            lock (_sync) _open.Remove(pipe);
            Quietly(pipe.Dispose);
            _slots.Release();
        }

        /// <summary>One pipe instance with the link's ACL.</summary>
        internal static NamedPipeServerStream Create(string name) =>
            new NamedPipeServerStream(name, PipeDirection.InOut, MaxConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferSize, BufferSize, Security());

        /// <summary>
        /// Allow the current user (read, write, new instances), deny NETWORK everything, owner the current user, nothing else:
        /// no Everyone and no ALL APPLICATION PACKAGES, so remote, sandboxed and low-integrity clients can't connect.
        /// </summary>
        internal static PipeSecurity Security()
        {
            var user = WindowsIdentity.GetCurrent().User;
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return security;
        }

        // ---------------------------------------------------------------- a connection

        private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancel)
        {
            var peer = Peer(pipe);
            Log.Info($"Local link connection from {peer.Image ?? "an unknown program"} (pid {peer.ProcessId})");
            var reader = new LineReader(pipe, LinkProtocol.MaxLineBytes);
            var greeted = false;
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    var read = reader.ReadLineAsync();
                    if (await Task.WhenAny(read, Task.Delay(ReadTimeout, cancel)).ConfigureAwait(false) != read)
                    {
                        _ = read.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        return; // idle (or stopping): the dispose ends the read
                    }
                    var line = await read.ConfigureAwait(false);
                    if (line == null) return;
                    if (line.Trim().Length == 0) continue;

                    JToken id = null;
                    JObject response;
                    var close = false;
                    try
                    {
                        JObject request;
                        try
                        {
                            request = JObject.Parse(line);
                        }
                        catch (JsonException)
                        {
                            throw new LinkError(LinkProtocol.ErrBadRequest, "The request isn't valid JSON.");
                        }
                        id = request["id"];
                        var method = (string)request["method"];
                        var p = request["params"] as JObject ?? new JObject();
                        JObject result;
                        if (!greeted)
                        {
                            if (method != LinkProtocol.Hello) throw new LinkError(LinkProtocol.ErrBadRequest, "Say hello first.");
                            close = true; // a refused hello ends the connection
                            result = _handler.Hello(p, peer);
                            close = false;
                            greeted = true;
                        }
                        else if (method == LinkProtocol.Hello)
                        {
                            throw new LinkError(LinkProtocol.ErrBadRequest, "This connection already said hello.");
                        }
                        else
                        {
                            result = await _handler.HandleAsync(peer, method, p, cancel).ConfigureAwait(false);
                        }
                        response = new JObject { ["id"] = id, ["result"] = result ?? new JObject() };
                    }
                    catch (LinkError e)
                    {
                        response = Error(id, e.Code, e.Message);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        Log.Warn($"Local link {peer.Client ?? "request"} failed: {ex.GetType().Name}: {ex.Message}");
                        response = Error(id, LinkProtocol.ErrFault, "Unify3D hit an error while answering: " + ex.Message);
                    }
                    await WriteAsync(pipe, response, cancel).ConfigureAwait(false);
                    if (close) return;
                }
            }
            catch (LineTooLongException)
            {
                Log.Warn($"Local link: a request over {LinkProtocol.MaxLineBytes} bytes; connection closed");
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                // The client went away, or the link is stopping.
            }
        }

        private static JObject Error(JToken id, string code, string message) =>
            new JObject { ["id"] = id?.DeepClone(), ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        private static async Task WriteAsync(Stream pipe, JObject message, CancellationToken cancel)
        {
            var bytes = new UTF8Encoding(false).GetBytes(message.ToString(Formatting.None) + "\n");
            await pipe.WriteAsync(bytes, 0, bytes.Length, cancel).ConfigureAwait(false);
            await pipe.FlushAsync(cancel).ConfigureAwait(false);
        }

        private static LinkPeer Peer(NamedPipeServerStream pipe)
        {
            var peer = new LinkPeer();
            try
            {
                if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
                {
                    peer.ProcessId = (int)pid;
                    peer.Image = ImagePath(pid);
                }
            }
            catch
            {
                // Logging only.
            }
            return peer;
        }

        private static string ImagePath(uint pid)
        {
            var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                var size = sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static void Quietly(Action a)
        {
            try
            {
                a();
            }
            catch
            {
                // Best effort while closing.
            }
        }

        public void Dispose() => Stop();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>A line longer than the protocol allows: the connection closes.</summary>
        private sealed class LineTooLongException : Exception
        {
        }

        /// <summary>UTF-8 lines from a stream, without reading past what a line needs to know it is too long.</summary>
        private sealed class LineReader
        {
            private readonly Stream _stream;
            private readonly int _max;
            private readonly byte[] _chunk = new byte[8192];
            private readonly MemoryStream _line = new MemoryStream();
            private int _start, _end;

            public LineReader(Stream stream, int max)
            {
                _stream = stream;
                _max = max;
            }

            /// <summary>The next line without its \n (and \r), or null at the end of the stream.</summary>
            public async Task<string> ReadLineAsync()
            {
                while (true)
                {
                    for (var i = _start; i < _end; i++)
                    {
                        if (_chunk[i] != (byte)'\n') continue;
                        _line.Write(_chunk, _start, i - _start);
                        _start = i + 1;
                        return Take();
                    }
                    _line.Write(_chunk, _start, _end - _start);
                    _start = _end = 0;
                    if (_line.Length > _max) throw new LineTooLongException();
                    var n = await _stream.ReadAsync(_chunk, 0, _chunk.Length).ConfigureAwait(false);
                    if (n <= 0) return _line.Length > 0 ? Take() : null;
                    _end = n;
                }
            }

            private string Take()
            {
                if (_line.Length > _max) throw new LineTooLongException();
                var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
                _line.SetLength(0);
                return text;
            }
        }
    }
}
