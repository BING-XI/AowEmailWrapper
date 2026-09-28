using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AowEmailWrapper.ConfigFramework;
using MimeKit;
using MimeKit.Utils;

namespace AowEmailWrapper.Classes
{
    /// <summary>What a wrapper says about a turn when asked.</summary>
    public class TurnState
    {
        public string Game { get; set; }
        public string QueryId { get; set; }
        public string Responder { get; set; }
        public ActivityState Status { get; set; }
        public DateTimeOffset? Date { get; set; }
        public string SentTo { get; set; }

        /// <summary>True when the responder has the turn and has not sent it on.</summary>
        public bool Holds
        {
            get { return Status == ActivityState.Received; }
        }
    }

    /// <summary>A turn passed from one player to another, as the answers report it.</summary>
    public class TurnSend
    {
        /// <summary>Who sent it; null when it was the player, whose own address is not known here.</summary>
        public string From { get; set; }
        public string To { get; set; }
        public DateTimeOffset Date { get; set; }
    }

    /// <summary>A wrapper asking who has a turn.</summary>
    public class TurnQueryRequest
    {
        public string Game { get; set; }
        public string QueryId { get; set; }
        public string From { get; set; }
    }

    /// <summary>
    /// Wrapper-to-wrapper mail: one wrapper asks every player of a game where the turn is, and each
    /// wrapper that has that game answers with what it knows. Both the question and the answer are
    /// small plain-text emails marked with headers; a wrapper that reads one deletes it from the mailbox.
    /// </summary>
    public static class TurnQuery
    {
        public const string KindHeader = "X-AowEmailWrapper-Kind";
        public const string GameHeader = "X-AowEmailWrapper-Game";
        public const string IdHeader = "X-AowEmailWrapper-Query-Id";
        public const string StateHeader = "X-AowEmailWrapper-State";
        public const string StateDateHeader = "X-AowEmailWrapper-State-Date";
        public const string SentToHeader = "X-AowEmailWrapper-Sent-To";

        public const string KindQuery = "query";
        public const string KindReply = "reply";

        private const string QuerySubjectTemplate = "AoW Wrapper: who has {0}?";
        private const string ReplySubjectTemplate = "AoW Wrapper: {0}";

        #region Recognising

        public static bool IsWrapperMessage(HeaderList headers)
        {
            string kind = headers != null ? headers[KindHeader] : null;
            return IsQueryKind(kind) || IsReplyKind(kind);
        }

        public static bool IsWrapperMessage(MimeMessage message)
        {
            return message != null && IsWrapperMessage(message.Headers);
        }

        public static bool IsQuery(MimeMessage message)
        {
            return message != null && IsQueryKind(message.Headers[KindHeader]);
        }

        public static bool IsReply(MimeMessage message)
        {
            return message != null && IsReplyKind(message.Headers[KindHeader]);
        }

