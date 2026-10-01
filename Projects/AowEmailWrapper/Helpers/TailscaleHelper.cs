using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace AowEmailWrapper.Helpers
{
    public enum TailscaleState
    {
        NotInstalled,
        NotSignedIn,
        NeedsApproval,
        Failed,
        Published
    }

    public class TailscaleResult
    {
        public TailscaleState State { get; set; }

        /// <summary>The public address of the turn server, when published.</summary>
        public string Url { get; set; }

        /// <summary>The page where Funnel is allowed for the tailnet, when Tailscale asks for it.</summary>
        public string Link { get; set; }

        /// <summary>What the Tailscale command said, for the log.</summary>
        public string Detail { get; set; }
    }

    /// <summary>
    /// Publishes a hosted turn server on the internet through Tailscale Funnel, under a path of its own
    /// so whatever else the player publishes on the same name is left alone. Runs the tailscale
    /// command; call it off the window thread.
    /// </summary>
    public static class TailscaleHelper
    {
        public const string FunnelPath = "/aow-turns";
        public const string DownloadUrl = "https://tailscale.com/download/windows";
        private const int CommandTimeoutMilliseconds = 30000;
        private static readonly Regex LoginLink = new Regex(@"https://login\.tailscale\.com/\S+", RegexOptions.Compiled);

        /// <summary>The tailscale command, or null when Tailscale is not installed.</summary>
        public static string FindCli()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string installed = Path.Combine(programFiles, "Tailscale", "tailscale.exe");
            if (File.Exists(installed))
            {
                return installed;
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            return path.Split(Path.PathSeparator)
                .Where(folder => folder.Trim().Length > 0)
                .Select(folder => Path.Combine(folder.Trim(), "tailscale.exe"))
                .FirstOrDefault(File.Exists);
        }

        /// <summary>Publishes http://127.0.0.1:port under FunnelPath and works out the public address.</summary>
        public static TailscaleResult Publish(int port)
        {
            string cli = FindCli();
            if (cli == null)
            {
                return new TailscaleResult { State = TailscaleState.NotInstalled };
            }

            string dnsName;
            TailscaleResult status = ReadStatus(cli, out dnsName);
            if (status != null)
            {
                return status;
            }

            string output;
            int? exit = Run(cli, string.Format("funnel --bg --yes --set-path {0} http://127.0.0.1:{1}", FunnelPath, port), true, out output);
            Match link = LoginLink.Match(output);
            if (link.Success && exit != 0)
            {
                return new TailscaleResult { State = TailscaleState.NeedsApproval, Link = link.Value.TrimEnd('.', ','), Detail = output };
            }
            if (exit != 0)
            {
                return new TailscaleResult { State = TailscaleState.Failed, Detail = output };
            }

            return new TailscaleResult { State = TailscaleState.Published, Url = "https://" + dnsName + FunnelPath, Detail = output };
        }

        /// <summary>Takes the turn server's path off Funnel, leaving everything else published as it was.</summary>
        public static void Unpublish()
        {
            string cli = FindCli();
            if (cli == null)
            {
                return;
            }

            string output;
            Run(cli, string.Format("funnel --bg --yes --set-path {0} off", FunnelPath), false, out output);
            Trace.TraceInformation("Turn server taken off Funnel: {0}", output.Trim());
        }

        /// <summary>Null when Tailscale is running and signed in, with this machine's name; otherwise the reason it is not.</summary>
        private static TailscaleResult ReadStatus(string cli, out string dnsName)
        {
            dnsName = null;
            string output;
            int? exit = Run(cli, "status --json", false, out output);

            try
            {
                int start = output.IndexOf('{');
                using (JsonDocument json = JsonDocument.Parse(start >= 0 ? output.Substring(start) : output))
                {
                    JsonElement root = json.RootElement;
                    JsonElement state, self, name;
                    bool running = root.TryGetProperty("BackendState", out state) && state.GetString() == "Running";
                    if (running && root.TryGetProperty("Self", out self) && self.TryGetProperty("DNSName", out name) && !string.IsNullOrEmpty(name.GetString()))
                    {
                        dnsName = name.GetString().TrimEnd('.');
                        return null;
                    }
                }
            }
            catch (JsonException)
            {
                //Not running: the command says so in words
            }

            return new TailscaleResult { State = TailscaleState.NotSignedIn, Detail = exit.HasValue ? output : "tailscale status did not answer" };
        }

        /// <summary>
        /// Runs the command and returns its exit code, or null when it was stopped. With stopAtLink the
        /// command is stopped once it prints a login.tailscale.com link, since it would wait there
        /// until the player allows Funnel in the browser.
        /// </summary>
        private static int? Run(string cli, string arguments, bool stopAtLink, out string output)
        {
            StringBuilder text = new StringBuilder();
            ProcessStartInfo start = new ProcessStartInfo(cli, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using (Process process = new Process { StartInfo = start })
            {
                DataReceivedEventHandler collect = (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (text)
                        {
                            text.AppendLine(e.Data);
                        }
                    }
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    output = ex.Message;
                    return -1;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                Stopwatch clock = Stopwatch.StartNew();
                while (!process.WaitForExit(250))
                {
                    bool linked;
                    lock (text)
                    {
                        linked = stopAtLink && LoginLink.IsMatch(text.ToString());
                    }
                    if (linked || clock.ElapsedMilliseconds > CommandTimeoutMilliseconds)
                    {
                        try
                        {
                            process.Kill(true);
                        }
                        catch (Exception)
                        {
                            //Already gone
                        }
                        Thread.Sleep(250);
                        lock (text)
                        {
                            output = text.ToString();
                        }
                        return null;
                    }
                }

                process.WaitForExit();
                lock (text)
                {
                    output = text.ToString();
                }
                return process.ExitCode;
            }
        }
    }
}
