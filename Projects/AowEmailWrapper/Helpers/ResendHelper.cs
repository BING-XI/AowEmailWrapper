using System;
using System.Diagnostics;
using System.IO;
using MimeKit;

namespace AowEmailWrapper.Helpers
{
    public class ResendHelper
    {
        #region Private Members

        private const string ResendFileNameTemplate = "{0}_resend.eml";

        #endregion

        #region Public Methods

        /// <summary>
        /// Writes the email to the resend folder so a failed send can be retried later.
        /// Runs synchronously so the message is never being written and sent at the same time.
        /// </summary>
        public static void Save(MimeMessage theEmail)
        {
            try
            {
                MimePart firstAttachment = MailHelper.GetFirstAttachment(theEmail);
                if (firstAttachment != null && !string.IsNullOrEmpty(firstAttachment.FileName))
                {
                    theEmail.WriteTo(GetEmlFilePath(firstAttachment.FileName));
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
                Trace.Flush();
            }
        }

        public static MimeMessage Load(string asgFileName)
        {
            MimeMessage returnVal = null;

            try
            {
                returnVal = MimeMessage.Load(GetEmlFilePath(asgFileName));
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
                Trace.Flush();
            }

            return returnVal;
        }

        public static void Delete(string asgFileName)
        {
            try
            {
                string filePath = GetEmlFilePath(asgFileName);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
                Trace.Flush();
            }
        }

        public static bool CanResend(string asgFileName)
        {
            string filePath = GetEmlFilePath(asgFileName);
            return File.Exists(filePath);
        }

        /// <summary>
        /// Makes a kept copy a new email before it goes out again: a new Message-ID, and dated now. With the Message-ID
        /// of the first send, a mailbox that already has that email, as Gmail's do, drops the second one as the same
        /// message, so the turn never reached a player whose first copy had gone astray.
        /// </summary>
        public static void Renew(MimeMessage theEmail)
        {
            //When the turn first went, kept through any number of resends: a new email reaches a player who has already
            //played the turn too, and their Wrapper must be able to tell it is not a new one
            if (ResentOf(theEmail) == null)
            {
                theEmail.Headers.Add(Classes.TurnQuery.ResendOfHeader, theEmail.Date.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
            theEmail.MessageId = MailHelper.NewMessageId(MailHelper.GetFromAddress(theEmail));
            theEmail.Date = DateTimeOffset.Now;
        }

        /// <summary>For a resent turn, when it was first sent; null for a turn sent once.</summary>
        public static DateTimeOffset? ResentOf(MimeMessage theEmail)
        {
            string value = theEmail != null ? theEmail.Headers[Classes.TurnQuery.ResendOfHeader] : null;
            DateTimeOffset first;
            if (!string.IsNullOrWhiteSpace(value) &&
                DateTimeOffset.TryParse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out first))
            {
                return first;
            }
            return null;
        }

        /// <summary>The copy kept of the game's last sent turn, written as it went out; null when there is none.</summary>
        public static FileInfo GetCopy(string asgFileName)
        {
            try
            {
                FileInfo copy = new FileInfo(GetEmlFilePath(asgFileName));
                return copy.Exists ? copy : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Private Methods

        private static string GetEmlFilePath(string fileName)
        {
            return Path.Combine(AppDataHelper.Resend.FullName, string.Format(ResendFileNameTemplate, fileName));
        }

        #endregion
    }
}
