using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        /// <summary>
        /// Asked, for a resent turn, whether the player has answered that turn since it was first sent (the game's file
        /// name, the first send's date). Such a resend is not stored: it would put back a turn already played.
        /// </summary>
        public Func<string, DateTimeOffset, bool> IsAnsweredResend { get; set; }

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
        private bool _stopped;
        //Guards the three above, so that once the poller is stopped no check or connection can begin
        private readonly object _gate = new object();
        protected EmailSaveFolder _saveFolder;

        /// <summary>
        /// True during a check of the mailbox, and while a new connection signs in and looks through the mailbox for
        /// turns to recover: all of them can store a turn, and closing the Wrapper in the middle cuts that off. Once
        /// the poller is stopped nothing new begins, so when this turns false it stays false.
        /// </summary>
        public bool IsPolling
        {
            get
            {
                lock (_gate)
                {
                    return _pollQueue.Count > 0 || _connectionWork > 0;
                }
            }
        }

        /// <summary>Lets checks begin again, for a poller started (again).</summary>
        protected void AllowChecks()
        {
            lock (_gate)
            {
                _stopped = false;
            }
        }

        /// <summary>From here on no check or connection begins; one under way finishes.</summary>
        protected void StopChecks()
        {
            lock (_gate)
            {
                _stopped = true;
            }
        }

        /// <summary>Counts a new connection as checking the mailbox until its first scan begins; false once stopped.</summary>
        protected bool TryBeginConnectionWork()
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return false;
                }
                _connectionWork++;
                return true;
            }
        }

        /// <summary>
        /// Ends what <see cref="TryBeginConnectionWork"/> began. A connection that failed before its first scan tells the
        /// window, which shows the envelope only once nothing is checking.
        /// </summary>
        protected void EndConnectionWork(bool failed)
        {
            lock (_gate)
            {
                _connectionWork--;
            }
            if (failed && OnEmailEvent != null)
            {
                OnEmailEvent(this, new PollerEventArgs(PollState.Aborted, false));
            }
        }

        /// <summary>
        /// Waits until none of the pollers is checking, for at most <paramref name="limit"/>; false when one still is.
        /// Meant for stopped pollers, which begin nothing new.
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> WhenIdle(IEnumerable<BasePoller> pollers, TimeSpan limit, TimeSpan step)
        {
            List<BasePoller> watched = (pollers ?? Enumerable.Empty<BasePoller>()).ToList();
            Stopwatch waited = Stopwatch.StartNew();
            while (watched.Any(poller => poller.IsPolling))
            {
                if (waited.Elapsed >= limit)
                {
                    return false;
                }
                await System.Threading.Tasks.Task.Delay(step);
            }
            return true;
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
            AllowChecks();
            if (_timer == null)
            {
                StartTimer();
                PollNow();
            }
        }

        public virtual void Stop()
        {
            StopChecks();
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

        /// <summary>Begins a check; false, with nothing begun, once the poller is stopped.</summary>
        protected bool TryPollBegin()
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return false;
                }
                _pollQueue.Enqueue(Guid.NewGuid().ToString());
            }
            if (OnEmailEvent != null)
            {
                OnEmailEvent(this, new PollerEventArgs(PollState.Begin, false));
            }
            return true;
        }

        protected void PollEnd(bool emailDownloaded, Exception ex)
        {
            lock (_gate)
            {
                _pollQueue.Dequeue();
            }
            if (OnEmailEvent != null)
            {
                OnEmailEvent(this, new PollerEventArgs(PollState.End, emailDownloaded, ex));
            }
        }

        /// <summary>Ends a check that failed without telling the user; the caller decides if it matters.</summary>
        protected void PollAbort()
        {
            lock (_gate)
            {
                _pollQueue.Dequeue();
            }
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
                                    //A resend of a turn the player has played since it first came: storing it would overwrite
                                    //the game's file with the old turn and show it as waiting again
                                    DateTimeOffset? firstSent = ResendHelper.ResentOf(email);
                                    Func<string, DateTimeOffset, bool> answered = IsAnsweredResend;
                                    if (firstSent.HasValue && answered != null && answered(theASG.FileNameTrue, firstSent.Value))
                                    {
                                        Trace.TraceInformation("Resend of {0} from {1}, first sent {2:u}, ignored: the turn was answered since", theASG.FileNameTrue, sender, firstSent.Value);
                                        continue;
                                    }

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
