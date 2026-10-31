namespace SolidWorksAgent.LocalLink
{
    /// <summary>The add-in <-> SolidWorksAgent.LocalMcp.exe pipe contract (docs/plugins/local-mcp.md section 5).</summary>
    internal static class LinkProtocol
    {
        public const int Version = 1;                       // bump on any incompatible change; both sides check it
        public const string PipeEnv = "SOLIDWORKS_AGENT_LINK_PIPE";   // exe only: a test pipe name
        public const int MaxLineBytes = 1 << 20;            // a longer line closes the connection
        public const string ClaudeDesktop = "claude-desktop", Codex = "codex";
        public static readonly string[] Clients = { ClaudeDesktop, Codex };
        public static string Label(string client) => client == Codex ? "Codex" : client == ClaudeDesktop ? "Claude Desktop" : client;
        public static string Plugin(string client) => client == Codex ? "codex-mcp" : "claude-mcp";

        /// <summary>\\.\pipe\SolidWorksAgent.Link.&lt;user SID&gt;.&lt;session id&gt;: one per user and Windows session.</summary>
        public static string DefaultPipeName() =>
            "SolidWorksAgent.Link." + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value + "." +
            System.Diagnostics.Process.GetCurrentProcess().SessionId;

        /// <summary>Held by the SolidWorks that serves the pipe (Local\ = this session).</summary>
        public static string MutexName() =>
            @"Local\SolidWorksAgent.Link." + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;

        // methods
        public const string Hello = "hello", Status = "status", Ask = "ask", Answer = "answer", Tool = "tool";

        // error codes (error.code); error.message is the plain text the exe passes on
        public const string ErrOff = "off", ErrVersion = "version", ErrLimit = "limit", ErrNoDocument = "no-document",
            ErrUnknownTicket = "unknown-ticket", ErrBadRequest = "bad-request", ErrBusy = "busy", ErrFault = "fault",
            ErrNotAllowed = "not-allowed", ErrPanel = "panel";

        /// <summary>The model tools the pipe may run: read-only, fixed. Pipe name -> add-in tool.</summary>
        public static readonly string[][] ModelTools =
        {
            new[] { "solidworks_model_context", "get_model_context" },
            new[] { "solidworks_feature_tree", "get_feature_tree" },
            new[] { "solidworks_selection", "get_selection" },
            new[] { "solidworks_open_documents", "list_open_documents" },
        };
    }
}
