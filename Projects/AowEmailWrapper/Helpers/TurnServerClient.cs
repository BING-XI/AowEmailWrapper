using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AowEmailWrapper.Classes;

namespace AowEmailWrapper.Helpers
{
    /// <summary>
    /// Talks to a game's turn server: posts this player's records and reads a game's records. A record
    /// that cannot be delivered waits in an outbox file and goes with the next attempt, so a host whose
    /// PC is off only delays records.
    /// </summary>
    public class TurnServerClient
    {
        public const int MaxOutbox = 500;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        private class PendingRecord
        {
            public string Server { get; set; }
            public TurnRecord Record { get; set; }
        }

        private readonly HttpClient _http;
        private readonly string _outboxPath;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _flushing = new SemaphoreSlim(1, 1);
        private readonly List<PendingRecord> _outbox = new List<PendingRecord>();

        /// <summary>A client whose outbox is kept in the file at outboxPath, or in memory only when it is null.</summary>
        public TurnServerClient(string outboxPath)
            : this(outboxPath, new HttpClient())
        { }

        public TurnServerClient(string outboxPath, HttpClient http)
        {
            _http = http;
            _http.Timeout = Timeout;
            _outboxPath = outboxPath;
            LoadOutbox();
        }

        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return _outbox.Count;
                }
            }
        }

        #region Addresses

        /// <summary>
        /// The server address in its usual form (no trailing slash), or null when it is not one a Wrapper
        /// should talk to. Addresses arrive in other players' emails, so only https is taken, and plain
        /// http only on this machine, where a hosted server is reached before it is published.
        /// </summary>
        public static string Normalise(string address)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out uri))
            {
                return null;
            }
            bool secure = uri.Scheme == Uri.UriSchemeHttps;
            bool local = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
            if ((!secure && !local) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return null;
            }
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        private static Uri RecordsUri(string server, string game)
        {
            string url = server + TurnServerHost.RecordsPath;
            return new Uri(game == null ? url : url + "?game=" + Uri.EscapeDataString(game));
        }

        #endregion

        #region Reading

        /// <summary>The records the server holds for a game. Throws when the server cannot be reached or refuses.</summary>
        public async Task<List<TurnRecord>> GetAsync(string server, string game)
        {
            string normalised = Normalise(server);
            if (normalised == null)
            {
                throw new ArgumentException("Not a turn server address: " + server);
            }

            using (HttpResponseMessage response = await _http.GetAsync(RecordsUri(normalised, game)))
            {
                response.EnsureSuccessStatusCode();
                return TurnRecord.ListFromJson(await response.Content.ReadAsStringAsync())
                    .Where(record => record.IsValid && string.Equals(record.Game, game, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        /// <summary>True when something answers at the address as a turn server does.</summary>
        public async Task<bool> IsTurnServerAsync(string server)
        {
            string normalised = Normalise(server);
            if (normalised == null)
            {
                return false;
            }

            try
            {
                using (HttpResponseMessage response = await _http.GetAsync(normalised + "/"))
                {
                    return response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains(TurnServerHost.Banner);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceInformation("No turn server at {0}: {1}", normalised, ex.Message);
                return false;
            }
        }

        #endregion

        #region Writing

        /// <summary>Queues the record for the server and tries to deliver everything queued.</summary>
        public Task PostAsync(string server, TurnRecord record)
        {
            string normalised = Normalise(server);
            if (normalised == null || record == null || !record.IsValid)
            {
                return Task.CompletedTask;
            }

            lock (_lock)
            {
                //Only the newest record of a player for a game matters, as on the server
                _outbox.RemoveAll(pending => pending.Server == normalised && pending.Record.SameKey(record));
                _outbox.Add(new PendingRecord { Server = normalised, Record = record });
                if (_outbox.Count > MaxOutbox)
                {
                    _outbox.RemoveRange(0, _outbox.Count - MaxOutbox);
                }
                SaveOutbox();
            }

            return FlushAsync();
        }

        /// <summary>Delivers what is queued. A record the server refuses is dropped; one it could not take now is kept.</summary>
        public async Task FlushAsync()
        {
            if (!await _flushing.WaitAsync(0))
            {
                return;
            }

            try
            {
                List<PendingRecord> pending;
                lock (_lock)
                {
                    pending = _outbox.ToList();
                }

                HashSet<string> unreachable = new HashSet<string>();
                foreach (PendingRecord item in pending)
                {
                    if (unreachable.Contains(item.Server))
                    {
                        continue;
                    }

                    bool done;
                    try
                    {
                        using (StringContent content = new StringContent(item.Record.ToJson(), Encoding.UTF8, "application/json"))
                        using (HttpResponseMessage response = await _http.PostAsync(RecordsUri(item.Server, null), content))
                        {
                            int status = (int)response.StatusCode;
                            done = response.IsSuccessStatusCode || (status >= 400 && status < 500 && response.StatusCode != HttpStatusCode.TooManyRequests);
                            if (!response.IsSuccessStatusCode)
                            {
                                Trace.TraceWarning("Turn server {0} answered {1} to the record for '{2}'", item.Server, status, item.Record.Game);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceInformation("Turn server {0} not reached, record for '{1}' kept: {2}", item.Server, item.Record.Game, ex.Message);
                        done = false;
                    }

                    if (done)
                    {
                        lock (_lock)
                        {
                            _outbox.Remove(item);
                            SaveOutbox();
                        }
                    }
                    else
                    {
                        unreachable.Add(item.Server);
                    }
                }
            }
            finally
            {
                _flushing.Release();
            }
        }

        #endregion

        #region Outbox file

        private void LoadOutbox()
        {
            if (string.IsNullOrEmpty(_outboxPath) || !File.Exists(_outboxPath))
            {
                return;
            }

            try
            {
                List<PendingRecord> loaded = JsonSerializer.Deserialize<List<PendingRecord>>(File.ReadAllText(_outboxPath));
                if (loaded != null)
                {
                    _outbox.AddRange(loaded.Where(item => item != null && Normalise(item.Server) != null && item.Record != null && item.Record.IsValid));
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Turn server outbox {0} could not be read: {1}", _outboxPath, ex.Message);
            }
        }

        private void SaveOutbox()
        {
            if (string.IsNullOrEmpty(_outboxPath))
            {
                return;
            }

            try
            {
                string temp = _outboxPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_outbox));
                File.Move(temp, _outboxPath, true);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Turn server outbox could not be saved to {0}: {1}", _outboxPath, ex.Message);
            }
        }

        #endregion
    }
}
