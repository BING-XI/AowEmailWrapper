using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Helpers;
using MailKit.Net.Smtp;
using MimeKit;

namespace AowEmailWrapper.CSES
{
    public delegate void SmtpSenderSentEventHandler(object sender, SmtpSendResponse theResponse);

    public class SmtpSender : IDisposable
    {
        #region Private Members

        private const int NETWORK_TIMEOUT_MS = 120000;
        private const int MAX_SEND_ATTEMPTS = 3;

        private Queue<MimeMessage> _messageQueue;
        private List<string> _messageIDsBeingSent;
        private Dictionary<string, int> _messageSendAttemptCount;
        private string _host;
        private int _port;
        private SSLType _sslType;
        private string _username;
        private string _password;
        private bool _bccMyself;

        #endregion

        #region Public Properties

        public event SmtpSenderSentEventHandler OnEmailSent;

        public bool IsSending
        {
            get { return !_messageQueue.Count.Equals(0); }
        }

        /// <summary>Empty for password sign-in, otherwise the OAuth provider name.</summary>
        public string OAuthProvider { get; set; }

        /// <summary>The account this sender delivers through.</summary>
        public string AccountName { get; set; }

        #endregion

        #region Constructors

        public SmtpSender(string host, int port, SSLType sslType, bool bccMyself)
            : this(host, port, null, null, sslType, bccMyself)
        { }

        public SmtpSender(string host, int port, string username, string password, SSLType sslType, bool bccMyself)
        {
            _host = host;
            _port = port;
            _username = username;
            _password = password;
            _sslType = sslType;
            _bccMyself = bccMyself;

            _messageQueue = new Queue<MimeMessage>();
            _messageIDsBeingSent = new List<string>();
            _messageSendAttemptCount = new Dictionary<string, int>();
        }

        #endregion

        #region Public Methods

        public void SendMessage(MimeMessage theGameEmail)
        {
            PrepareForSending(theGameEmail);

            _messageQueue.Enqueue(theGameEmail);

            System.Threading.Thread newThread = new System.Threading.Thread(new System.Threading.ThreadStart(this.ProcessMessageQueue));
            newThread.SetApartmentState(System.Threading.ApartmentState.STA);
            newThread.IsBackground = true;
            newThread.Start();
        }

        /// <summary>
        /// Takes off what would name this computer. The spool already does for a new turn, but a copy kept for Resend by
        /// an older Wrapper still has the Received line of the Wrapper's own mail server.
        /// </summary>
        internal static void PrepareForSending(MimeMessage theGameEmail)
        {
            theGameEmail.Headers.RemoveAll(HeaderId.Received);
            if (string.IsNullOrEmpty(theGameEmail.MessageId))
            {
                theGameEmail.MessageId = MailHelper.NewMessageId(MailHelper.GetFromAddress(theGameEmail));
            }
        }

        #endregion

        #region Private Methods

        private void ProcessMessageQueue()
        {
            MimeMessage theGameEmail = null;

            try
            {
                if (_messageIDsBeingSent.Count.Equals(0))
                {
                    if (_messageQueue.Count > 0)
                    {
                        theGameEmail = _messageQueue.Peek();
                        _messageIDsBeingSent.Add(theGameEmail.MessageId);
                        SendAowEmail(theGameEmail);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(string.Format("ProcessMessageQueue Error: {0}", ex.ToString()));
                if (theGameEmail != null)
                {
                    RaiseOnEmailSentEvent(new SmtpSendResponse(theGameEmail, false, ex));
                }
            }
        }

        private void SendAowEmail(MimeMessage theGameEmail)
        {
            string toAddress = MailHelper.GetFirstToAddress(theGameEmail);

            try
            {
                if (_bccMyself)
                {
                    MailboxAddress from = theGameEmail.From.Mailboxes.FirstOrDefault();
                    if (from != null &&
                        !theGameEmail.Bcc.Mailboxes.Any(bcc => bcc.Address.Equals(from.Address, StringComparison.OrdinalIgnoreCase)))
                    {
                        theGameEmail.Bcc.Add(from);
                    }
                }

                //Make sure the save game attachment is safely encoded for any server
                theGameEmail.Prepare(EncodingConstraint.SevenBit);

                using (SmtpClient smtp = new SmtpClient())
                {
                    smtp.Timeout = NETWORK_TIMEOUT_MS;
                    smtp.Connect(_host, _port, MailHelper.ToSecureSocketOptions(_sslType));

                    if (!string.IsNullOrEmpty(_username) &&
                        (!string.IsNullOrEmpty(_password) || MicrosoftOAuth.IsProvider(OAuthProvider)))
                    {
                        MailHelper.Authenticate(smtp, _username, _password, OAuthProvider);
                    }

                    smtp.Send(theGameEmail);

                    smtp.Disconnect(true);
                }

                Trace.WriteLine(string.Format("EMAIL: [{0}] Message sent successfully.", toAddress));
                RaiseOnEmailSentEvent(new SmtpSendResponse(theGameEmail, true));
            }
            catch (Exception ex)
            {
                Trace.WriteLine(string.Format("EMAIL: [{0}] {1}", toAddress, ex.ToString()));

                if (IsRetrySend(theGameEmail.MessageId))
                {
                    //Try again
                    _messageIDsBeingSent.Remove(theGameEmail.MessageId);
                    Trace.WriteLine(string.Format("EMAIL: [{0}] Retry: {1}", toAddress, _messageSendAttemptCount[theGameEmail.MessageId]));
                }
                else
                {
                    //Send FAILED
                    RaiseOnEmailSentEvent(new SmtpSendResponse(theGameEmail, false, ex));
                }
            }
            ProcessMessageQueue();
        }

        private bool IsRetrySend(string theID)
        {
            if (!_messageSendAttemptCount.ContainsKey(theID))
            {
                _messageSendAttemptCount.Add(theID, 1);
            }
            else
            {
                _messageSendAttemptCount[theID]++;
            }

            return (_messageSendAttemptCount[theID] < MAX_SEND_ATTEMPTS);
        }

        private void RetryClear(string theID)
        {
            if (_messageSendAttemptCount.ContainsKey(theID))
            {
                _messageSendAttemptCount.Remove(theID);
            }
        }

        private void RaiseOnEmailSentEvent(SmtpSendResponse theResponse)
        {
            if (_messageIDsBeingSent.Contains(theResponse.GameEmail.MessageId))
            {
                _messageIDsBeingSent.Remove(theResponse.GameEmail.MessageId);
            }
            if (_messageQueue.Count > 0 && _messageQueue.Peek().Equals(theResponse.GameEmail))
            {
                _messageQueue.Dequeue();
            }

            RetryClear(theResponse.GameEmail.MessageId);

            if (OnEmailSent != null)
            {
                OnEmailSent.Invoke(this, theResponse);
            }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _messageQueue = null;
            _messageIDsBeingSent = null;
            _messageSendAttemptCount = null;
            _host = null;
            _username = null;
            _password = null;
        }

        #endregion
    }
}
