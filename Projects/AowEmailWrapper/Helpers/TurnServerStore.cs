using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AowEmailWrapper.Classes;

namespace AowEmailWrapper.Helpers
{
    /// <summary>
    /// The records a hosted turn server holds: one per player per game, kept in a file so a restart
    /// loses nothing. Bounded, since anyone who finds the address can post to it.
    /// </summary>
    public class TurnServerStore
    {
        public const int MaxRecordsPerGame = 32;
        public const int MaxRecords = 20000;

        private class StoredRecord
        {
            public TurnRecord Record { get; set; }
            public DateTimeOffset Stored { get; set; }
        }

        private readonly string _path;
        private readonly object _lock = new object();
        private readonly List<StoredRecord> _records = new List<StoredRecord>();

        /// <summary>A store kept in the file at path, or in memory only when path is null.</summary>
        public TurnServerStore(string path)
        {
            _path = path;
            Load();
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _records.Count;
                }
            }
        }

        /// <summary>Stores a valid record in place of the player's previous one for that game.</summary>
        public bool Put(TurnRecord record)
        {
            if (record == null || !record.IsValid)
            {
                return false;
            }

            lock (_lock)
            {
                _records.RemoveAll(stored => stored.Record.SameKey(record));
                _records.Add(new StoredRecord { Record = record, Stored = DateTimeOffset.UtcNow });

                //Whoever posts junk for a game, or for endless games, pushes out the oldest records first
                List<StoredRecord> game = _records.Where(stored => SameGame(stored.Record.Game, record.Game)).OrderBy(stored => stored.Stored).ToList();
                foreach (StoredRecord old in game.Take(Math.Max(0, game.Count - MaxRecordsPerGame)))
                {
                    _records.Remove(old);
                }
                if (_records.Count > MaxRecords)
                {
                    foreach (StoredRecord old in _records.OrderBy(stored => stored.Stored).Take(_records.Count - MaxRecords).ToList())
                    {
                        _records.Remove(old);
                    }
                }

                Save();
            }
            return true;
        }

        public List<TurnRecord> ForGame(string game)
        {
            lock (_lock)
            {
                return _records.Where(stored => SameGame(stored.Record.Game, game)).Select(stored => stored.Record).ToList();
            }
        }

        private static bool SameGame(string a, string b)
        {
            return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void Load()
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
            {
                return;
            }

            try
            {
                List<StoredRecord> loaded = JsonSerializer.Deserialize<List<StoredRecord>>(File.ReadAllText(_path));
                if (loaded != null)
                {
                    _records.AddRange(loaded.Where(stored => stored != null && stored.Record != null && stored.Record.IsValid));
                }
            }
            catch (Exception ex)
            {
                //A damaged file costs the records, not the server
                Trace.TraceWarning("Turn server records in {0} could not be read: {1}", _path, ex.Message);
            }
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_path))
            {
                return;
            }

            try
            {
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_records));
                File.Move(temp, _path, true);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Turn server records could not be saved to {0}: {1}", _path, ex.Message);
            }
        }
    }
}
