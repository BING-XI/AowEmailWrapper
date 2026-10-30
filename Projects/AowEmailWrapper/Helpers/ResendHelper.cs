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
