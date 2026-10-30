using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Timers;
using AowEmailWrapper.ASG;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Helpers;
using MimeKit;

namespace AowEmailWrapper.Pollers
{
    public delegate void PollerEmailEventHandler(object sender, PollerEventArgs e);
    public delegate void PollerHistoryEventHandler(BasePoller sender, MailboxHistory history);
    public delegate void PollerRecoveredEventHandler(BasePoller sender, int count);
    public delegate void PollerWrapperMessageEventHandler(BasePoller sender, MimeMessage message);
    public delegate void PollerSentGamesEventHandler(BasePoller sender, List<SentGame> games);

    public abstract class BasePoller
    {
        private const int ONE_MIN_MILLISECONDS = 60000;
        protected const int NETWORK_TIMEOUT_MS = 60000;

        protected AowGameManager _gameManager;
        protected Timer _timer;
        protected string _host;
        protected int _port;
        protected SSLType _sslType;
        protected string _username;
        protected string _password;
        protected string _outputPath;
        protected int _pollInterval;
        public event PollerEmailEventHandler OnEmailEvent;

        /// <summary>
        /// Raised once, on the poller's thread, with what the mailbox scan found when <see cref="ImportHistoryOnNextScan"/>
        /// was set. The handler fills in <see cref="MailboxHistory.ToRecover"/> before returning.
        /// </summary>
        public event PollerHistoryEventHandler OnHistoryImported;

        /// <summary>Raised after unplayed turns from the mailbox were downloaded and filed.</summary>
        public event PollerRecoveredEventHandler OnTurnsRecovered;

        /// <summary>
        /// Raised on the poller's thread when it connects, to learn which games the activity log shows as sent. The
        /// handler fills the list before returning; the poller then looks for turns of those games that arrived but
        /// were never recorded.
        /// </summary>
        public event PollerSentGamesEventHandler OnSentGamesWanted;

        /// <summary>The player's own addresses; mail from them counts as a turn the player sent.</summary>
        public IEnumerable<string> OwnAddresses { get; set; }

        /// <summary>Raised on the poller's thread for a wrapper-to-wrapper query or reply; the message is then deleted from the mailbox.</summary>
        public event PollerWrapperMessageEventHandler OnWrapperMessage;

        /// <summary>True when the message is wrapper chatter, which has then been handed to the main form.</summary>
        protected bool HandleWrapperMessage(MimeMessage email)
        {
            if (!TurnQuery.IsWrapperMessage(email))
            {
                return false;
            }

            PollerWrapperMessageEventHandler handler = OnWrapperMessage;
            if (handler != null)
            {
                handler(this, email);
            }
            return true;
        }

        /// <summary>
        /// When set, the next connection looks through the mailbox for the addresses the player has
        /// exchanged turns with, so opponents from before the contact list existed are not reported as new.
        /// </summary>
        public bool ImportHistoryOnNextScan { get; set; }
        private Queue<string> _pollQueue;
        private int _connectionWork;
        protected EmailSaveFolder _saveFolder;

        /// <summary>
        /// True during a check of the mailbox, and while a new connection looks through it for turns to recover:
        /// both can store a turn, and closing the Wrapper in the middle cuts that off.
        /// </summary>
        public bool IsPolling
        {
            get { return !_pollQueue.Count.Equals(0) || System.Threading.Volatile.Read(ref _connectionWork) > 0; }
        }

        protected void BeginConnectionWork()
        {
            System.Threading.Interlocked.Increment(ref _connectionWork);
        }

        protected void EndConnectionWork()
        {
            System.Threading.Interlocked.Decrement(ref _connectionWork);
        }

        /// <summary>Empty for password sign-in, otherwise the OAuth provider name.</summary>
        public string OAuthProvider { get; set; }

        /// <summary>The account this poller watches; turns it downloads are replied to through it.</summary>
        public string AccountName { get; set; }

        public string Host { get { return _host; } }

        public string Username { get { return _username; } }

        protected BasePoller(
            string host,
            int port,
            SSLType sslType,
            string username,
            string password,
            int pollInterval,
            EmailSaveFolder saveFolder,
            AowGameManager gameManager)
        {
            _host = host;
            _port = port;
            _sslType = sslType;
            _username = username;
            _password = password;
            _pollInterval = pollInterval;
            _saveFolder = saveFolder;
            _gameManager = gameManager;
            _pollQueue = new Queue<string>();
        }

