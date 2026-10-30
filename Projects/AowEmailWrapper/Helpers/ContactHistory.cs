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

        /// <summary>When the player's turn went out, by this computer's clock (local time).</summary>
        public DateTime Sent { get; set; }

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
        /// The save games, already read, that came from <paramref name="senders"/> on or after the day before
        /// <paramref name="since"/>: what a stored turn that never reached the activity log may have arrived in.
        /// Unread mail is left to the poller. Only message structures are fetched; nothing is downloaded.
        /// </summary>
        public static List<MailboxTurn> FindTurnsFrom(IMailFolder inbox, IEnumerable<string> senders, DateTime since, CancellationToken token)
        {
            List<string> from = (senders ?? Enumerable.Empty<string>()).Where(address => !string.IsNullOrWhiteSpace(address)).Select(address => address.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<MailboxTurn> turns = new List<MailboxTurn>();
            if (from.Count == 0)
            {
                return turns;
            }

            //The search goes by date only, by the server's calendar; a day's margin keeps a time zone from cutting it short
            SearchQuery query = SearchQuery.Seen
                .And(SearchQuery.DeliveredAfter(since.Date.AddDays(-1)))
                .And(from.Select(address => (SearchQuery)SearchQuery.FromContains(address)).Aggregate((left, right) => left.Or(right)));
            Collect(inbox, inbox.Search(query, token), false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), turns, token);
            return turns;
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
