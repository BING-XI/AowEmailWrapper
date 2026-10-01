using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AowEmailWrapper.ConfigFramework;
using MailKit;
using MailKit.Security;
using MimeKit;

namespace AowEmailWrapper.Helpers
{
    /// <summary>
    /// Bridges MimeKit / MailKit objects and the wrapper's own configuration types.
    /// </summary>
    public static class MailHelper
    {
        public static SecureSocketOptions ToSecureSocketOptions(SSLType sslType)
        {
            switch (sslType)
            {
                case SSLType.SSL:
                    return SecureSocketOptions.SslOnConnect;
                case SSLType.TLS:
                    return SecureSocketOptions.StartTls;
                default:
                    //Let MailKit pick: implicit SSL on the well known SSL ports, otherwise STARTTLS when the server offers it.
                    return SecureSocketOptions.Auto;
            }
        }

        /// <summary>
        /// Signs in with a password, or with a Microsoft OAuth token when the account uses Microsoft sign-in.
        /// </summary>
        public static void Authenticate(IMailService client, string username, string password, string oauthProvider)
        {
            if (MicrosoftOAuth.IsProvider(oauthProvider))
            {
                string accessToken = MicrosoftOAuth.AcquireAccessToken(username);
                client.Authenticate(new SaslMechanismOAuth2(username, accessToken));
            }
            else
            {
                client.Authenticate(username, password);
            }
        }

        /// <summary>
        /// Every MIME part that carries a file, whether or not the sender flagged it as an attachment.
        /// </summary>
        public static List<MimePart> GetAttachments(MimeMessage message)
        {
            List<MimePart> attachments = new List<MimePart>();

            if (message != null && message.Body != null)
            {
                foreach (MimeEntity entity in message.BodyParts)
                {
                    MimePart part = entity as MimePart;
                    if (part != null && (part.IsAttachment || !string.IsNullOrEmpty(part.FileName)))
                    {
                        attachments.Add(part);
                    }
                }
            }

            return attachments;
        }

        /// <summary>Header that names the mod (the sender's copy label) a turn was played with.</summary>
        public const string ModHeaderName = "X-AowEmailWrapper-Mod";

        public static string GetModLabel(MimeMessage message)
        {
            string value = message != null ? message.Headers[ModHeaderName] : null;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public static void SetModLabel(MimeMessage message, string label)
        {
            if (message == null)
            {
                return;
            }
            message.Headers.RemoveAll(ModHeaderName);
            if (!string.IsNullOrWhiteSpace(label))
            {
                message.Headers.Add(ModHeaderName, label.Trim());
            }
        }

        /// <summary>Header that names the turn server a game's Wrappers record its turns on.</summary>
        public const string TurnServerHeaderName = "X-AowEmailWrapper-Turn-Server";

        /// <summary>The turn server the email names, or null when it names none a Wrapper should use.</summary>
        public static string GetTurnServer(MimeMessage message)
        {
            return message != null ? TurnServerClient.Normalise(message.Headers[TurnServerHeaderName]) : null;
        }

        public static void SetTurnServer(MimeMessage message, string server)
        {
            if (message == null)
            {
                return;
            }
            message.Headers.RemoveAll(TurnServerHeaderName);
            string normalised = TurnServerClient.Normalise(server);
            if (normalised != null)
            {
                message.Headers.Add(TurnServerHeaderName, normalised);
            }
        }

        public static MimePart GetFirstAttachment(MimeMessage message)
        {
            return GetAttachments(message).FirstOrDefault();
        }

        /// <summary>
        /// Largest attachment the Wrapper will decode. A real save game is a few megabytes at most;
        /// anything far beyond that is not a turn and is not worth holding in memory.
        /// </summary>
        public const long MaxAttachmentBytes = 32L * 1024 * 1024;

        /// <summary>The decoded attachment, or an empty array when there is none or it is over the size limit.</summary>
        public static byte[] GetAttachmentBytes(MimePart part)
        {
            if (part == null || part.Content == null)
            {
                return new byte[0];
            }

            try
            {
                using (BoundedMemoryStream stream = new BoundedMemoryStream(MaxAttachmentBytes))
                {
                    part.Content.DecodeTo(stream);
                    return stream.ToArray();
                }
            }
            catch (InvalidDataException ex)
            {
                Trace.TraceWarning("Attachment '{0}' skipped: {1}", part.FileName, ex.Message);
                return new byte[0];
            }
        }

        public static void SaveAttachment(MimePart part, string path)
        {
            using (FileStream stream = File.Create(path))
            {
                part.Content.DecodeTo(stream);
            }
        }

        public static string GetAttachmentsString(MimeMessage message)
        {
            return string.Join(", ", GetAttachments(message)
                .Select(part => part.FileName)
                .Where(name => !string.IsNullOrEmpty(name)));
        }

        public static string GetPlainText(MimeMessage message)
        {
            return message == null ? string.Empty : (message.TextBody ?? string.Empty);
        }

        /// <summary>
        /// Replaces the plain text body, keeping the attachments and the rest of the MIME tree intact.
        /// </summary>
        public static void SetPlainText(MimeMessage message, string text)
        {
            TextPart textPart = message.BodyParts
                .OfType<TextPart>()
                .FirstOrDefault(part => part.IsPlain && string.IsNullOrEmpty(part.FileName));

            if (textPart != null)
            {
                textPart.SetText(Encoding.UTF8, text);
                return;
            }

            //No plain text part to update, rebuild the body around the attachments
            BodyBuilder builder = new BodyBuilder();
            builder.TextBody = text;
            foreach (MimePart attachment in GetAttachments(message))
            {
                builder.Attachments.Add(attachment);
            }
            message.Body = builder.ToMessageBody();
        }

        public static string GetFromAddress(MimeMessage message)
        {
            MailboxAddress from = (message == null) ? null : message.From.Mailboxes.FirstOrDefault();
            return from == null ? string.Empty : from.Address;
        }

        public static string GetFirstToAddress(MimeMessage message)
        {
            MailboxAddress to = (message == null) ? null : message.To.Mailboxes.FirstOrDefault();
            return to == null ? string.Empty : to.Address;
        }

        /// <summary>Every To, Cc and Bcc address, joined with the activity log's separator.</summary>
        public static string GetRecipientAddresses(MimeMessage message)
        {
            if (message == null)
            {
                return string.Empty;
            }

            IEnumerable<string> addresses = message.To.Mailboxes
                .Concat(message.Cc.Mailboxes)
                .Concat(message.Bcc.Mailboxes)
                .Select(mailbox => mailbox.Address)
                .Where(address => !string.IsNullOrEmpty(address))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            return string.Join(ActivityList.AddressSeparator.ToString(), addresses);
        }
    }
}
