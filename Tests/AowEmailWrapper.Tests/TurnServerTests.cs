using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Helpers;
using MimeKit;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// The turn server: Wrappers record what they did with a game's turn, and anyone in the game reads the
    /// records to see who has it, without a message to anyone. The hosted server is reached over a real
    /// socket on this machine, the way Tailscale Funnel's proxy reaches it.
    /// </summary>
    public class TurnServerTests
    {
        private const string Game = "Highpass (Dave, Fred).asg";
        private const string Me = "me@example.com";
        private const string Bob = "bob@example.com";
        private const string Carol = "carol@example.org";
        private const string Dave = "dave@example.net";
        private static readonly DateTimeOffset Day1 = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.Zero);

        private static TurnRecord Record(string player, ActivityState status, int day, string sentTo = null, string game = Game)
        {
            return TurnRecord.Create(game, player, status, Day1.AddDays(day - 1), sentTo, "4");
        }

        #region Records

        [Fact]
        public void A_record_survives_json_and_only_sensible_records_are_valid()
        {
            TurnRecord record = TurnRecord.FromJson(Record(Bob, ActivityState.Sent, 2, Carol).ToJson());

            Assert.True(record.IsValid);
            Assert.Equal(ActivityState.Sent, record.State);
            Assert.Equal(Carol, record.SentTo);
            Assert.Equal(Day1.AddDays(1), record.When);

            Assert.Equal(ActivityState.Sent, Record(Bob, ActivityState.Pending, 2).State);
            Assert.False(Record("not an address", ActivityState.Sent, 2).IsValid);
            Assert.False(Record(Bob, ActivityState.New, 2).IsValid);
            Assert.False(Record(Bob, ActivityState.Sent, 2, game: new string('x', TurnRecord.MaxGameLength + 1)).IsValid);
            Assert.False(new TurnRecord { Game = Game, Player = Bob, Status = "Sent", Date = "yesterday" }.IsValid);
            Assert.DoesNotContain("isValid", record.ToJson());
            Assert.Null(TurnRecord.FromJson("not json"));
            Assert.Empty(TurnRecord.ListFromJson("{\"game\":\"x\"}"));
        }

        [Fact]
        public void Only_https_addresses_or_this_machine_are_used()
        {
            Assert.Equal("https://host.example.ts.net/aow-turns", TurnServerClient.Normalise(" https://host.example.ts.net/aow-turns/ "));
            Assert.Equal("http://127.0.0.1:49253", TurnServerClient.Normalise("http://127.0.0.1:49253/"));
            Assert.Null(TurnServerClient.Normalise("http://192.168.1.1/records"));
            Assert.Null(TurnServerClient.Normalise("https://host.example/x?game=y"));
            Assert.Null(TurnServerClient.Normalise("https://user:pass@host.example/"));
            Assert.Null(TurnServerClient.Normalise("file:///c:/windows"));
            Assert.Null(TurnServerClient.Normalise("not an address"));
            Assert.Null(TurnServerClient.Normalise(null));
        }

        [Fact]
        public void A_turn_email_names_its_turn_server()
        {
            MimeMessage email = new MimeMessage();
            MailHelper.SetTurnServer(email, "https://host.example.ts.net/aow-turns/");
            using (MemoryStream stream = new MemoryStream())
            {
                email.WriteTo(stream);
                stream.Position = 0;
                email = MimeMessage.Load(stream);
            }
            Assert.Equal("https://host.example.ts.net/aow-turns", MailHelper.GetTurnServer(email));

            //An address a Wrapper would not use is neither sent nor taken
            MailHelper.SetTurnServer(email, "http://192.168.1.1");
            Assert.Null(email.Headers[MailHelper.TurnServerHeaderName]);
            email.Headers.Add(MailHelper.TurnServerHeaderName, "http://192.168.1.1");
            Assert.Null(MailHelper.GetTurnServer(email));
        }

        [Fact]
        public void A_games_turn_server_survives_the_activity_log()
        {
            Activity activity = new Activity(ActivityState.Sent, AowGameType.Aow1, Game, "Highpass", "4") { TurnServer = "https://host.example.ts.net/aow-turns" };

            XmlSerializer serializer = new XmlSerializer(typeof(Activity));
            string xml;
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, activity);
                xml = writer.ToString();
            }
            using (StringReader reader = new StringReader(xml))
            {
                Assert.Equal(activity.TurnServer, ((Activity)serializer.Deserialize(reader)).TurnServer);
            }
        }

        #endregion

        #region Store

        [Fact]
        public void The_store_keeps_one_record_per_player_and_survives_a_restart()
        {
            string path = Path.Combine(TestEnvironment.TestAppData, "store-" + Guid.NewGuid().ToString("N") + ".json");
            TurnServerStore store = new TurnServerStore(path);

            Assert.True(store.Put(Record(Bob, ActivityState.Received, 1)));
            Assert.True(store.Put(Record("BOB@example.com", ActivityState.Sent, 2, Carol)));
            Assert.True(store.Put(Record(Carol, ActivityState.Received, 2)));
            Assert.True(store.Put(Record(Carol, ActivityState.Received, 2, game: "Other.asg")));
            Assert.False(store.Put(Record("nobody", ActivityState.Sent, 2)));

            List<TurnRecord> records = new TurnServerStore(path).ForGame(Game.ToUpperInvariant());
            Assert.Equal(2, records.Count);
            Assert.Equal(ActivityState.Sent, records.Single(r => TurnQuery.SameAddress(r.Player, Bob)).State);
        }

        [Fact]
        public void Junk_posted_for_a_game_pushes_out_its_oldest_records_only()
        {
            TurnServerStore store = new TurnServerStore(null);
            store.Put(Record(Carol, ActivityState.Received, 1, game: "Other.asg"));
            for (int i = 0; i < TurnServerStore.MaxRecordsPerGame + 10; i++)
            {
                store.Put(Record("junk" + i + "@example.com", ActivityState.Sent, 1));
            }

            Assert.Equal(TurnServerStore.MaxRecordsPerGame, store.ForGame(Game).Count);
            Assert.DoesNotContain(store.ForGame(Game), r => r.Player == "junk0@example.com");
            Assert.Single(store.ForGame("Other.asg"));
        }

        #endregion

        #region Hosted server

        private static TurnServerHost StartHost(TurnServerStore store)
        {
            TurnServerHost host = new TurnServerHost(store);
            host.Start(0);
            return host;
        }

        [Fact]
        public async Task Records_posted_through_the_client_are_read_back_by_game()
        {
            TurnServerStore store = new TurnServerStore(null);
            using (TurnServerHost host = StartHost(store))
            {
                TurnServerClient client = new TurnServerClient(null);
                //Funnel publishes the server under a path, which may or may not reach the server: both work
                string server = host.LocalAddress + "/aow-turns";

                await client.PostAsync(server, Record(Bob, ActivityState.Sent, 2, Carol));
                await client.PostAsync(host.LocalAddress, Record(Carol, ActivityState.Received, 2));
                await client.PostAsync(server, Record(Dave, ActivityState.Received, 1, game: "Other.asg"));

                Assert.Equal(0, client.PendingCount);
                List<TurnRecord> records = await client.GetAsync(server, Game);
                Assert.Equal(new[] { Bob, Carol }, records.Select(r => r.Player).OrderBy(p => p));
                Assert.True(await client.IsTurnServerAsync(server));
                Assert.False(await client.IsTurnServerAsync("http://127.0.0.1:1"));
            }
        }

        [Fact]
        public async Task A_record_waits_while_the_server_is_down_and_goes_when_it_is_back()
        {
            string outbox = Path.Combine(TestEnvironment.TestAppData, "outbox-" + Guid.NewGuid().ToString("N") + ".json");
            int port = FreePort();
            string server = "http://127.0.0.1:" + port;

            TurnServerClient client = new TurnServerClient(outbox);
            await client.PostAsync(server, Record(Bob, ActivityState.Received, 1));
            await client.PostAsync(server, Record(Bob, ActivityState.Sent, 2, Carol));
            Assert.Equal(1, client.PendingCount);

            //The Wrapper was restarted meanwhile: the outbox file still has it
            TurnServerClient restarted = new TurnServerClient(outbox);
            Assert.Equal(1, restarted.PendingCount);

            TurnServerStore store = new TurnServerStore(null);
            TurnServerHost host = new TurnServerHost(store);
            host.Start(port);
            try
            {
                await restarted.FlushAsync();
                Assert.Equal(0, restarted.PendingCount);
                Assert.Equal(ActivityState.Sent, Assert.Single(store.ForGame(Game)).State);
            }
            finally
            {
                host.Stop();
            }
        }

        [Fact]
        public async Task The_server_refuses_what_it_should_not_take()
        {
            using (TurnServerHost host = StartHost(new TurnServerStore(null)))
            using (HttpClient http = new HttpClient())
            {
                string records = host.LocalAddress + TurnServerHost.RecordsPath;

                HttpResponseMessage junk = await http.PostAsync(records, new StringContent("{\"game\":\"x\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);

                HttpResponseMessage large = await http.PostAsync(records, new StringContent(new string('x', TurnServerHost.MaxBodyBytes + 1)));
                Assert.Equal((HttpStatusCode)413, large.StatusCode);

                HttpResponseMessage noGame = await http.GetAsync(records);
                Assert.Equal(HttpStatusCode.BadRequest, noGame.StatusCode);

                HttpResponseMessage delete = await http.DeleteAsync(records);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);

                string banner = await http.GetStringAsync(host.LocalAddress + "/aow-turns/");
                Assert.Contains(TurnServerHost.Banner, banner);
            }
        }

        [Fact]
        public async Task A_client_that_asks_too_often_is_slowed_without_slowing_others()
        {
            using (TurnServerHost host = StartHost(new TurnServerStore(null)))
            using (HttpClient http = new HttpClient())
            {
                string url = host.LocalAddress + TurnServerHost.RecordsPath + "?game=" + Uri.EscapeDataString(Game);
                HttpStatusCode last = HttpStatusCode.OK;
                for (int i = 0; i <= TurnServerHost.RequestsPerMinute; i++)
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        //What Funnel's proxy adds: the client's claim first, the address it saw last
                        request.Headers.Add("X-Forwarded-For", "1.2.3.4, 203.0.113.9");
                        last = (await http.SendAsync(request)).StatusCode;
                    }
                }
                Assert.Equal((HttpStatusCode)429, last);

                using (HttpRequestMessage other = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    //Same forged claim, different real address: not the one being slowed
                    other.Headers.Add("X-Forwarded-For", "203.0.113.9, 198.51.100.7");
                    Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(other)).StatusCode);
                }
            }
        }

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        #endregion

        #region Who has the turn

        /// <summary>A turn the player sent to Bob on day 1, in a game of the player, Bob, Carol and Dave.</summary>
        private static Activity SentToBob()
        {
            Activity activity = new Activity(ActivityState.Sent, AowGameType.Aow1, Game, "Highpass", "4");
            activity.Recipients = Bob;
            activity.Players = string.Join(";", Me, Bob, Carol, Dave);
            activity.DateTicks = Day1.LocalDateTime.Ticks.ToString();
            return activity;
        }

        [Fact]
        public void The_player_whose_wrapper_received_it_last_holds_it()
        {
            Activity activity = SentToBob();
            TurnQuery.ApplyServerRecords(activity, new[]
            {
                Record(Bob, ActivityState.Received, 1),
                Record(Bob, ActivityState.Sent, 2, Carol),
                Record(Carol, ActivityState.Received, 2),
            }, new[] { Me });

            Assert.Equal(Carol, activity.Holder);
            Assert.Null(activity.LikelyHolder);
        }

        [Fact]
        public void A_player_without_a_record_after_a_send_to_them_probably_has_it()
        {
            Activity activity = SentToBob();
            TurnState newest = TurnQuery.ApplyServerRecords(activity, new[]
            {
                Record(Bob, ActivityState.Sent, 2, Carol),
                Record(Carol, ActivityState.Sent, 3, Dave),
            }, new[] { Me });

            Assert.Null(activity.Holder);
            Assert.Equal(Dave, activity.LikelyHolder);
            Assert.Equal(Carol, newest.Responder);
        }

        [Fact]
        public void A_receipt_older_than_the_newest_send_is_left_over_from_an_earlier_round()
        {
            //Dave received it on day 1 of an earlier round and his send was never recorded; since then the
            //turn went round again and the player sent it to Bob on day 4
            Activity activity = SentToBob();
            activity.DateTicks = Day1.AddDays(3).LocalDateTime.Ticks.ToString();
            TurnQuery.ApplyServerRecords(activity, new[] { Record(Dave, ActivityState.Received, 1) }, new[] { Me });

            Assert.Null(activity.Holder);
            Assert.Equal(Bob, activity.LikelyHolder);
        }

        [Fact]
        public void The_players_own_records_and_strangers_are_left_out()
        {
            Activity activity = SentToBob();
            TurnQuery.ApplyServerRecords(activity, new[]
            {
                Record(Me, ActivityState.Received, 5),
                Record("stranger@example.com", ActivityState.Received, 5),
                Record(Bob, ActivityState.Received, 5, game: "Other.asg"),
            }, new[] { Me });

            Assert.Null(activity.Holder);
            Assert.Empty(activity.Answers);
            Assert.Equal(Bob, activity.LikelyHolder);
        }

        [Fact]
        public void The_server_replaces_what_older_answers_said()
        {
            Activity activity = SentToBob();
            TurnQuery.RecordWhereabouts(activity, new TurnState { Responder = Carol, Status = ActivityState.Received, Date = Day1 });
            Assert.Equal(Carol, activity.Holder);

            TurnQuery.ApplyServerRecords(activity, new[] { Record(Bob, ActivityState.Received, 2) }, new[] { Me });

            Assert.Equal(Bob, activity.Holder);
            Assert.Equal(Bob, Assert.Single(activity.Answers).Responder);
        }

        [Fact]
        public void The_player_column_says_who_has_the_turn()
        {
            Activity waiting = new Activity(ActivityState.Received, AowGameType.Aow1, Game, "Highpass", "4");
            Assert.False(string.IsNullOrEmpty(Controls.ActivityListView.PlayerLabel(waiting)));

            //Before anyone else's Wrapper is heard from, whoever it was sent to
            Activity sent = SentToBob();
            Assert.Contains(Bob, Controls.ActivityListView.PlayerLabel(sent));
            Assert.NotEqual(Bob, Controls.ActivityListView.PlayerLabel(sent));

            TurnQuery.ApplyServerRecords(sent, new[] { Record(Bob, ActivityState.Sent, 2, Carol), Record(Carol, ActivityState.Received, 2) }, new[] { Me });
            Assert.Equal(Carol, Controls.ActivityListView.PlayerLabel(sent));

            TurnQuery.ApplyServerRecords(sent, new[] { Record(Bob, ActivityState.Sent, 2, Carol), Record(Carol, ActivityState.Sent, 3, Dave) }, new[] { Me });
            Assert.Contains(Dave, Controls.ActivityListView.PlayerLabel(sent));

            sent.Status = ActivityState.Ended;
            Assert.Equal(string.Empty, Controls.ActivityListView.PlayerLabel(sent));
        }

        #endregion
    }
}
