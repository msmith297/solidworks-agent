using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Unify3D.LocalLink;

namespace SwAgent.LocalMcp
{
    /// <summary>
    /// Unify3D.LocalMcp.exe --client claude-desktop|codex: the stdio MCP server Claude Desktop or Codex runs
    /// (docs/plugins/local-mcp.md section 6). stdout carries only MCP messages, one JSON object per line; stderr is the log
    /// (never message text). Start-up opens no connection, so a client starts it fine while SolidWorks is closed. It exits
    /// when stdin closes.
    /// </summary>
    internal static class Program
    {
        private static readonly object WriteLock = new object();

        private static int Main(string[] args)
        {
            var client = ArgValue(args, "--client");
            if (client == null || !LinkProtocol.Clients.Contains(client))
            {
                Console.Error.WriteLine($"Unify3D.LocalMcp: pass --client {string.Join("|", LinkProtocol.Clients)}.");
                return 2;
            }

            var utf8 = new UTF8Encoding(false);
            var stdin = new StreamReader(Console.OpenStandardInput(), utf8, false);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n", AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { NewLine = "\n", AutoFlush = true };
            void Log(string text)
            {
                lock (WriteLock) stderr.WriteLine($"{DateTime.Now:HH:mm:ss.fff} unify3d: {text}");
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var versionText = $"{version.Major}.{version.Minor}.{version.Build}";
            var pipe = Environment.GetEnvironmentVariable(LinkProtocol.PipeEnv);
            if (string.IsNullOrWhiteSpace(pipe)) pipe = LinkProtocol.DefaultPipeName();
            var link = new LinkClient(pipe, client, versionText);
            var server = new McpServer(client, link, versionText,
                message =>
                {
                    var text = message.ToString(Formatting.None);
                    lock (WriteLock) stdout.WriteLine(text);
                },
                Log);
            Log($"started {versionText} for {LinkProtocol.Label(client)}");

            string line;
            while ((line = stdin.ReadLine()) != null) server.HandleLine(line);
            Log("stdin closed; exiting");
            return 0;
        }

        private static string ArgValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }
            return null;
        }
    }
}
