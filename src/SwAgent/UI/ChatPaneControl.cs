using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SwAgent.Infrastructure;

namespace SwAgent.UI
{
    /// <summary>
    /// The Task Pane content: a WebView2 (Chromium) hosting the React chat UI.
    /// The browser runs in its own processes, so the UI and the agent's network loop can't crash SolidWorks.
    /// Set SWAGENT_DEV_URL=http://localhost:5173 to load the Vite dev server with hot reload.
    /// </summary>
    [System.ComponentModel.ToolboxItem(false)]
    // xCAD takes the Task Pane tab's tooltip and icon from these attributes (TaskPaneSpec is only a fallback).
    [System.ComponentModel.DisplayName("SolidWorks Agent")]
    [Xarial.XCad.Base.Attributes.Icon(typeof(Icons), nameof(Icons.Agent))]
    public class ChatPaneControl : UserControl
    {
        private const string VirtualHost = "swagent.app";

        private readonly WebView2 _web;
        private readonly Label _status;

        public ChatPaneControl()
        {
            BackColor = Color.FromArgb(0xF3, 0xF4, 0xF6);
            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(0x55, 0x5B, 0x66),
                Text = "Starting Agent...",
            };
            _web = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = BackColor };
            Controls.Add(_web);
            Controls.Add(_status);
        }

        internal WebBridge Bridge { get; private set; }

        /// <summary>Paint the panel in the SolidWorks background colour so there's no flash before the UI loads.</summary>
        internal void SetBackground(string hex)
        {
            try
            {
                var c = ColorTranslator.FromHtml(hex);
                BackColor = c;
                _status.BackColor = c;
                _status.ForeColor = c.GetBrightness() < 0.5f ? Color.FromArgb(0xC6, 0xC6, 0xC6) : Color.FromArgb(0x47, 0x47, 0x47);
                _web.DefaultBackgroundColor = c;
            }
            catch (Exception ex)
            {
                Log.Warn("SetBackground failed: " + ex.Message);
            }
        }

        internal event Action BridgeReady;

        internal async void Initialize()
        {
            try
            {
                // Make sure awaits inside RPC handlers resume on the SolidWorks UI thread.
                if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                }

                Bridge = new WebBridge(this);
                var loaderDir = Path.Combine(Paths.AddInDir, "runtimes", "win-x64", "native");
                if (Directory.Exists(loaderDir)) CoreWebView2Environment.SetLoaderDllFolderPath(loaderDir);
                var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewDataDir);
                await _web.EnsureCoreWebView2Async(env);
                var core = _web.CoreWebView2;

                var devUrl = Environment.GetEnvironmentVariable("SWAGENT_DEV_URL");
                var isDev = !string.IsNullOrWhiteSpace(devUrl);
                core.Settings.AreDevToolsEnabled = true; // F12 / right-click > Inspect while we're building this
                core.Settings.AreDefaultContextMenusEnabled = isDev;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = isDev;

                core.SetVirtualHostNameToFolderMapping(VirtualHost, Paths.WebRoot, CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += OnNavigationStarting;
                core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    OpenExternal(e.Uri);
                };
                core.ProcessFailed += (s, e) => Log.Error($"WebView2 process failed: {e.ProcessFailedKind} ({e.Reason})");

                Bridge.Attach(core);
                BridgeReady?.Invoke();

                if (isDev)
                {
                    core.Navigate(devUrl);
                }
                else if (File.Exists(Path.Combine(Paths.WebRoot, "index.html")))
                {
                    core.Navigate($"https://{VirtualHost}/index.html");
                }
                else
                {
                    ShowError($"UI files not found in {Paths.WebRoot}. Build the UI (npm run build in /ui) and rebuild the add-in.");
                    return;
                }

                _web.Visible = true;
                _status.Visible = false;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowError("The Microsoft Edge WebView2 Runtime is not installed. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            }
            catch (Exception ex)
            {
                Log.Error("WebView2 initialisation failed", ex);
                ShowError("The Agent panel failed to start: " + ex.Message);
            }
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            var uri = new Uri(e.Uri);
            var devUrl = Environment.GetEnvironmentVariable("SWAGENT_DEV_URL");
            var allowed = uri.Host == VirtualHost ||
                          (!string.IsNullOrWhiteSpace(devUrl) && uri.GetLeftPart(UriPartial.Authority) == new Uri(devUrl).GetLeftPart(UriPartial.Authority));
            if (allowed) return;

            // Links in chat messages open in the user's browser, never inside the panel.
            e.Cancel = true;
            OpenExternal(e.Uri);
        }

        internal static void OpenExternal(string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            {
                Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
            }
        }

        private void ShowError(string message)
        {
            _status.Text = message;
            _status.Visible = true;
            _web.Visible = false;
        }
    }
}
