using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Localization;
using MailKit.Net.Smtp;
using MimeKit;

namespace AowEmailWrapper.Helpers
{
    /// <summary>
    /// Builds a bug report email (the player's description, a summary of their setup and the
    /// Wrapper's log file) and sends it to the maintainer through one of the configured accounts.
    /// </summary>
    public static class BugReportHelper
    {
        private const string EmailKey = "BugReport.Email";
        private const string DefaultEmail = "eugene.j.wolff@gmail.com";
        private const string SubjectTemplate = "AoW Email Wrapper bug report ({0})";
        private const string Divider = "--------------------------------------------------------";
        private const int TimeoutMs = 60000;

        /// <summary>Where the reports go. Overridable through the BugReport.Email app setting.</summary>
        public static string Email
        {
            get { return ConfigHelper.GetProperty<string>(EmailKey, DefaultEmail); }
        }

        public static string Subject
        {
            get { return string.Format(SubjectTemplate, UpdateHelper.CurrentBuild.Describe()); }
        }

        /// <summary>
        /// The account the report is sent from: the primary account when it has an outgoing server,
        /// otherwise the first account that does. Null when no account can send email yet.
        /// </summary>
        public static AccountConfigValues FindSender(Config config)
        {
            if (config == null || config.AccountsList == null || config.AccountsList.Accounts == null)
            {
                return null;
            }

            AccountConfigValues primary = config.AccountsList.PrimaryAccount;
            if (CanSend(primary))
            {
                return primary;
            }
            return config.AccountsList.Accounts.Find(CanSend);
        }

        /// <summary>The address the report is sent from, and the one a reply goes back to.</summary>
        public static string SenderAddress(AccountConfigValues account)
        {
            if (account == null)
            {
                return null;
            }

            string[] candidates =
            {
                account.SmtpConfig != null ? account.SmtpConfig.EmailAddress : null,
                account.PollingConfig != null ? account.PollingConfig.Username : null,
                account.SmtpConfig != null ? account.SmtpConfig.Username : null,
            };

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && candidate.Contains("@"))
                {
                    return candidate.Trim();
                }
            }
            return null;
        }

        /// <summary>Sends the report on a worker thread; a failure surfaces as the task's exception.</summary>
        public static Task SendAsync(AccountConfigValues account, string description, bool attachLog)
        {
            return Task.Run(() => Send(account, description, attachLog));
        }

        /// <summary>
        /// Fallback when the Wrapper has no account to send from: hands the report to the player's
        /// own email program and opens the log folder so the log can be attached by hand.
        /// </summary>
        public static void OpenMailClient(string description)
        {
            string mailto = string.Format("mailto:{0}?subject={1}&body={2}",
                Email,
                Uri.EscapeDataString(Subject),
                Uri.EscapeDataString(BuildBody(null, description)));

            Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
            LogHelper.OpenLogFolder();
        }

        /// <summary>The report text: the player's description followed by a summary of their setup.</summary>
        public static string BuildBody(AccountConfigValues account, string description)
        {
            StringBuilder body = new StringBuilder();
            body.AppendLine((description ?? string.Empty).Trim());
            body.AppendLine();
            body.AppendLine(Divider);
            body.AppendLine(string.Format("Wrapper: {0}", UpdateHelper.CurrentBuild.Describe()));
            body.AppendLine(string.Format("Windows: {0}", RuntimeInformation.OSDescription));
            body.AppendLine(string.Format(".NET: {0}", Environment.Version));
            body.AppendLine(string.Format("Language: {0}", Translator.CurrentLanguageCode));
            body.AppendLine(string.Format("Local time: {0:yyyy-MM-dd HH:mm:ss zzz}", DateTimeOffset.Now));

            if (account != null)
            {
                body.AppendLine(string.Format("Account: {0}{1}",
                    account.Name,
                    string.IsNullOrEmpty(account.OAuthProvider) ? string.Empty : string.Format(" ({0} sign-in)", account.OAuthProvider)));

                PollingConfigValues polling = account.PollingConfig;
                if (polling != null && !string.IsNullOrEmpty(polling.Server))
                {
                    body.AppendLine(string.Format("Incoming: {0} {1}:{2} {3}, every {4} min",
                        polling.EmailType, polling.Server, polling.Port, polling.SSLType, polling.PollInterval));
                }

                SmtpConfigValues smtp = account.SmtpConfig;
                if (smtp != null && !string.IsNullOrEmpty(smtp.SmtpServer))
                {
                    body.AppendLine(string.Format("Outgoing: {0}:{1} {2}{3}",
                        smtp.SmtpServer, smtp.Port, smtp.SmtpSSLType, smtp.Authentication ? ", authenticated" : string.Empty));
                }
            }

            return body.ToString();
        }

        private static bool CanSend(AccountConfigValues account)
        {
            return account != null &&
                account.SmtpConfig != null &&
                !string.IsNullOrEmpty(account.SmtpConfig.SmtpServer) &&
                SenderAddress(account) != null;
        }

        private static void Send(AccountConfigValues account, string description, bool attachLog)
        {
            SmtpConfigValues smtp = account.SmtpConfig;
            PollingConfigValues polling = account.PollingConfig;

            string username = null;
            string password = null;
            if (smtp.Authentication)
            {
                bool usePolling = smtp.UsePollingCredentials && polling != null;
                username = usePolling ? polling.Username : smtp.Username;
                password = usePolling ? polling.PasswordTrue : smtp.PasswordTrue;
            }

            MimeMessage message = Build(account, description, attachLog);

            using (SmtpClient client = new SmtpClient())
            {
                client.Timeout = TimeoutMs;
                client.Connect(smtp.SmtpServer, smtp.Port, MailHelper.ToSecureSocketOptions(smtp.SmtpSSLType));

                if (!string.IsNullOrEmpty(username))
                {
                    MailHelper.Authenticate(client, username, password ?? string.Empty, account.OAuthProvider);
                }

                client.Send(message);
                client.Disconnect(true);
            }

            Trace.TraceInformation("Bug report sent to {0} from {1}", Email, SenderAddress(account));
        }

        private static MimeMessage Build(AccountConfigValues account, string description, bool attachLog)
        {
            string from = SenderAddress(account);

            MimeMessage message = new MimeMessage();
            message.From.Add(new MailboxAddress(string.IsNullOrEmpty(account.Name) ? from : account.Name, from));
            message.To.Add(MailboxAddress.Parse(Email));
            message.Subject = Subject;
            message.MessageId = MailHelper.NewMessageId(from);

            BodyBuilder builder = new BodyBuilder();
            builder.TextBody = BuildBody(account, description);

            if (attachLog)
            {
                //Get everything written so far onto disk before the file is read
                Trace.Flush();
                AttachLog(builder, LogHelper.LogFile);
                AttachLog(builder, LogHelper.PreviousLogFile);
            }

            message.Body = builder.ToMessageBody();
            message.Prepare(EncodingConstraint.SevenBit);
            return message;
        }

        private static void AttachLog(BodyBuilder builder, string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            //The trace listener keeps the current log open for writing, so share the handle
            byte[] bytes;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (MemoryStream copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }

            builder.Attachments.Add(Path.GetFileName(path), bytes, new ContentType("text", "plain"));
        }
    }
}