        public virtual void Start()
        {
            if (_timer == null)
            {
                StartTimer();
                PollNow();
            }
        }

        public virtual void Stop()
        {
            StopTimer();
        }

        protected void StartTimer()
        {
            if (_timer == null)
            {
                _timer = new Timer();
                _timer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
                _timer.Interval = Math.Max(1, _pollInterval) * ONE_MIN_MILLISECONDS;
                _timer.Enabled = true;
                _timer.Start();
            }
        }

        protected void StopTimer()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }

        private void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            OnTimerElapsed();
        }

        protected virtual void OnTimerElapsed()
        {
            Poll();
        }

        protected virtual void Poll()
        { }

        public virtual void PollNow()
        {
            System.Threading.Thread pollThread = new System.Threading.Thread(new System.Threading.ThreadStart(this.Poll));
            pollThread.IsBackground = true;
            pollThread.Start();
        }

        protected void PollBegin()
        {
            _pollQueue.Enqueue(Guid.NewGuid().ToString());
            if (OnEmailEvent != null)
            {
                OnEmailEvent(this, new PollerEventArgs(PollState.Begin, false));
            }
        }

        protected void PollEnd(bool emailDownloaded, Exception ex)
        {
            _pollQueue.Dequeue();
            if (OnEmailEvent != null)
            {
                OnEmailEvent(this, new PollerEventArgs(PollState.End, emailDownloaded, ex));
            }
        }

        /// <summary>Ends a check that failed without telling the user; the caller decides if it matters.</summary>
        protected void PollAbort()
        {
            _pollQueue.Dequeue();
            if (OnEmailEvent != null)
            {
                OnEmailEvent(this, new PollerEventArgs(PollState.Aborted, false));
            }
        }

        protected void RaiseHistoryImported(MailboxHistory history)
        {
            PollerHistoryEventHandler handler = OnHistoryImported;
            if (handler != null)
            {
                handler(this, history);
            }
        }

        protected void RaiseTurnsRecovered(int count)
        {
            PollerRecoveredEventHandler handler = OnTurnsRecovered;
            if (handler != null)
            {
                handler(this, count);
            }
        }

        /// <summary>The games the activity log shows as sent; empty when nothing answers.</summary>
        protected List<SentGame> RaiseSentGamesWanted()
        {
            List<SentGame> games = new List<SentGame>();
            PollerSentGamesEventHandler handler = OnSentGamesWanted;
            if (handler != null)
            {
                handler(this, games);
            }
            return games;
        }

        /// <summary>
        /// Saves every Age of Wonders save game attached to the email and returns how many were found.
        /// </summary>
        protected int ProcessEmailAttachments(MimeMessage email)
        {
            bool recorded;
            return ProcessEmailAttachments(email, out recorded);
        }

        /// <summary>
        /// Saves every Age of Wonders save game attached to the email and returns how many were found.
        /// <paramref name="recorded"/> is false when one of them did not reach the activity log, because the Wrapper is
        /// closing or storing it failed; the email is then to be left for the next check.
        /// </summary>
        protected int ProcessEmailAttachments(MimeMessage email, out bool recorded)
        {
            int count = 0;
            recorded = true;

            try
            {
                if (email != null)
                {
                    string bodyText = MailHelper.GetPlainText(email);
                    string sender = MailHelper.GetFromAddress(email);

                    foreach (MimePart attachment in MailHelper.GetAttachments(email))
                    {
                        if (!string.IsNullOrEmpty(attachment.FileName) &&
                            ASGFileInfo.IsAsg(attachment.FileName))
                        {
                            count++;

                            using (ASGFileInfo theASG = new ASGFileInfo(attachment))
                            {
                                if (theASG.Length > 0)
                                {
                                    if (!_gameManager.StoreDownloadFile(theASG, _saveFolder, AccountName, MailHelper.GetModLabel(email), sender, MailHelper.GetSharedNames(email), TurnQuery.WantsNotices(email)))
                                    {
                                        recorded = false;
                                    }

                                    TurnLogger.SaveLog(theASG.FileNameTrue, bodyText);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //A save that could not be written (a locked file, a folder without write access) is not in the log either
                recorded = false;
                Trace.TraceError(ex.ToString());
                Trace.Flush();
            }

            return count;
        }
    }
}