        private static bool IsQueryKind(string kind)
        {
            return string.Equals((kind ?? string.Empty).Trim(), KindQuery, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReplyKind(string kind)
        {
            return string.Equals((kind ?? string.Empty).Trim(), KindReply, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Building

        public static string NewQueryId()
        {
            return Guid.NewGuid().ToString("N");
        }

        public static MimeMessage BuildQuery(string from, string to, string gameFileName, string queryId)
        {
            MimeMessage message = new MimeMessage();
            message.From.Add(new MailboxAddress(string.Empty, from));
            message.To.Add(new MailboxAddress(string.Empty, to));
            message.Subject = string.Format(QuerySubjectTemplate, gameFileName);
            message.MessageId = MimeUtils.GenerateMessageId();
            message.Headers.Add(KindHeader, KindQuery);
            message.Headers.Add(GameHeader, gameFileName);
            message.Headers.Add(IdHeader, queryId);
            //Nobody should auto-reply to wrapper chatter but another wrapper
            message.Headers.Add("Auto-Submitted", "auto-generated");
            message.Headers.Add("X-Auto-Response-Suppress", "All");

            StringBuilder body = new StringBuilder();
            body.AppendFormat("The Age of Wonders Email Wrapper of {0} is asking who has the turn for '{1}'.", from, gameFileName);
            body.AppendLine();
            body.AppendLine();
            body.AppendLine("If you run the Wrapper it answers automatically and removes this message. Otherwise you can reply by hand or ignore it.");
            message.Body = new TextPart("plain") { Text = body.ToString() };

            return message;
        }

        public static MimeMessage BuildReply(TurnQueryRequest query, string from, TurnState state)
        {
            MimeMessage message = new MimeMessage();
            message.From.Add(new MailboxAddress(string.Empty, from));
            message.To.Add(new MailboxAddress(string.Empty, query.From));
            message.Subject = string.Format(ReplySubjectTemplate, query.Game);
            message.MessageId = MimeUtils.GenerateMessageId();
            message.Headers.Add(KindHeader, KindReply);
            message.Headers.Add(GameHeader, query.Game);
            message.Headers.Add(IdHeader, query.QueryId ?? string.Empty);
            message.Headers.Add(StateHeader, state.Status.ToString());
            if (state.Date.HasValue)
            {
                message.Headers.Add(StateDateHeader, state.Date.Value.ToString("o", CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(state.SentTo))
            {
                message.Headers.Add(SentToHeader, state.SentTo);
            }
            message.Headers.Add("Auto-Submitted", "auto-replied");
            message.Headers.Add("X-Auto-Response-Suppress", "All");

            state.Responder = from;
            message.Body = new TextPart("plain") { Text = "Automatic reply from the Age of Wonders Email Wrapper." + Environment.NewLine + Environment.NewLine + Describe(state, query.Game) };

            return message;
        }

        #endregion

        #region Parsing

        public static TurnQueryRequest ParseQuery(MimeMessage message)
        {
            if (!IsQuery(message))
            {
                return null;
            }

            TurnQueryRequest query = new TurnQueryRequest
            {
                Game = Clean(message.Headers[GameHeader]),
                QueryId = Clean(message.Headers[IdHeader]),
                From = Helpers.MailHelper.GetFromAddress(message)
            };

            return string.IsNullOrEmpty(query.Game) || string.IsNullOrEmpty(query.QueryId) || string.IsNullOrEmpty(query.From) ? null : query;
        }

        public static TurnState ParseReply(MimeMessage message)
        {
            if (!IsReply(message))
            {
                return null;
            }

            TurnState state = new TurnState
            {
                Game = Clean(message.Headers[GameHeader]),
                QueryId = Clean(message.Headers[IdHeader]),
                Responder = Helpers.MailHelper.GetFromAddress(message),
                SentTo = Clean(message.Headers[SentToHeader])
            };

            ActivityState status;
            state.Status = Enum.TryParse(Clean(message.Headers[StateHeader]), true, out status) ? status : ActivityState.None;

            DateTimeOffset date;
            if (DateTimeOffset.TryParse(Clean(message.Headers[StateDateHeader]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date))
            {
                state.Date = date;
            }

            return string.IsNullOrEmpty(state.Game) || string.IsNullOrEmpty(state.Responder) ? null : state;
        }

        private static string Clean(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        #endregion

        #region Answering

        /// <summary>
        /// A query is answered only when the asker is someone the player has exchanged turns with,
        /// the player has that game, and the asker is one of its players. Anyone else learns nothing,
        /// not even whether the game exists here.
        /// </summary>
        public static bool MayAnswer(TurnQueryRequest query, ActivityList log, IEnumerable<string> ownAddresses)
        {
            if (query == null || log == null || string.IsNullOrEmpty(query.From))
            {
                return false;
            }

            if ((ownAddresses ?? Enumerable.Empty<string>()).Any(own => SameAddress(own, query.From)))
            {
                //A copy of our own query (BCC myself) is not a question
                return false;
            }

            if (!log.IsKnownAddress(query.From))
            {
                return false;
            }

            Activity activity = log.GetLastActivityByFileName(query.Game);
            return activity != null && IsPlayer(activity, query.From);
        }

        /// <summary>True when the address is in the game: listed in the save, sent the turn to us, or was sent it by us.</summary>
        public static bool IsPlayer(Activity activity, string address)
        {
            if (activity == null || string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            return Contains(activity.Players, address) || SameAddress(activity.Sender, address) || Contains(activity.Recipients, address);
        }

        /// <summary>The players of a game to ask, excluding the player's own addresses.</summary>
        public static List<string> PlayersToAsk(Activity activity, IEnumerable<string> ownAddresses)
        {
            List<string> targets = new List<string>();
            if (activity == null)
            {
                return targets;
            }

            HashSet<string> own = new HashSet<string>((ownAddresses ?? Enumerable.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> candidates = Split(activity.Players).Concat(Split(activity.Recipients)).Concat(new[] { activity.Sender ?? string.Empty });

            foreach (string candidate in candidates.Select(c => c.Trim()).Where(c => c.Contains("@")))
            {
                if (!own.Contains(candidate) && !targets.Any(t => SameAddress(t, candidate)))
                {
                    targets.Add(candidate);
                }
            }

            return targets;
        }

        public static TurnState StateOf(Activity activity, string game, string queryId)
        {
            TurnState state = new TurnState { Game = game, QueryId = queryId, Status = ActivityState.None };
            if (activity == null)
            {
                return state;
            }

            state.Status = activity.Status;
            long ticks;
            if (long.TryParse(activity.DateTicks, out ticks))
            {
                state.Date = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Local));
            }
            if (activity.Status == ActivityState.Sent)
            {
                state.SentTo = activity.Recipients ?? string.Empty;
            }
            return state;
        }

        #endregion

        #region Whereabouts

        /// <summary>"bob@example.com holds it since 5 Sep 2026" style line for one responder.</summary>
        public static string Describe(TurnState state, string game)
        {
            string when = state.Date.HasValue ? state.Date.Value.LocalDateTime.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture) : "an unknown date";
            switch (state.Status)
            {
                case ActivityState.Received:
                    return string.Format("{0} has held '{1}' since {2}", state.Responder, game, when);
                case ActivityState.Sent:
                case ActivityState.Pending:
                    return string.Format("{0} sent '{1}' to {2} on {3}", state.Responder, game, string.IsNullOrEmpty(state.SentTo) ? "someone" : state.SentTo, when);
                case ActivityState.Ended:
                    return string.Format("{0} has '{1}' marked as ended", state.Responder, game);
                default:
                    return string.Format("{0} does not know about '{1}'", state.Responder, game);
            }
        }

        public const string WhereaboutsSeparator = " | ";

        /// <summary>Replaces what was last heard from this responder in the activity's whereabouts line.</summary>
        public static void RecordWhereabouts(Activity activity, TurnState state)
        {
            if (activity == null || state == null || string.IsNullOrEmpty(state.Responder))
            {
                return;
            }

            List<string> lines = (activity.Whereabouts ?? string.Empty)
                .Split(new[] { WhereaboutsSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.StartsWith(state.Responder + " ", StringComparison.OrdinalIgnoreCase))
                .ToList();
            lines.Add(Describe(state, activity.FileName));
            activity.Whereabouts = string.Join(WhereaboutsSeparator, lines);

            activity.Answers.RemoveAll(answer => SameAddress(answer.Responder, state.Responder));
            activity.Answers.Add(new TurnAnswer
            {
                Responder = state.Responder,
                Status = state.Status,
                Date = state.Date.HasValue ? state.Date.Value.ToString("o", CultureInfo.InvariantCulture) : null,
                SentTo = string.IsNullOrEmpty(state.SentTo) ? null : state.SentTo,
            });

            if (state.Holds)
            {
                activity.Holder = state.Responder;
            }
            else if (SameAddress(activity.Holder, state.Responder))
            {
                activity.Holder = null;
            }
        }

        /// <summary>
        /// Who most probably has the turn when no wrapper says it does: the recipient of the newest send
        /// anyone knows about (the answers, and the player's own send), provided that recipient has not
        /// answered. A player without the Wrapper, or whose Wrapper is not running, never answers, so the
        /// turn is usually with them. Null when a wrapper holds it, when nothing was sent, when the newest
        /// send went to several people, or when its recipient answered without claiming it.
        /// </summary>
        public static TurnSend LikelyHolder(Activity activity, IEnumerable<string> ownAddresses)
        {
            if (activity == null || activity.Answers.Any(answer => answer.Status == ActivityState.Received))
            {
                return null;
            }

            List<string> own = (ownAddresses ?? Enumerable.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            List<TurnSend> sends = new List<TurnSend>();
            foreach (TurnAnswer answer in activity.Answers.Where(a => a.Status == ActivityState.Sent || a.Status == ActivityState.Pending))
            {
                DateTimeOffset when;
                if (!string.IsNullOrEmpty(answer.SentTo) &&
                    DateTimeOffset.TryParse(answer.Date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when))
                {
                    sends.Add(new TurnSend { From = answer.Responder, To = answer.SentTo, Date = when });
                }
            }

            //What the player sent counts too: the chain starts there
            long ticks;
            if ((activity.Status == ActivityState.Sent || activity.Status == ActivityState.Pending) &&
                !string.IsNullOrEmpty(activity.Recipients) && long.TryParse(activity.DateTicks, out ticks))
            {
                sends.Add(new TurnSend { From = null, To = activity.Recipients, Date = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Local)) });
            }

            TurnSend newest = sends.OrderByDescending(send => send.Date).FirstOrDefault();
            if (newest == null)
            {
                return null;
            }

            List<string> recipients = Split(newest.To)
                .Select(address => address.Trim())
                .Where(address => address.Contains("@") && !own.Any(mine => SameAddress(mine, address)) && !SameAddress(address, newest.From))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count != 1 || activity.Answers.Any(answer => SameAddress(answer.Responder, recipients[0])))
            {
                return null;
            }

            newest.To = recipients[0];
            return newest;
        }

        #endregion

        #region Address helpers

        public static bool SameAddress(string a, string b)
        {
            return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string list, string address)
        {
            return Split(list).Any(entry => SameAddress(entry, address));
        }

        private static IEnumerable<string> Split(string list)
        {
            return string.IsNullOrEmpty(list) ? Enumerable.Empty<string>() : list.Split(ActivityList.AddressSeparator).Select(s => s.Trim()).Where(s => s.Length > 0);
        }

        #endregion
    }
}
