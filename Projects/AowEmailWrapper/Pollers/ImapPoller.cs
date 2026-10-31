using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AowEmailWrapper.ASG;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Helpers;
using AowEmailWrapper.Classes;
using AowEmailWrapper.Pollers.MessageStore;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace AowEmailWrapper.Pollers
{
    /// <summary>
    /// Watches an IMAP mailbox. Where the server supports IMAP IDLE (all the big providers do) a
    /// connection stays open and the server announces new mail the moment it arrives; the timed
    /// interval from the settings is then only a safety net. Servers without IDLE are polled on the
    /// interval as before.
    /// </summary>
    public class ImapPoller : BasePoller
    {
        /// <summary>
        /// Unread mail older than this is never downloaded. It keeps a first poll on a busy mailbox
        /// (or one whose local records are stale) from replaying old turns, and it bounds the work per poll.
        /// </summary>
        public const int LookBackDays = 365;

        /// <summary>How many message structures to request from the server in one round trip.</summary>
        private const int SummaryChunkSize = 250;

        /// <summary>Servers drop an IDLE after about 30 minutes (Gmail sooner), so it is renewed well before that.</summary>
        private const int IdleRenewMinutes = 9;

        private const int ReconnectDelaySeconds = 30;
        private const int ReconnectDelayMaxSeconds = 300;

        /// <summary>Transient failures are only reported to the user after this many in a row.</summary>
        private const int FailuresBeforeReporting = 3;

        private Thread _worker;
        private CancellationTokenSource _stop;
        private CancellationTokenSource _wakeIdle;
        private readonly object _wakeLock = new object();
        private readonly ManualResetEventSlim _scanRequest = new ManualResetEventSlim(false);
        private int _consecutiveFailures;

        public ImapPoller(
            string host,
            int port,
            SSLType sslType,
            string username,
            string password,
            int pollInterval,
            EmailSaveFolder saveFolder,
            AowGameManager gameManager)
            : base(
            host,
            port,
            sslType,
            username,
            password,
            pollInterval,
            saveFolder,
            gameManager)
        { }

        /// <summary>True while the background connection loop is running.</summary>
        public bool IsWatching
        {
            get { return _worker != null && _stop != null && !_stop.IsCancellationRequested; }
        }

        #region Start / stop / requests

        public override void Start()
        {
            if (IsWatching)
            {
                return;
            }

            AllowChecks();
            _stop = new CancellationTokenSource();
            _consecutiveFailures = 0;
            _scanRequest.Reset();
            StartTimer();

            _worker = new Thread(new ThreadStart(Run));
            _worker.IsBackground = true;
            _worker.Name = "IMAP watcher";
            _worker.Start();
        }

        public override void Stop()
        {
            StopChecks();
            StopTimer();

            CancellationTokenSource stop = _stop;
            _stop = null;
            _worker = null;

            if (stop != null)
            {
                //Do not join the worker: Stop can be called from the worker's own event handlers
                stop.Cancel();
            }
            WakeIdle();
        }

        public override void PollNow()
        {
            if (IsWatching)
            {
                RequestScan();
            }
            else
            {
                base.PollNow();
            }
        }

        protected override void OnTimerElapsed()
        {
            if (IsWatching)
            {
                RequestScan();
            }
            else
            {
                base.OnTimerElapsed();
            }
        }

        private void RequestScan()
        {
            _scanRequest.Set();
            WakeIdle();
        }

        private void WakeIdle()
        {
            lock (_wakeLock)
            {
                if (_wakeIdle != null)
                {
                    try { _wakeIdle.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        #endregion

        #region Connection loop

        private void Run()
        {
            CancellationTokenSource stop = _stop;
            if (stop == null)
            {
                return;
            }
            CancellationToken token = stop.Token;
            int delay = ReconnectDelaySeconds;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (ImapClient imap = new ImapClient())
                    {
                        //Signing in, the history import and the recovery count as checking the mailbox until the first
                        //scan has begun, so the Wrapper does not close in the middle: all of them can file turns. A
                        //stopped poller begins none of it.
                        if (!TryBeginConnectionWork())
                        {
                            return;
                        }
                        bool connecting = true;
                        Action connected = () =>
                        {
                            if (connecting)
                            {
                                connecting = false;
                                EndConnectionWork(false);
                            }
                        };

                        IMailFolder inbox;
                        bool canIdle;
                        try
                        {
                            imap.Timeout = NETWORK_TIMEOUT_MS;
                            imap.Connect(_host, _port, MailHelper.ToSecureSocketOptions(_sslType), token);
                            MailHelper.Authenticate(imap, _username, _password, OAuthProvider);

                            inbox = imap.Inbox;
                            inbox.Open(FolderAccess.ReadWrite, token);

                            canIdle = imap.Capabilities.HasFlag(ImapCapabilities.Idle);

                            if (ImportHistoryOnNextScan)
                            {
                                ImportHistory(imap, token);
                            }

                            Trace.TraceInformation("IMAP connected to {0}, IDLE {1}", _host, canIdle ? "supported" : "not supported, polling on the timer");

                            RecoverMissedTurns(imap, token);

                            _scanRequest.Reset();
                            //Ends the connection's count once the scan's own has begun, so there is no moment between
                            Scan(inbox, connected);
                        }
                        finally
                        {
                            if (connecting)
                            {
                                connecting = false;
                                EndConnectionWork(true);
                            }
                        }
                        _consecutiveFailures = 0;
                        delay = ReconnectDelaySeconds;

                        if (!canIdle)
                        {
                            imap.Disconnect(true, token);
                            //Wait for the timer or Poll now, then reconnect and scan
                            _scanRequest.Wait(token);
                            continue;
                        }

                        EventHandler<EventArgs> newMail = (s, e) =>
                        {
                            Trace.TraceInformation("Server announced new mail in the inbox");
                            RequestScan();
                        };
                        inbox.CountChanged += newMail;
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                if (_scanRequest.IsSet)
                                {
                                    _scanRequest.Reset();
                                    Scan(inbox);
                                    continue;
                                }

                                using (CancellationTokenSource renew = new CancellationTokenSource(TimeSpan.FromMinutes(IdleRenewMinutes)))
                                using (CancellationTokenSource done = CancellationTokenSource.CreateLinkedTokenSource(token, renew.Token))
                                {
                                    lock (_wakeLock) { _wakeIdle = done; }
                                    try
                                    {
                                        imap.Idle(done.Token, token);
                                    }
                                    finally
                                    {
                                        lock (_wakeLock) { _wakeIdle = null; }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            inbox.CountChanged -= newMail;
                        }

                        imap.Disconnect(true);
                    }
                }
                catch (OperationCanceledException)
                {
                    //Stopping
                }
                catch (AuthenticationException ex)
                {
                    //A rejected sign-in will not fix itself; report it at once and stop, the main form pauses polling
                    Trace.TraceError("IMAP sign-in rejected: {0}", ex.Message);
                    ReportFailure(ex);
                    return;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    Trace.TraceWarning("IMAP connection problem ({0} in a row): {1}", _consecutiveFailures, ex.Message);

                    if (_consecutiveFailures >= FailuresBeforeReporting)
                    {
                        ReportFailure(ex);
                    }

                    if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(delay)))
                    {
                        return;
                    }
                    delay = Math.Min(delay * 2, ReconnectDelayMaxSeconds);
                }
            }
        }

        private void ReportFailure(Exception ex)
        {
            //A stopped poller has nobody left to tell
            if (TryPollBegin())
            {
                PollEnd(false, ex);
            }
        }

        #endregion

        #region Scanning

        /// <summary>One-off check on its own connection, used when the watcher is not running.</summary>
        protected override void Poll()
        {
            bool emailDownloaded = false;
            Exception error = null;

            if (!TryPollBegin())
            {
                return;
            }

            try
            {
                using (ImapClient imap = new ImapClient())
                {
                    imap.Timeout = NETWORK_TIMEOUT_MS;
                    imap.Connect(_host, _port, MailHelper.ToSecureSocketOptions(_sslType));
                    MailHelper.Authenticate(imap, _username, _password, OAuthProvider);

                    IMailFolder inbox = imap.Inbox;
                    inbox.Open(FolderAccess.ReadWrite);

                    if (ImportHistoryOnNextScan)
                    {
                        ImportHistory(imap, CancellationToken.None);
                    }

                    RecoverMissedTurns(imap, CancellationToken.None);

                    emailDownloaded = ScanMailbox(inbox);

                    imap.Disconnect(true);
                }
            }
            catch (Exception ex)
            {
                error = ex;
                Trace.TraceError(ex.ToString());
                Trace.Flush();
            }
            finally
            {
                PollEnd(emailDownloaded, error);
            }
        }

        /// <summary>
        /// A scan on the watcher's open connection. Errors propagate so the loop reconnects. A stopped poller begins
        /// none and leaves the loop. <paramref name="begun"/> runs once the scan counts as checking the mailbox.
        /// </summary>
        private void Scan(IMailFolder inbox, Action begun = null)
        {
            bool emailDownloaded = false;
            bool completed = false;

            if (!TryPollBegin())
            {
                throw new OperationCanceledException("The mail checker was stopped");
            }
            if (begun != null)
            {
                begun();
            }
            try
            {
                emailDownloaded = ScanMailbox(inbox);
                completed = true;
            }
            finally
            {
                if (completed)
                {
                    PollEnd(emailDownloaded, null);
                }
                else
                {
                    //Not reported here: the connection loop decides whether the failure is worth telling the user about
                    PollAbort();
                }
            }
        }

        /// <summary>
        /// One-time look through the mailbox, before any new mail is processed: past opponents become
        /// known, and turns the player still owes that this install has never seen (a new computer)
        /// are downloaded and filed like any other turn.
        /// </summary>
        private void ImportHistory(ImapClient imap, CancellationToken token)
        {
            MailboxHistory history;
            try
            {
                history = ContactHistory.Scan(imap, OwnAddresses, LookBackDays, token);
                ImportHistoryOnNextScan = false;
                RaiseHistoryImported(history);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                //Tried again on the next connection
                Trace.TraceWarning("Could not look through {0} for past opponents: {1}", _host, ex.Message);
                return;
            }

            if (history.ToRecover == null || history.ToRecover.Count == 0)
            {
                return;
            }

            IMailFolder inbox = imap.Inbox;
            foreach (MailboxTurn turn in history.ToRecover)
            {
                try
                {
                    MimeMessage email = inbox.GetMessage(new UniqueId(turn.Uid), token);
                    bool recorded;
                    if (ProcessEmailAttachments(email, out recorded) > 0 && recorded)
                    {
                        history.Recovered++;
                        Trace.TraceInformation("Recovered unplayed turn {0} from {1}", turn.FileName, turn.From);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Could not recover {0}: {1}", turn.FileName, ex.Message);
                }
            }

            RaiseTurnsRecovered(history.Recovered);
        }

        /// <summary>
        /// Files turns that reached this install but never its activity log. A Wrapper closed in the middle of a check
        /// (an automatic update did this right after start-up) could store a turn and mark its email as read without
        /// recording it, so its game stayed Sent and the player was never told the turn had come.
        /// </summary>
        private void RecoverMissedTurns(ImapClient imap, CancellationToken token)
        {
            try
            {
                List<SentGame> games = RaiseSentGamesWanted();
                if (games.Count > 0)
                {
                    RecoverStrandedTurns(imap.Inbox, games, token);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                //Tried again on the next connection
                Trace.TraceWarning("Could not look through {0} for turns that were never recorded: {1}", _host, ex.Message);
            }
        }

        /// <summary>
        /// The mark such a turn leaves: its game's line is Sent, yet the game's turn file in this install was written
        /// after the send, and it is the very save a read email from one of the game's players carries. Only then is
        /// the turn filed again. A turn answered elsewhere, or read first on another computer or a phone, is not in
        /// this install's folders, and the turn the player answered was written before the send. "After the send"
        /// holds against both the line's time and the copy of the sent turn kept for Resend, written as it went out:
        /// the copy is dated by the same file system as the turn file, so a time zone changed since does not count,
        /// and the line's time catches a copy left from an earlier send. A game without such a copy has only the line's
        /// time, so its file must be written later than any change of time zone could account for (see
        /// <see cref="NoCopyMargin"/>). Games without the mark cost nothing; a file is looked up in the mailbox once.
        /// Returns how many were filed.
        /// </summary>
        internal int RecoverStrandedTurns(IMailFolder inbox, IList<SentGame> games, CancellationToken token)
        {
            List<KeyValuePair<SentGame, FileInfo>> stranded = new List<KeyValuePair<SentGame, FileInfo>>();
            foreach (SentGame game in games.Where(game => game != null && game.Players != null && game.Players.Count > 0))
            {
                FileInfo sentCopy = ResendHelper.GetCopy(game.FileName);

                foreach (FileInfo file in _gameManager.StoredTurnFiles(game.FileName))
                {
                    bool afterSend = sentCopy != null
                        ? file.LastWriteTimeUtc > sentCopy.LastWriteTimeUtc + StrandedMargin && file.LastWriteTime > game.Sent + StrandedMargin
                        : file.LastWriteTime > game.Sent + NoCopyMargin;
                    if (afterSend && !_strandedChecked.Contains(StrandedKey(file)))
                    {
                        stranded.Add(new KeyValuePair<SentGame, FileInfo>(game, file));
                    }
                }
            }
            if (stranded.Count == 0)
            {
                return 0;
            }

            List<MailboxTurn> candidates = ContactHistory.FindTurnsFrom(inbox, stranded.SelectMany(pair => pair.Key.Players), stranded.Min(pair => pair.Key.Sent), token);

            int recovered = 0;
            foreach (KeyValuePair<SentGame, FileInfo> pair in stranded)
            {
                SentGame game = pair.Key;
                HashSet<string> players = new HashSet<string>(game.Players, StringComparer.OrdinalIgnoreCase);
                byte[] stored = File.ReadAllBytes(pair.Value.FullName);
                bool found = false;

                foreach (MailboxTurn turn in candidates
                    .Where(turn => turn.GameKey == game.GameKey && players.Contains(turn.From ?? string.Empty))
                    .OrderByDescending(turn => turn.Arrived ?? turn.Date))
                {
                    MimeMessage email = inbox.GetMessage(new UniqueId(turn.Uid), token);
                    if (!CarriesSave(email, stored))
                    {
                        continue;
                    }

                    found = true;
                    bool recorded;
                    if (ProcessEmailAttachments(email, out recorded) > 0 && recorded)
                    {
                        recovered++;
                        Trace.TraceInformation("Recovered turn {0} from {1}: stored {2:u} but never recorded", turn.FileName, turn.From, pair.Value.LastWriteTime);
                    }
                    break;
                }

                if (!found)
                {
                    Trace.TraceInformation("{0} was written after its turn was sent, but no read email carries it; left alone", pair.Value.FullName);
                }
                _strandedChecked.Add(StrandedKey(pair.Value));
            }

            if (recovered > 0)
            {
                RaiseTurnsRecovered(recovered);
            }
            return recovered;
        }

        /// <summary>
        /// How much later than the send a turn file must be written to count as a turn that came after it. The file of a
        /// turn is written just before its line, and the game rewrites its EmailIn file just before a send, so before
        /// the copy for Resend too.
        /// </summary>
        private static readonly TimeSpan StrandedMargin = TimeSpan.FromMinutes(1);

        /// <summary>
        /// How much later than the line's time a turn file must be written when the game has no copy for Resend. Up to
        /// 2.1.11 a turn that arrived deleted that copy before the activity log was saved, so a turn stored while the
        /// Wrapper was still downloading an update, its window open, lost the copy as well as its line. The line's time
        /// is this computer's clock when the turn went, and a time zone changed since moves it by up to the difference
        /// between the two zones, which is never more than 26 hours.
        /// </summary>
        private static readonly TimeSpan NoCopyMargin = TimeSpan.FromHours(26);

        private readonly HashSet<string> _strandedChecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string StrandedKey(FileInfo file)
        {
            return file.FullName + "|" + file.LastWriteTimeUtc.Ticks;
        }

        /// <summary>True when one of the email's save games is, byte for byte, the given file.</summary>
        private static bool CarriesSave(MimeMessage email, byte[] stored)
        {
            foreach (MimePart attachment in MailHelper.GetAttachments(email))
            {
                if (!string.IsNullOrEmpty(attachment.FileName) && ASGFileInfo.IsAsg(attachment.FileName))
                {
                    byte[] data = MailHelper.GetAttachmentBytes(attachment);
                    if (data != null && data.AsSpan().SequenceEqual(stored))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        internal bool ScanMailbox(IMailFolder inbox)
        {
            bool emailDownloaded = false;

            IList<UniqueId> unseen = inbox.Search(SearchQuery.NotSeen);
            List<long> serverUids = unseen.Select(uid => (long)uid.Id).ToList();

            //Only unread mail from the look back window is a candidate for download
            DateTime since = DateTime.Now.AddDays(-LookBackDays);
            HashSet<long> recent = new HashSet<long>(
                inbox.Search(SearchQuery.NotSeen.And(SearchQuery.DeliveredAfter(since))).Select(uid => (long)uid.Id));

            MessageStoreCollection localMessageStore = MessageStoreManager.LoadLocalMessageStore(_username, _host)
                ?? new MessageStoreCollection(new List<long>());

            MessageStoreManager.RemoveMessagesNoLongerOnServer(ref localMessageStore, serverUids);

            List<long> uidsToCheck = MessageStoreManager.GetMessagesToCheck(localMessageStore, serverUids);
            List<long> recentToCheck = uidsToCheck.Where(uid => recent.Contains(uid)).ToList();

            //Older unread mail is recorded as dealt with and never fetched
            foreach (long uid in uidsToCheck.Where(uid => !recent.Contains(uid)))
            {
                localMessageStore.Messages.Add(new MessageStoreMessage(uid));
            }

            //Ask the server which of the recent messages carry a save game, without downloading them
            HashSet<long> withSaveGame;
            HashSet<long> wrapperMessages;
            Classify(inbox, recentToCheck, out withSaveGame, out wrapperMessages);
            List<UniqueId> toDelete = new List<UniqueId>();

            //Wrapper messages are for the Wrapper, not the player, and are handled even when already read: the
            //player's phone or webmail may open one before the Wrapper checks, and it would then stay in the inbox
            //for good. Found by subject, confirmed by the wrapper's header like the unread ones.
            List<long> readWrapperMessages = FindReadWrapperMessages(inbox, since);
            foreach (long uid in readWrapperMessages)
            {
                wrapperMessages.Add(uid);
            }

            Trace.TraceInformation("Mailbox scan: {0} unread, {1} not seen before within {2} days, {3} with a save game attached, {4} wrapper message(s) already read",
                serverUids.Count, recentToCheck.Count, LookBackDays, withSaveGame.Count, readWrapperMessages.Count);

            //In mailbox order, so the read wrapper messages take their place among the unread ones
            foreach (long uid in recentToCheck.Concat(readWrapperMessages).Distinct().OrderBy(uid => uid))
            {
                if (wrapperMessages.Contains(uid))
                {
                    //A question from, or an answer to, another wrapper: dealt with and then removed from the mailbox
                    UniqueId wrapperId = new UniqueId((uint)uid);
                    try
                    {
                        HandleWrapperMessage(inbox.GetMessage(wrapperId));
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning("Could not read wrapper message {0}: {1}", uid, ex.Message);
                    }
                    toDelete.Add(wrapperId);
                    continue;
                }

                if (!withSaveGame.Contains(uid))
                {
                    localMessageStore.Messages.Add(new MessageStoreMessage(uid));
                    continue;
                }

                UniqueId uniqueId = new UniqueId((uint)uid);

                MimeMessage email = inbox.GetMessage(uniqueId);

                bool recorded;
                if (ProcessEmailAttachments(email, out recorded) > 0)
                {
                    if (!recorded)
                    {
                        //The activity log did not take the turn: the Wrapper is closing, or storing it failed. Marked as
                        //read, the turn could sit in the game's folder with nothing saying it waits; unread, the next
                        //check files it again
                        Trace.TraceWarning("The turn in message {0} was not recorded; left unread for the next check", uid);
                        inbox.RemoveFlags(uniqueId, MessageFlags.Seen, true);
                        continue;
                    }

                    //We want the user to be able to go Mark as Unread > Poll > Redownload
                    //So just mark as read but don't add to local message store
                    emailDownloaded = true;
                    inbox.AddFlags(uniqueId, MessageFlags.Seen, true);
                }
                else
                {
                    localMessageStore.Messages.Add(new MessageStoreMessage(uid));
                    inbox.RemoveFlags(uniqueId, MessageFlags.Seen, true);
                }
            }

            RemoveWrapperMessages(inbox, toDelete);

            MessageStoreManager.SaveLocalMessageStore(_username, _host, localMessageStore);
            localMessageStore.Dispose();

            return emailDownloaded;
        }

        /// <summary>Wrapper messages within the look-back window that are already marked as read.</summary>
        internal static List<long> FindReadWrapperMessages(IMailFolder inbox, DateTime since)
        {
            try
            {
                List<long> candidates = inbox.Search(SearchQuery.Seen.And(SearchQuery.SubjectContains(TurnQuery.SubjectPrefix)).And(SearchQuery.DeliveredAfter(since)))
                    .Select(uid => (long)uid.Id).ToList();
                return candidates.Count == 0 ? candidates : FindWrapperMessages(inbox, candidates).OrderBy(uid => uid).ToList();
            }
            catch (Exception ex)
            {
                //A server that cannot search by subject leaves them where they are, as before
                Trace.TraceWarning("Could not look for wrapper messages already read: {0}", ex.Message);
                return new List<long>();
            }
        }

        /// <summary>Messages carrying the wrapper's own header, found by fetching that header alone.</summary>
        public static HashSet<long> FindWrapperMessages(IMailFolder folder, List<long> uids)
        {
            HashSet<long> withSaveGame;
            HashSet<long> wrapperMessages;
            Classify(folder, uids, out withSaveGame, out wrapperMessages);
            return wrapperMessages;
        }

        /// <summary>
        /// One request per chunk tells apart the messages with a save game attached and the wrapper's
        /// own queries and replies, from the structure and the one header the server sends back.
        /// Nothing is downloaded.
        /// </summary>
        public static void Classify(IMailFolder folder, List<long> uids, out HashSet<long> withSaveGame, out HashSet<long> wrapperMessages)
        {
            withSaveGame = new HashSet<long>();
            wrapperMessages = new HashSet<long>();

            for (int offset = 0; offset < uids.Count; offset += SummaryChunkSize)
            {
                List<UniqueId> chunk = uids.Skip(offset).Take(SummaryChunkSize).Select(uid => new UniqueId((uint)uid)).ToList();

                FetchRequest request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure)
                {
                    Headers = new HeaderSet(new[] { TurnQuery.KindHeader })
                };

                foreach (IMessageSummary summary in folder.Fetch(chunk, request))
                {
                    if (summary.Headers != null && TurnQuery.IsWrapperMessage(summary.Headers))
                    {
                        wrapperMessages.Add(summary.UniqueId.Id);
                        continue;
                    }

                    if (summary.Body == null)
                    {
                        continue;
                    }

                    foreach (BodyPartBasic part in summary.BodyParts)
                    {
                        if (!string.IsNullOrEmpty(part.FileName) && ASGFileInfo.IsAsg(part.FileName))
                        {
                            withSaveGame.Add(summary.UniqueId.Id);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>Flags and expunges wrapper chatter so it never lingers in the player's mailbox.</summary>
        public static void RemoveWrapperMessages(IMailFolder folder, IList<UniqueId> uids)
        {
            if (uids.Count == 0)
            {
                return;
            }

            try
            {
                folder.AddFlags(uids, MessageFlags.Deleted, true);
                try
                {
                    folder.Expunge(uids);
                }
                catch (NotSupportedException)
                {
                    //No UIDPLUS: expunge everything flagged, which is only what this scan flagged
                    folder.Expunge();
                }
                Trace.TraceInformation("Removed {0} wrapper message(s) from the mailbox", uids.Count);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Could not remove wrapper messages: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Uses the MIME structure the server already knows (BODYSTRUCTURE) to find messages with an
        /// Age of Wonders save attached. Costs one request per chunk instead of one download per message.
        /// </summary>
        public static HashSet<long> FindMessagesWithSaveGames(IMailFolder folder, List<long> uids)
        {
            HashSet<long> withSaveGame;
            HashSet<long> wrapperMessages;
            Classify(folder, uids, out withSaveGame, out wrapperMessages);
            return withSaveGame;
        }

        #endregion
    }
}
