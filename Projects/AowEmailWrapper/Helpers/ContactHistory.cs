using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AowEmailWrapper.ASG;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace AowEmailWrapper.Helpers
{
    /// <summary>A message in the mailbox that carries a save game.</summary>
    public class MailboxTurn
    {
        public uint Uid { get; set; }
        public string FileName { get; set; }
        public DateTimeOffset Date { get; set; }
        public string From { get; set; }

        /// <summary>When the message reached the mailbox, by the server's clock; null when the server did not say.</summary>
        public DateTimeOffset? Arrived { get; set; }

        /// <summary>The game the file belongs to, ignoring the game's own truncation of the name at a dot.</summary>
        public string GameKey
        {
            get { return ContactHistory.GameKey(FileName); }
        }

        public override string ToString()
        {
            return string.Format("{0} ({1:d}) from {2}", FileName, Date, From);
        }
    }

    /// <summary>A game whose last line in the activity log is Sent.</summary>
    public class SentGame
    {
        public string FileName { get; set; }

        /// <summary>When the player's turn went out, by this computer's clock.</summary>
        public DateTimeOffset Sent { get; set; }

        /// <summary>The game's other players: whoever may send the next turn.</summary>
        public List<string> Players { get; set; }

        public string GameKey
        {
            get { return ContactHistory.GameKey(FileName); }
        }
    }

    /// <summary>What a one-time look through a mailbox found.</summary>
    public class MailboxHistory
    {
        public MailboxHistory()
        {
            Addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Unplayed = new List<MailboxTurn>();
            ToRecover = new List<MailboxTurn>();
        }

        /// <summary>Everyone the player has exchanged save games with.</summary>
        public HashSet<string> Addresses { get; private set; }

        /// <summary>The newest received turn of each game that the player has not answered, newest first.</summary>
        public List<MailboxTurn> Unplayed { get; private set; }

        /// <summary>Filled in by the main form: the unplayed turns it does not already know about, to download.</summary>
        public List<MailboxTurn> ToRecover { get; set; }

        /// <summary>How many of those were downloaded and filed.</summary>
        public int Recovered { get; set; }
    }

    /// <summary>
    /// Finds, in the mailbox, the addresses the player has exchanged turns with and the turns they
    /// still owe, so a fresh install (a new computer, say) picks up where the old one left off and
    /// existing opponents are not reported as new senders.
    /// </summary>
    public static class ContactHistory
    {
        /// <summary>Newest messages looked at per folder. Bounds the one-time scan on a large mailbox.</summary>
        public const int MaxMessagesPerFolder = 2000;

        private const int ChunkSize = 250;

        #region On disk

        /// <summary>Recipients of the turns kept in the resend folder.</summary>
        public static HashSet<string> FromResendFiles()
        {
            HashSet<string> addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                DirectoryInfo folder = AppDataHelper.Resend;
                if (folder == null || !folder.Exists)
                {
                    return addresses;
                }

                foreach (FileInfo file in folder.GetFiles("*.eml"))
                {
                    try
                    {
                        MimeMessage message = MimeMessage.Load(file.FullName);
                        foreach (string address in Split(MailHelper.GetRecipientAddresses(message)))
                        {
                            addresses.Add(address);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning("Could not read {0} for past opponents: {1}", file.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Could not look through the resend folder for past opponents: {0}", ex.Message);
            }

            return addresses;
        }

        #endregion

        #region Mailbox

        /// <summary>
        /// Looks through the read messages in the inbox and the messages in the Sent folder, newest
        /// <see cref="MaxMessagesPerFolder"/> of each, using the structure the server already knows
        /// so nothing is downloaded. Unread inbox mail is left out: the poller has yet to process it.
        /// The inbox is left open the way it was found.
        /// </summary>
        public static MailboxHistory Scan(ImapClient imap, IEnumerable<string> ownAddresses, int lookBackDays, CancellationToken token)
        {
            MailboxHistory history = new MailboxHistory();
            List<MailboxTurn> received = new List<MailboxTurn>();
            List<MailboxTurn> sent = new List<MailboxTurn>();

            IMailFolder inbox = imap.Inbox;
            bool wasOpen = inbox.IsOpen;
            if (!wasOpen)
            {
                inbox.Open(FolderAccess.ReadOnly, token);
            }
            Collect(inbox, inbox.Search(SearchQuery.Seen, token), false, history.Addresses, received, token);

            IMailFolder sentFolder = null;
            try
            {
                sentFolder = imap.GetFolder(SpecialFolder.Sent);
            }
            catch (Exception ex)
            {
                Trace.TraceInformation("No Sent folder to look through: {0}", ex.Message);
            }

            if (sentFolder != null && sentFolder != inbox)
            {
                try
                {
                    sentFolder.Open(FolderAccess.ReadOnly, token);
                    Collect(sentFolder, sentFolder.Search(SearchQuery.All, token), true, history.Addresses, sent, token);
                    sentFolder.Close(false, token);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Could not look through the Sent folder: {0}", ex.Message);
                }
                finally
                {
                    if (!inbox.IsOpen)
                    {
                        inbox.Open(wasOpen ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, token);
                    }
                }
            }

            history.Unplayed.AddRange(FindUnplayed(received, sent, ownAddresses, DateTimeOffset.Now, lookBackDays));

            Trace.TraceInformation("Mailbox history: {0} received and {1} sent messages with a save game, {2} addresses, {3} unplayed turn(s)",
                received.Count, sent.Count, history.Addresses.Count, history.Unplayed.Count);

            return history;
        }

        private static void Collect(IMailFolder folder, IList<UniqueId> uids, bool outgoing, HashSet<string> addresses, List<MailboxTurn> turns, CancellationToken token)
        {
            //UIDs come back ascending; the newest are the most likely to be current games
            List<UniqueId> newest = uids.Skip(Math.Max(0, uids.Count - MaxMessagesPerFolder)).ToList();
            const MessageSummaryItems items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate;

            for (int offset = 0; offset < newest.Count; offset += ChunkSize)
            {
                List<UniqueId> chunk = newest.Skip(offset).Take(ChunkSize).ToList();
                IList<IMessageSummary> summaries = folder.Fetch(chunk, items, token);

                foreach (IMessageSummary summary in summaries)
                {
                    if (summary.Body == null || summary.Envelope == null)
                    {
                        continue;
                    }

                    string fileName = SaveGameName(summary.BodyParts);
                    if (fileName == null)
                    {
                        continue;
                    }

                    foreach (string address in AddressesOf(summary.Envelope, outgoing))
                    {
                        addresses.Add(address);
                    }

                    turns.Add(new MailboxTurn
                    {
                        Uid = summary.UniqueId.Id,
                        FileName = fileName,
                        Date = summary.Envelope.Date ?? summary.InternalDate ?? DateTimeOffset.MinValue,
                        From = AddressesOf(summary.Envelope, false).FirstOrDefault() ?? string.Empty,
                        Arrived = summary.InternalDate
                    });
                }
            }
        }

        /// <summary>
        /// The newest received turn of each game that is newer than anything the player sent for it.
        /// A message from one of the player's own addresses counts as sent (the BCC-myself option
        /// puts a copy in the inbox). Turns older than the look back window are left out.
        /// </summary>
        public static List<MailboxTurn> FindUnplayed(IEnumerable<MailboxTurn> received, IEnumerable<MailboxTurn> sent, IEnumerable<string> ownAddresses, DateTimeOffset now, int lookBackDays)
        {
            HashSet<string> own = new HashSet<string>((ownAddresses ?? Enumerable.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DateTimeOffset> latestSent = new Dictionary<string, DateTimeOffset>();
            Dictionary<string, MailboxTurn> latestReceived = new Dictionary<string, MailboxTurn>();

            foreach (MailboxTurn turn in (sent ?? Enumerable.Empty<MailboxTurn>()).Concat((received ?? Enumerable.Empty<MailboxTurn>()).Where(t => own.Contains(t.From ?? string.Empty))))
            {
                string key = turn.GameKey;
                DateTimeOffset date;
                if (!latestSent.TryGetValue(key, out date) || turn.Date > date)
                {
                    latestSent[key] = turn.Date;
                }
            }

            foreach (MailboxTurn turn in (received ?? Enumerable.Empty<MailboxTurn>()).Where(t => !own.Contains(t.From ?? string.Empty)))
            {
                string key = turn.GameKey;
                MailboxTurn best;
                if (!latestReceived.TryGetValue(key, out best) || turn.Date > best.Date)
                {
                    latestReceived[key] = turn;
                }
            }

            DateTimeOffset oldest = now.AddDays(-lookBackDays);
            List<MailboxTurn> unplayed = new List<MailboxTurn>();
            foreach (MailboxTurn turn in latestReceived.Values)
            {
                DateTimeOffset answered;
                bool answeredSince = latestSent.TryGetValue(turn.GameKey, out answered) && answered >= turn.Date;
                if (turn.Date >= oldest && !answeredSince)
                {
                    unplayed.Add(turn);
                }
            }

            return unplayed.OrderByDescending(t => t.Date).ToList();
        }

        /// <summary>
        /// How far this computer's clock may be from the server's for the server's copy of a send to count as that
        /// send. A copy older than the activity log's send by more than this is from an earlier send.
        /// </summary>
        public static readonly TimeSpan SentCopyTolerance = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Turns the activity log missed: for each game it shows as sent, the newest save game from one of the game's
        /// players that reached the inbox after the player's own newest copy of the game reached the server (in the
        /// Sent folder, or in the inbox for a copy sent to oneself). Only mail already read is looked at; unread mail
        /// is the poller's. Both times are the server's, so a computer clock that is off cannot make an answered turn
        /// look new, and a game whose last send has no copy on the server is left alone. Nothing is downloaded. The
        /// inbox is left open for reading and writing.
        /// </summary>
        public static List<MailboxTurn> FindMissedTurns(IMailFolder inbox, IMailFolder sentFolder, IList<SentGame> games, IEnumerable<string> ownAddresses, int lookBackDays, CancellationToken token)
        {
            HashSet<string> own = new HashSet<string>((ownAddresses ?? Enumerable.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
            List<SentGame> wanted = (games ?? new List<SentGame>()).Where(game => game != null && !string.IsNullOrEmpty(game.FileName) && game.Players != null && game.Players.Count > 0).ToList();
            if (wanted.Count == 0)
            {
                return new List<MailboxTurn>();
            }

            //Searches go by date only; a day either side keeps a time zone from cutting off the first send
            DateTimeOffset earliest = wanted.Min(game => game.Sent) - SentCopyTolerance;
            DateTimeOffset oldest = DateTimeOffset.Now.AddDays(-lookBackDays);
            DateTime since = (earliest > oldest ? earliest : oldest).LocalDateTime.Date.AddDays(-1);

            //Turns come from the games' players; the player's own address in the inbox is a copy of a send
            HashSet<string> senders = new HashSet<string>(own, StringComparer.OrdinalIgnoreCase);
            foreach (SentGame game in wanted)
            {
                senders.UnionWith(game.Players.Where(address => !string.IsNullOrWhiteSpace(address)).Select(address => address.Trim()));
            }
            SearchQuery from = senders.Select(address => (SearchQuery)SearchQuery.FromContains(address)).Aggregate((left, right) => left.Or(right));

            List<MailboxTurn> received = new List<MailboxTurn>();
            HashSet<string> unused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(inbox, inbox.Search(SearchQuery.Seen.And(SearchQuery.DeliveredAfter(since)).And(from), token), false, unused, received, token);

            List<MailboxTurn> sent = new List<MailboxTurn>();
            if (sentFolder != null && sentFolder != inbox)
            {
                try
                {
                    sentFolder.Open(FolderAccess.ReadOnly, token);
                    Collect(sentFolder, sentFolder.Search(SearchQuery.DeliveredAfter(since), token), true, unused, sent, token);
                    sentFolder.Close(false, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Could not look through the Sent folder for the last sends: {0}", ex.Message);
                }
                finally
                {
                    if (!inbox.IsOpen)
                    {
                        inbox.Open(FolderAccess.ReadWrite, token);
                    }
                }
            }

            return PickMissed(received, sent, wanted, own);
        }

        /// <summary>
        /// The choice behind <see cref="FindMissedTurns"/>, on what the mailbox holds: per game, the newest turn from
        /// one of its players that arrived after the newest copy of the player's own send, when that copy is the
        /// send the activity log knows of.
        /// </summary>
        public static List<MailboxTurn> PickMissed(IEnumerable<MailboxTurn> received, IEnumerable<MailboxTurn> sent, IEnumerable<SentGame> games, ICollection<string> ownAddresses)
        {
            HashSet<string> own = new HashSet<string>(ownAddresses ?? new string[0], StringComparer.OrdinalIgnoreCase);
            List<MailboxTurn> inbox = (received ?? Enumerable.Empty<MailboxTurn>()).Where(turn => turn.Arrived.HasValue).ToList();

            Dictionary<string, DateTimeOffset> lastCopy = new Dictionary<string, DateTimeOffset>();
            foreach (MailboxTurn copy in (sent ?? Enumerable.Empty<MailboxTurn>()).Where(turn => turn.Arrived.HasValue).Concat(inbox.Where(turn => own.Contains(turn.From ?? string.Empty))))
            {
                DateTimeOffset date;
                if (!lastCopy.TryGetValue(copy.GameKey, out date) || copy.Arrived.Value > date)
                {
                    lastCopy[copy.GameKey] = copy.Arrived.Value;
                }
            }

            List<MailboxTurn> missed = new List<MailboxTurn>();
            foreach (SentGame game in games ?? Enumerable.Empty<SentGame>())
            {
                DateTimeOffset copied;
                //No copy of the send on this server (it went from another account, or the provider keeps none), or
                //the newest copy is from an earlier send: either way the mailbox cannot say what came after it
                if (!lastCopy.TryGetValue(game.GameKey, out copied) || copied < game.Sent - SentCopyTolerance)
                {
                    continue;
                }

                HashSet<string> players = new HashSet<string>((game.Players ?? new List<string>()).Where(address => !own.Contains(address)), StringComparer.OrdinalIgnoreCase);
                MailboxTurn newest = inbox
                    .Where(turn => turn.GameKey == game.GameKey && players.Contains(turn.From ?? string.Empty) && turn.Arrived.Value > copied)
                    .OrderByDescending(turn => turn.Arrived.Value)
                    .FirstOrDefault();
                if (newest != null)
                {
                    missed.Add(newest);
                }
            }

            return missed;
        }

        /// <summary>The game a save file belongs to: the name up to the first dot, as the game itself truncates it.</summary>
        public static string GameKey(string fileName)
        {
            string name = (fileName ?? string.Empty).Trim();
            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                name = name.Substring(0, dot);
            }
            return name.Trim().ToLowerInvariant();
        }

        /// <summary>The file name of the first save game part, or null when the message has none.</summary>
        public static string SaveGameName(IEnumerable<BodyPartBasic> parts)
        {
            if (parts == null)
            {
                return null;
            }
            BodyPartBasic part = parts.FirstOrDefault(p => !string.IsNullOrEmpty(p.FileName) && ASGFileInfo.IsAsg(p.FileName));
            return part != null ? part.FileName : null;
        }

        /// <summary>True when any part of the message is a save game, by its file name.</summary>
        public static bool HasSaveGame(IEnumerable<BodyPartBasic> parts)
        {
            return SaveGameName(parts) != null;
        }

        /// <summary>The From address of a received message, or the To and Cc addresses of a sent one.</summary>
        public static IEnumerable<string> AddressesOf(Envelope envelope, bool outgoing)
        {
            if (envelope == null)
            {
                return Enumerable.Empty<string>();
            }

            IEnumerable<MailboxAddress> mailboxes = outgoing
                ? envelope.To.Mailboxes.Concat(envelope.Cc.Mailboxes)
                : envelope.From.Mailboxes;

            return mailboxes
                .Select(mailbox => mailbox.Address)
                .Where(address => !string.IsNullOrEmpty(address))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        private static IEnumerable<string> Split(string list)
        {
            return string.IsNullOrEmpty(list)
                ? Enumerable.Empty<string>()
                : list.Split(ConfigFramework.ActivityList.AddressSeparator).Select(address => address.Trim()).Where(address => address.Length > 0);
        }
    }
}
