using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Helpers;
using AowEmailWrapper.Pollers;
using MailKit;
using MailKit.Search;
using MimeKit;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// A wrapper notice that the player's phone or webmail opened before the Wrapper checked is already
    /// marked as read. The scan used to look at unread mail only, so such a notice stayed in the inbox for
    /// good. These tests run the real IMAP scan against an in-memory inbox.
    /// </summary>
    [Collection(nameof(ProcessStateCollection))]
    public class ReadWrapperMessageTests : IDisposable
    {
        private readonly string _root;
        private readonly string _oldAppData;

        public ReadWrapperMessageTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AowEmailWrapper.Tests", Guid.NewGuid().ToString("N"));
            string appData = Path.Combine(_root, "AppData");
            Directory.CreateDirectory(appData);
            _oldAppData = Environment.GetEnvironmentVariable("APPDATA");
            Environment.SetEnvironmentVariable("APPDATA", appData);
            ForgetAppDataFolders();
            Assert.StartsWith(_root, AppDataHelper.Root.FullName, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPDATA", _oldAppData);
            ForgetAppDataFolders();
            try { Directory.Delete(_root, true); } catch { }
        }

        private static void ForgetAppDataFolders()
        {
            foreach (FieldInfo field in typeof(AppDataHelper).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Where(field => field.FieldType == typeof(DirectoryInfo)))
            {
                field.SetValue(null, null);
            }
        }

        private static MimeMessage Notice(string game, string from)
        {
            MimeMessage message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(from));
            message.To.Add(MailboxAddress.Parse("player@example.com"));
            message.Subject = TurnQuery.SubjectPrefix + " " + game;
            message.Headers.Add(TurnQuery.KindHeader, TurnQuery.KindReply);
            message.Headers.Add(TurnQuery.GameHeader, game);
            message.Body = new TextPart("plain") { Text = "Automatic reply from the Age of Wonders Email Wrapper." };
            return message;
        }

        private static MimeMessage Ordinary(string subject)
        {
            MimeMessage message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse("friend@example.com"));
            message.To.Add(MailboxAddress.Parse("player@example.com"));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = "Hello" };
            return message;
        }

        private static ImapPoller Poller(List<string> handled)
        {
            AowGameManager games = new AowGameManager(null, new AowGame[0], new GamesConfigValues());
            ImapPoller poller = new ImapPoller("imap.example.com", 993, SSLType.SSL, "player@example.com", "secret", 10, EmailSaveFolder.EmailIn, games);
            poller.OnWrapperMessage += (sender, message) => handled.Add(message.Subject);
            return poller;
        }

        [Fact]
        public void A_wrapper_message_already_read_is_handled_and_removed_and_other_read_mail_is_left()
        {
            FakeInbox inbox = FakeInbox.Create();
            DateTime now = DateTime.Now;
            inbox.Add(1, Ordinary("Lunch on Friday"), seen: true, now.AddHours(-5));
            inbox.Add(2, Notice("Hamardis 2026.asg", "flecnick@example.com"), seen: true, now.AddHours(-4));
            inbox.Add(3, Ordinary("AoW Wrapper: a question from a person, not a wrapper"), seen: true, now.AddHours(-3));
            inbox.Add(4, Notice("Heulax 26.asg", "danaloy@example.com"), seen: false, now.AddHours(-2));

            List<string> handled = new List<string>();
            Poller(handled).ScanMailbox(inbox.Folder);

            Assert.Equal(new[] { "AoW Wrapper: Hamardis 2026.asg", "AoW Wrapper: Heulax 26.asg" }, handled);
            Assert.Equal(new uint[] { 1, 3 }, inbox.Uids);
        }

        [Fact]
        public void A_read_wrapper_message_older_than_the_look_back_window_is_left()
        {
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, Notice("Swolterwood 1v1.asg", "old@example.com"), seen: true, DateTime.Now.AddDays(-(ImapPoller.LookBackDays + 5)));

            List<string> handled = new List<string>();
            Poller(handled).ScanMailbox(inbox.Folder);

            Assert.Empty(handled);
            Assert.Equal(new uint[] { 1 }, inbox.Uids);
        }

        /// <summary>
        /// An inbox in memory, behind MailKit's folder interface: the searches, summaries, downloads, flags,
        /// expunges and opening and closing the scan uses. Anything else it is asked fails the test by name.
        /// Folders created on one server close each other when opened, as an IMAP connection has one open folder.
        /// </summary>
        public class FakeInbox : DispatchProxy
        {
            private sealed class Stored
            {
                public uint Uid;
                public MimeMessage Message;
                public MessageFlags Flags;
                public DateTime Delivered;
            }

            private readonly List<Stored> _messages = new List<Stored>();
            private List<FakeInbox> _server;
            private bool _open;

            public IMailFolder Folder { get; private set; }

            public uint[] Uids { get { return _messages.Select(stored => stored.Uid).ToArray(); } }

            public bool IsOpen { get { return _open; } }

            /// <summary>The inbox, open as the poller leaves it.</summary>
            public static FakeInbox Create()
            {
                return Create(null, true);
            }

            /// <summary>Another folder on the same server as <paramref name="sameServer"/>, closed.</summary>
            public static FakeInbox CreateBeside(FakeInbox sameServer)
            {
                return Create(sameServer, false);
            }

            private static FakeInbox Create(FakeInbox sameServer, bool open)
            {
                IMailFolder folder = Create<IMailFolder, FakeInbox>();
                FakeInbox inbox = (FakeInbox)(object)folder;
                inbox.Folder = folder;
                inbox._open = open;
                inbox._server = sameServer != null ? sameServer._server : new List<FakeInbox>();
                inbox._server.Add(inbox);
                return inbox;
            }

            public void Add(uint uid, MimeMessage message, bool seen, DateTime delivered)
            {
                _messages.Add(new Stored { Uid = uid, Message = message, Flags = seen ? MessageFlags.Seen : MessageFlags.None, Delivered = delivered });
            }

            public bool IsSeen(uint uid)
            {
                return _messages.First(stored => stored.Uid == uid).Flags.HasFlag(MessageFlags.Seen);
            }

            private Stored Find(UniqueId uid)
            {
                return _messages.First(stored => stored.Uid == uid.Id);
            }

            private static bool Matches(Stored stored, SearchQuery query)
            {
                switch (query.Term)
                {
                    case SearchTerm.All: return true;
                    case SearchTerm.Seen: return stored.Flags.HasFlag(MessageFlags.Seen);
                    case SearchTerm.NotSeen: return !stored.Flags.HasFlag(MessageFlags.Seen);
                    case SearchTerm.DeliveredAfter: return stored.Delivered.Date > ((DateSearchQuery)query).Date.Date;
                    case SearchTerm.SubjectContains: return (stored.Message.Subject ?? string.Empty).IndexOf(((TextSearchQuery)query).Text, StringComparison.OrdinalIgnoreCase) >= 0;
                    case SearchTerm.FromContains: return stored.Message.From.Mailboxes.Any(mailbox => mailbox.Address.IndexOf(((TextSearchQuery)query).Text, StringComparison.OrdinalIgnoreCase) >= 0);
                    case SearchTerm.And: return Matches(stored, ((BinarySearchQuery)query).Left) && Matches(stored, ((BinarySearchQuery)query).Right);
                    case SearchTerm.Or: return Matches(stored, ((BinarySearchQuery)query).Left) || Matches(stored, ((BinarySearchQuery)query).Right);
                    default: throw new NotSupportedException("search term " + query.Term);
                }
            }

            /// <summary>The structure the server reports for a message: its parts, with their types and file names.</summary>
            private static BodyPart Structure(MimeEntity entity)
            {
                Multipart multipart = entity as Multipart;
                if (multipart != null)
                {
                    BodyPartMultipart parts = new BodyPartMultipart { ContentType = multipart.ContentType };
                    foreach (MimeEntity child in multipart)
                    {
                        parts.BodyParts.Add(Structure(child));
                    }
                    return parts;
                }
                return new BodyPartBasic { ContentType = entity.ContentType, ContentDisposition = entity.ContentDisposition };
            }

            private static Envelope EnvelopeOf(MimeMessage message)
            {
                Envelope envelope = new Envelope { Subject = message.Subject, Date = message.Date };
                envelope.From.AddRange(message.From.Mailboxes);
                envelope.To.AddRange(message.To.Mailboxes);
                return envelope;
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                Type[] parameters = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
                switch (method.Name)
                {
                    case "get_IsOpen":
                        return _open;

                    case "Open" when parameters[0] == typeof(FolderAccess):
                        foreach (FakeInbox other in _server)
                        {
                            other._open = false;
                        }
                        _open = true;
                        return args[0];

                    case "Close":
                        _open = false;
                        return null;

                    case "Search" when parameters.Length == 2 && parameters[0] == typeof(SearchQuery):
                        Assert.True(_open, "searched a folder that is not open");
                        return (IList<UniqueId>)_messages.Where(stored => Matches(stored, (SearchQuery)args[0])).Select(stored => new UniqueId(stored.Uid)).ToList();

                    case "Fetch" when parameters[0] == typeof(IList<UniqueId>):
                        Assert.True(_open, "fetched from a folder that is not open");
                        return (IList<IMessageSummary>)((IList<UniqueId>)args[0]).Select(uid =>
                        {
                            Stored stored = Find(uid);
                            return (IMessageSummary)new MessageSummary(0)
                            {
                                UniqueId = uid,
                                Flags = stored.Flags,
                                Headers = stored.Message.Headers,
                                Envelope = EnvelopeOf(stored.Message),
                                Body = Structure(stored.Message.Body),
                                InternalDate = new DateTimeOffset(stored.Delivered)
                            };
                        }).ToList();

                    case "GetMessage" when parameters[0] == typeof(UniqueId):
                        Assert.True(_open, "downloaded from a folder that is not open");
                        return Find((UniqueId)args[0]).Message;

                    case "Store" when parameters[0] == typeof(IList<UniqueId>) && args[1] is IStoreFlagsRequest request:
                        foreach (UniqueId uid in (IList<UniqueId>)args[0])
                        {
                            Stored stored = Find(uid);
                            stored.Flags = request.Action == StoreAction.Remove ? stored.Flags & ~request.Flags : stored.Flags | request.Flags;
                        }
                        return method.ReturnType == typeof(void) ? null : (object)new List<UniqueId>();

                    case "Store" when parameters[0] == typeof(UniqueId) && args[1] is IStoreFlagsRequest single:
                        Stored one = Find((UniqueId)args[0]);
                        one.Flags = single.Action == StoreAction.Remove ? one.Flags & ~single.Flags : one.Flags | single.Flags;
                        return method.ReturnType == typeof(bool) ? (object)true : null;

                    case "Expunge":
                        HashSet<uint> only = args.Length > 1 && args[0] is IList<UniqueId> uids ? new HashSet<uint>(uids.Select(uid => uid.Id)) : null;
                        _messages.RemoveAll(stored => stored.Flags.HasFlag(MessageFlags.Deleted) && (only == null || only.Contains(stored.Uid)));
                        return null;

                    default:
                        throw new NotImplementedException("the fake inbox does not do " + method.Name + "(" + string.Join(", ", parameters.Select(type => type.Name)) + ")");
                }
            }
        }
    }
}
