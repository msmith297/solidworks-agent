using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unify3D.LocalLink;

namespace SwAgent.LocalMcp
{
    /// <summary>What one pipe call gave: the result, or a plain sentence for the client's model.</summary>
    internal sealed class LinkReply
    {
        public JObject Result;

        /// <summary>The add-in's error.code ("off", "limit", ...), or null for a connection problem on this side.</summary>
        public string ErrorCode;

        /// <summary>Set when the call failed: the text the model gets (the add-in's error.message, or ours).</summary>
        public string Error;

        public static LinkReply Fail(string text, string code = null) => new LinkReply { Error = text, ErrorCode = code };
    }

    /// <summary>
    /// One connection per MCP tool call (docs/plugins/local-mcp.md 5.3): connect to the add-in's pipe (2 s), check that the
    /// current Windows user owns it, send hello, check the answer (5 s), send one request and wait for its answer.
    /// Newline-delimited UTF-8 JSON both ways.
    /// </summary>
    internal sealed class LinkClient
    {
        public const int ConnectTimeoutMs = 2000, HelloTimeoutMs = 5000;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly string _pipe;
        private readonly string _client;
        private readonly string _exeVersion;

        /// <summary>The MCP client's name and version (initialize or _meta), for the add-in's log only.</summary>
        public string ClientInfo;

        public LinkClient(string pipe, string client, string exeVersion)
        {
            _pipe = pipe;
            _client = client;
            _exeVersion = exeVersion;
        }

        /// <param name="replyTimeoutMs">How long the request may take after hello; past that the reply is <paramref name="slowText"/>.</param>
        public async Task<LinkReply> CallAsync(string method, JObject parameters, int replyTimeoutMs, string slowText, CancellationToken cancel)
        {
            using (var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
            using (cancel.Register(() => pipe.Dispose()))
            {
                try
                {
                    // Connect waits for a free instance; a pipe that doesn't exist times out the same way.
                    await Task.Run(() => pipe.Connect(ConnectTimeoutMs), cancel).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return LinkReply.Fail(ToolCatalog.NotRunning);
                }
                catch (UnauthorizedAccessException)
                {
                    return LinkReply.Fail(ToolCatalog.AccessDenied(_client));
                }
                catch (IOException)
                {
                    return LinkReply.Fail(ToolCatalog.NotRunning);
                }
                cancel.ThrowIfCancellationRequested();
                // The pipe name can be guessed, and another Windows user could create it first. The add-in's pipe is owned
                // by the current user (LinkServer.Security), which nobody else can fake: send nothing to any other.
                if (!OwnedByCurrentUser(pipe)) return LinkReply.Fail(ToolCatalog.NotOurPipe);

                var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
                var writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
                try
                {
                    await SendAsync(writer, new JObject
                    {
                        ["id"] = 1,
                        ["method"] = LinkProtocol.Hello,
                        ["params"] = new JObject
                        {
                            ["protocol"] = LinkProtocol.Version,
                            ["client"] = _client,
                            ["exe"] = _exeVersion,
                            ["clientInfo"] = ClientInfo,
                        },
                    }).ConfigureAwait(false);
                    var hello = await ReadAsync(reader, HelloTimeoutMs, cancel).ConfigureAwait(false);
                    if (hello == null) return LinkReply.Fail(ToolCatalog.NoHello);
                    if (hello.Error != null) return hello;

                    var request = new JObject { ["id"] = 2, ["method"] = method };
                    if (parameters != null) request["params"] = parameters;
                    await SendAsync(writer, request).ConfigureAwait(false);
                    return await ReadAsync(reader, replyTimeoutMs, cancel).ConfigureAwait(false) ?? LinkReply.Fail(slowText);
                }
                catch (IOException) when (!cancel.IsCancellationRequested)
                {
                    return LinkReply.Fail(ToolCatalog.LinkLost);
                }
                catch (ObjectDisposedException) when (cancel.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancel);
                }
                catch (IOException) when (cancel.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancel);
                }
            }
        }

        private static bool OwnedByCurrentUser(NamedPipeClientStream pipe)
        {
            try
            {
                var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                return owner != null && owner == WindowsIdentity.GetCurrent().User;
            }
            catch (Exception)
            {
                return false; // can't tell: refuse
            }
        }

        private static Task SendAsync(StreamWriter writer, JObject message) => writer.WriteLineAsync(message.ToString(Formatting.None));

        /// <summary>The next answer line, or null when none came in time. A closed pipe is LinkLost.</summary>
        private static async Task<LinkReply> ReadAsync(StreamReader reader, int timeoutMs, CancellationToken cancel)
        {
            var read = reader.ReadLineAsync();
            var done = await Task.WhenAny(read, Task.Delay(timeoutMs, cancel)).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            if (done != read) return null;
            var line = await read.ConfigureAwait(false);
            if (line == null) return LinkReply.Fail(ToolCatalog.LinkLost);
            JObject o;
            try
            {
                o = JObject.Parse(line);
            }
            catch (JsonException)
            {
                return LinkReply.Fail(ToolCatalog.LinkLost);
            }
            if (o["error"] is JObject e)
                return LinkReply.Fail((string)e["message"] ?? "Unify3D refused the request.", (string)e["code"] ?? LinkProtocol.ErrFault);
            return new LinkReply { Result = o["result"] as JObject ?? new JObject() };
        }
    }
}
