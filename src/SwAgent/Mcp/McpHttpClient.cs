using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Mcp
{
    /// <summary>
    /// One remote MCP server over HTTP. Streamable HTTP (MCP 2025-06-18) first: every message is a POST, the answer comes
    /// back as JSON or as an SSE stream, and the server's Mcp-Session-Id goes back on every later request. A server that
    /// refuses the POSTed initialize with 400, 404 or 405 is an older HTTP+SSE server (2024-11-05): a GET opens its event
    /// stream, the "endpoint" event says where to POST, and the answers arrive on that stream. type "sse" goes straight
    /// there.
    ///
    /// Header values (e.g. Authorization: Bearer ${user_config.token}, expanded by the runtime) never reach the log or an
    /// error text, and the URL is only ever shown without its query. OAuth isn't supported: a 401 without a configured
    /// header says "needs OAuth - not supported yet". The server's own requests on a response stream get ping answered
    /// and everything else -32601. There is no standalone GET stream in Streamable mode, so tools/list_changed is only
    /// seen when a response stream carries it. Redirects are followed by hand, only to the same scheme and host (the
    /// handler's own would re-send our headers to any host). What a server sends is capped (MaxAnswerChars per answer or
    /// event), so a broken or hostile server can't fill SolidWorks' memory. Off the UI thread; never touches SolidWorks.
    /// </summary>
    internal sealed class McpHttpClient : IMcpConnection
    {
        private readonly HttpClient _http;
        private readonly Uri _url;
        private readonly Dictionary<string, string> _headers;
        private readonly bool _sseOnly;
        private readonly CancellationTokenSource _life = new CancellationTokenSource();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _calls = new ConcurrentDictionary<long, TaskCompletionSource<JObject>>();
        private long _nextId;
        private int _closed;
        private string _sessionId;
        private string _protocol;
        private bool _legacy;
        private Uri _postUrl;
        private HttpResponseMessage _legacyStream;

        /// <summary>The most one answer, event or line may hold, in characters (16 MB); tests lower it.</summary>
        internal static int MaxAnswerChars = 16 * 1024 * 1024;

        /// <summary>How much of an error status's body is read for its message.</summary>
        private const int MaxErrorChars = 64 * 1024;

        /// <param name="headers">Sent with every request, values already expanded (secrets included).</param>
        /// <param name="sseOnly">The server speaks only the older HTTP+SSE transport (.mcp.json type "sse").</param>
        public McpHttpClient(string name, string url, IDictionary<string, string> headers, bool sseOnly = false)
        {
            Name = name;
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
                throw new McpException($"{name}: the url must start with https:// (or http://).");
            _url = u;
            _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in headers ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null) _headers[kv.Key.Trim()] = kv.Value;
            }
            _sseOnly = sseOnly;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SolidWorks-Agent");
        }

        public string Name { get; }

        public string Instructions { get; private set; }

        public JObject ServerInfo { get; private set; }

        public int? ProcessId => null;

        public string Stderr => null;

        /// <summary>The server turned out to speak the older HTTP+SSE transport.</summary>
        public bool Legacy => _legacy;

        public event Action ToolsChanged;

        public event Action<string> Closed;

        /// <summary>A URL as it may be shown or logged: scheme, host, port and path; never the query or user info.</summary>
        public static string SafeUrl(string url) =>
            Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) ? u.Scheme + "://" + u.Authority + u.AbsolutePath : "(not a url)";

        private string Where => SafeUrl(_url.ToString());

        private JObject InitParams() => new JObject
        {
            ["protocolVersion"] = McpClient.ProtocolVersion,
            ["capabilities"] = new JObject(),
            ["clientInfo"] = new JObject { ["name"] = "solidworks-agent", ["version"] = typeof(McpHttpClient).Assembly.GetName().Version.ToString(3) },
        };

        public async Task StartAsync()
        {
            var timeout = McpClient.InitializeTimeout;
            try
            {
                if (_sseOnly) await OpenLegacyAsync(timeout).ConfigureAwait(false);
                JObject init;
                try
                {
                    init = await RequestAsync("initialize", InitParams(), timeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (HttpStatusException ex) when (!_legacy && (ex.Status == 400 || ex.Status == 404 || ex.Status == 405))
                {
                    Log.Info($"MCP {Name}: {Where} answered {ex.Status} to Streamable HTTP; trying the older HTTP+SSE transport");
                    await OpenLegacyAsync(timeout).ConfigureAwait(false);
                    init = await RequestAsync("initialize", InitParams(), timeout, CancellationToken.None).ConfigureAwait(false);
                }
                _protocol = (string)init["protocolVersion"] ?? McpClient.ProtocolVersion;
                Instructions = (string)init["instructions"];
                ServerInfo = init["serverInfo"] as JObject;
                await NotifyAsync("notifications/initialized", null).ConfigureAwait(false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public async Task<List<JObject>> ListToolsAsync(CancellationToken ct)
        {
            var tools = new List<JObject>();
            string cursor = null;
            for (var page = 0; page < 50; page++)
            {
                var result = await RequestAsync("tools/list", cursor == null ? new JObject() : new JObject { ["cursor"] = cursor }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                tools.AddRange((result["tools"] as JArray ?? new JArray()).OfType<JObject>());
                cursor = (string)result["nextCursor"];
                if (string.IsNullOrEmpty(cursor)) break;
            }
            return tools;
        }

        public Task<JObject> CallToolAsync(string tool, JObject arguments, TimeSpan timeout, CancellationToken ct) =>
            RequestAsync("tools/call", new JObject { ["name"] = tool, ["arguments"] = arguments ?? new JObject() }, timeout, ct);

        private async Task<JObject> RequestAsync(string method, JObject parameters, TimeSpan timeout, CancellationToken ct)
        {
            if (_closed != 0) throw new McpException($"The MCP server '{Name}' isn't connected.");
            var id = Interlocked.Increment(ref _nextId);
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
            JObject response;
            using (var timer = new CancellationTokenSource(timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token, _life.Token))
            {
                try
                {
                    if (_legacy)
                    {
                        // The answer comes on the event stream; the POST itself only says "accepted".
                        var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _calls[id] = tcs;
                        try
                        {
                            await PostAsync(msg, linked.Token).ConfigureAwait(false);
                            using (linked.Token.Register(() => tcs.TrySetCanceled()))
                                response = await tcs.Task.ConfigureAwait(false);
                        }
                        finally
                        {
                            _calls.TryRemove(id, out _);
                        }
                    }
                    else response = await PostForAnswerAsync(msg, id, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (_life.IsCancellationRequested) throw new McpException($"The MCP server '{Name}' was disconnected.");
                    var why = ct.IsCancellationRequested ? "cancelled by the user" : $"timed out after {timeout.TotalSeconds:0} s";
                    if (method != "initialize") _ = NotifyAsync("notifications/cancelled", new JObject { ["requestId"] = id, ["reason"] = why });
                    throw ct.IsCancellationRequested ? (Exception)new OperationCanceledException(why) : new TimeoutException($"{Name}: {method} {why}.");
                }
                catch (HttpRequestException ex)
                {
                    throw new McpException($"{Name}: can't reach {Where} ({Innermost(ex)}).");
                }
            }
            if (response["error"] is JObject error) throw new McpException($"{Name}: {(string)error["message"] ?? "error"} ({(int?)error["code"]})");
            return response["result"] as JObject ?? new JObject();
        }

        /// <summary>Streamable HTTP: POST one request; the answer is the JSON body or an event on the response's SSE stream.</summary>
        private async Task<JObject> PostForAnswerAsync(JObject msg, long id, CancellationToken token)
        {
            using (var res = await SendAsync(HttpMethod.Post, _url, msg, token).ConfigureAwait(false))
            using (token.Register(() => res.Dispose()))
            {
                await CheckAsync(res).ConfigureAwait(false);
                if ((string)msg["method"] == "initialize" && res.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.FirstOrDefault();
                JObject answer = null;
                try
                {
                    if (string.Equals(res.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    {
                        var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        await ReadEventsAsync(stream, (ev, data) =>
                        {
                            var m = ParseMessage(data);
                            if (m == null) return true;
                            if (IsAnswerTo(m, id))
                            {
                                answer = m;
                                return false;
                            }
                            Handle(m);
                            return true;
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        var body = await ReadCappedAsync(res.Content, MaxAnswerChars, true).ConfigureAwait(false);
                        JToken parsed;
                        try
                        {
                            parsed = JToken.Parse(body);
                        }
                        catch (JsonException)
                        {
                            throw new McpException($"{Name}: {Where} didn't answer with JSON.");
                        }
                        foreach (var m in parsed is JArray batch ? batch.OfType<JObject>() : new[] { parsed as JObject }.Where(x => x != null))
                        {
                            if (IsAnswerTo(m, id)) answer = m;
                            else Handle(m);
                        }
                    }
                }
                catch (Exception ex) when ((ex is IOException || ex is ObjectDisposedException) && token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
                return answer ?? throw new McpException($"{Name}: the server ended its answer without a result.");
            }
        }

        /// <summary>POST a message whose answer doesn't come back in the body: a notification, our reply, or any request in legacy mode.</summary>
        private async Task PostAsync(JObject msg, CancellationToken token)
        {
            using (var res = await SendAsync(HttpMethod.Post, _legacy ? _postUrl : _url, msg, token).ConfigureAwait(false))
                await CheckAsync(res).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends one request, following a redirect (with the same method and body, as a 307 asks) only to the same scheme and
        /// host: our headers carry the user's secrets. A redirect elsewhere is an error that names where it pointed.
        /// </summary>
        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, JObject body, CancellationToken token, bool eventStream = false)
        {
            for (var hop = 0; ; hop++)
            {
                HttpResponseMessage res;
                using (var req = NewRequest(method, url, body))
                {
                    if (eventStream)
                    {
                        req.Headers.Accept.Clear();
                        req.Headers.Accept.ParseAdd("text/event-stream");
                    }
                    res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                }
                var status = (int)res.StatusCode;
                if (status < 300 || status > 399 || status == 304) return res;
                var location = res.Headers.Location;
                res.Dispose();
                if (location == null) throw new HttpStatusException(status, $"{Name}: {Where} answered {status} without saying where to go.");
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (next.Scheme != _url.Scheme || next.Authority != _url.Authority)
                    throw new McpException($"{Name}: {Where} redirects to {SafeUrl(next.ToString())}, another host. Not followed (the server's headers would go there too); if that is the right server, put its url in .mcp.json.");
                if (hop >= 4) throw new McpException($"{Name}: {Where} redirects too many times.");
                url = next;
            }
        }

        private async Task NotifyAsync(string method, JObject parameters)
        {
            var msg = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (parameters != null) msg["params"] = parameters;
            await SendQuietlyAsync(msg, method).ConfigureAwait(false);
        }

        private async Task SendQuietlyAsync(JObject msg, string what)
        {
            try
            {
                using (var t = CancellationTokenSource.CreateLinkedTokenSource(_life.Token))
                {
                    t.CancelAfter(TimeSpan.FromSeconds(15));
                    await PostAsync(msg, t.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (!_life.IsCancellationRequested) Log.Warn($"MCP {Name}: {what} not sent ({(ex is HttpRequestException h ? Innermost(h) : ex.Message)})");
            }
        }

        private HttpRequestMessage NewRequest(HttpMethod method, Uri url, JObject body)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            foreach (var kv in _headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            if (_sessionId != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            if (_protocol != null && !_legacy) req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocol);
            if (body != null) req.Content = new StringContent(body.ToString(Formatting.None), new UTF8Encoding(false), "application/json");
            return req;
        }

        /// <summary>Throws on an error status, saying what it means; never repeats a header value.</summary>
        private async Task CheckAsync(HttpResponseMessage res)
        {
            if (res.IsSuccessStatusCode) return;
            var status = (int)res.StatusCode;
            if (status == 401 || status == 403)
            {
                var keyed = _headers.Count > 0;
                throw new HttpStatusException(status, !keyed && status == 401
                    ? $"{Name}: {Where} needs OAuth - not supported yet. If the service also offers an API key, send it in a header (Authorization: Bearer ${{user_config.<key>}})."
                    : $"{Name}: {Where} refused the request ({status} {res.ReasonPhrase}). Check the key or token the user entered.");
            }
            if (status == 404 && _sessionId != null)
                throw new HttpStatusException(status, $"{Name}: the server ended the session (404). Restart the server.");
            string text = null;
            try
            {
                text = (await ReadCappedAsync(res.Content, MaxErrorChars, false).ConfigureAwait(false))?.Trim();
            }
            catch
            {
                // No body.
            }
            throw new HttpStatusException(status, $"{Name}: {Where} answered {status} {res.ReasonPhrase}" + (string.IsNullOrEmpty(text) ? "." : ": " + McpClient.Clip(text.Replace("\r", " ").Replace("\n", " "), 300)));
        }

        // ---- the older HTTP+SSE transport ----

        private async Task OpenLegacyAsync(TimeSpan timeout)
        {
            _legacy = true;
            _sessionId = null;
            var endpoint = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(_life.Token))
            {
                t.CancelAfter(timeout);
                HttpResponseMessage res;
                try
                {
                    res = await SendAsync(HttpMethod.Get, _url, null, t.Token, eventStream: true).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new McpException($"{Name}: can't reach {Where} ({Innermost(ex)}).");
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"{Name}: {Where} didn't open its event stream within {timeout.TotalSeconds:0} s.");
                }
                try
                {
                    await CheckAsync(res).ConfigureAwait(false);
                    if (!string.Equals(res.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                        throw new McpException($"{Name}: {Where} is not an MCP server (neither Streamable HTTP nor an event stream).");
                }
                catch
                {
                    res.Dispose();
                    throw;
                }
                _legacyStream = res;
                var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
                _ = Task.Run(() => LegacyReadLoopAsync(stream, endpoint));
                using (t.Token.Register(() => endpoint.TrySetException(new TimeoutException($"{Name}: {Where} sent no endpoint event within {timeout.TotalSeconds:0} s."))))
                    _postUrl = await endpoint.Task.ConfigureAwait(false);
            }
        }

        private async Task LegacyReadLoopAsync(Stream stream, TaskCompletionSource<Uri> endpoint)
        {
            var why = "the server closed its event stream";
            try
            {
                await ReadEventsAsync(stream, (ev, data) =>
                {
                    if (ev == "endpoint")
                    {
                        // Only the same server may receive our messages (and the headers that go with them).
                        if (Uri.TryCreate(_url, data.Trim(), out var post) && post.Scheme == _url.Scheme && post.Authority == _url.Authority) endpoint.TrySetResult(post);
                        else endpoint.TrySetException(new McpException($"{Name}: the server named a message endpoint on another host; not used."));
                    }
                    else if (ParseMessage(data) is JObject m) Handle(m);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!_life.IsCancellationRequested) why = ex.Message;
            }
            endpoint.TrySetException(new McpException($"{Name}: {why}."));
            Close(why);
        }

        // ---- messages ----

        /// <summary>Reads server-sent events until the stream ends or onEvent returns false. An event over MaxAnswerChars throws.</summary>
        internal static async Task ReadEventsAsync(Stream stream, Func<string, string, bool> onEvent)
        {
            var reader = new LineReader(new StreamReader(stream, new UTF8Encoding(false)));
            string ev = null;
            var data = new StringBuilder();
            string line;
            while ((line = await reader.ReadLineAsync(MaxAnswerChars).ConfigureAwait(false)) != null)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0 && !onEvent(ev ?? "message", data.ToString())) return;
                    ev = null;
                    data.Clear();
                    continue;
                }
                if (line[0] == ':') continue;
                var colon = line.IndexOf(':');
                var field = colon < 0 ? line : line.Substring(0, colon);
                var value = colon < 0 ? "" : line.Substring(colon + 1);
                if (value.StartsWith(" ")) value = value.Substring(1);
                if (field == "event") ev = value;
                else if (field == "data")
                {
                    if (data.Length + value.Length >= MaxAnswerChars) throw TooBig();
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                }
            }
            if (data.Length > 0) onEvent(ev ?? "message", data.ToString());
        }

        private static McpException TooBig() =>
            new McpException($"the server sent a message over {(MaxAnswerChars >= 1 << 20 ? (MaxAnswerChars >> 20) + " MB" : Math.Max(1, MaxAnswerChars >> 10) + " KB")}; it was not read");

        /// <summary>A body as text, up to max characters: over it, an error (tooBigFails) or the text cut there.</summary>
        private static async Task<string> ReadCappedAsync(HttpContent content, int max, bool tooBigFails)
        {
            if (tooBigFails && content.Headers.ContentLength > max) throw TooBig();
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
                return await ReadCappedAsync(stream, max, tooBigFails).ConfigureAwait(false);
        }

        internal static async Task<string> ReadCappedAsync(Stream stream, int max, bool tooBigFails)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var sb = new StringBuilder();
                var buf = new char[8192];
                int n;
                while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    if (sb.Length + n > max)
                    {
                        if (tooBigFails) throw TooBig();
                        sb.Append(buf, 0, max - sb.Length);
                        break;
                    }
                    sb.Append(buf, 0, n);
                }
                return sb.ToString();
            }
        }

        /// <summary>An event stream's lines (ending in \n, \r or \r\n) with a length cap; StreamReader.ReadLineAsync has none.</summary>
        private sealed class LineReader
        {
            private readonly TextReader _reader;
            private readonly char[] _buf = new char[8192];
            private int _pos, _len;
            private bool _skipLf;

            public LineReader(TextReader reader)
            {
                _reader = reader;
            }

            /// <summary>The next line, or null at the end of the stream.</summary>
            public async Task<string> ReadLineAsync(int max)
            {
                StringBuilder sb = null;
                while (true)
                {
                    if (_pos == _len)
                    {
                        _len = await _reader.ReadAsync(_buf, 0, _buf.Length).ConfigureAwait(false);
                        _pos = 0;
                        if (_len == 0) return sb?.ToString();
                    }
                    if (_skipLf)
                    {
                        _skipLf = false;
                        if (_buf[_pos] == '\n')
                        {
                            _pos++;
                            continue;
                        }
                    }
                    var start = _pos;
                    while (_pos < _len && _buf[_pos] != '\n' && _buf[_pos] != '\r') _pos++;
                    sb = sb ?? new StringBuilder();
                    if (sb.Length + (_pos - start) > max) throw TooBig();
                    sb.Append(_buf, start, _pos - start);
                    if (_pos < _len)
                    {
                        _skipLf = _buf[_pos] == '\r';
                        _pos++;
                        return sb.ToString();
                    }
                }
            }
        }

        private JObject ParseMessage(string data)
        {
            try
            {
                return JToken.Parse(data) as JObject;
            }
            catch (JsonException)
            {
                Log.Warn($"MCP {Name}: an event that isn't JSON: {McpClient.Clip(data, 200)}");
                return null;
            }
        }

        private static bool IsAnswerTo(JObject m, long id) =>
            m["method"] == null && m["id"] is JToken t && (t.Type == JTokenType.Integer && (long)t == id || t.Type == JTokenType.String && (string)t == id.ToString());

        /// <summary>Anything that isn't the awaited answer: a legacy answer, a notification, or the server asking us something.</summary>
        private void Handle(JObject msg)
        {
            var method = (string)msg["method"];
            var id = msg["id"];
            if (method == null)
            {
                if (id != null && id.Type == JTokenType.Integer && _calls.TryGetValue((long)id, out var tcs)) tcs.TrySetResult(msg);
                return;
            }
            if (id == null || id.Type == JTokenType.Null)
            {
                if (method == "notifications/tools/list_changed") ToolsChanged?.Invoke();
                else if (method == "notifications/message") Log.Info($"MCP {Name}: {McpClient.Clip(msg["params"]?.ToString(Formatting.None), 500)}");
                return;
            }
            var reply = method == "ping"
                ? new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JObject() }
                : new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not found: " + method } };
            _ = SendQuietlyAsync(reply, "the answer to " + method);
        }

        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message.TrimEnd('.');
        }

        private void Close(string why)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            foreach (var call in _calls.Values) call.TrySetException(new McpException($"The MCP server '{Name}' stopped: {why}."));
            Closed?.Invoke(why);
        }

        /// <summary>Ends the session (DELETE with its id, as the spec asks), then disconnects.</summary>
        public async Task StopAsync(TimeSpan grace)
        {
            if (!_legacy && _sessionId != null && _closed == 0)
            {
                try
                {
                    using (var t = CancellationTokenSource.CreateLinkedTokenSource(_life.Token))
                    {
                        t.CancelAfter(grace);
                        using (await SendAsync(HttpMethod.Delete, _url, null, t.Token).ConfigureAwait(false))
                        {
                        }
                    }
                }
                catch
                {
                    // The server may not allow DELETE (405) or be gone: the session ends with it.
                }
            }
            Dispose();
        }

        public void Dispose()
        {
            try
            {
                _life.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
            _legacyStream?.Dispose();
            Close("stopped");
            _http.Dispose();
        }
    }

    /// <summary>An HTTP error status from an MCP server, with the status code (400/404/405 send us to the SSE fallback).</summary>
    internal sealed class HttpStatusException : McpException
    {
        public HttpStatusException(int status, string message)
            : base(message)
        {
            Status = status;
        }

        public int Status { get; }
    }
}
