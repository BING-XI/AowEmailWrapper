using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AowEmailWrapper.ConfigFramework;

namespace AowEmailWrapper.Classes
{
    /// <summary>
    /// What one player's Wrapper last told a turn server about a game: it received the turn, sent it on,
    /// or ended the game. The server keeps one record per player per game, so a newer record replaces
    /// the older one and the records of a game say where its turn is without anyone being asked.
    /// </summary>
    public class TurnRecord
    {
        public const int MaxGameLength = 260;
        public const int MaxAddressLength = 254;
        public const int MaxSentToLength = 1024;
        public const int MaxTurnLength = 16;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>The turn's file name, which every player's Wrapper knows the game by.</summary>
        public string Game { get; set; }

        /// <summary>Address of the player whose Wrapper wrote the record.</summary>
        public string Player { get; set; }

        /// <summary>Received, Sent or Ended.</summary>
        public string Status { get; set; }

        /// <summary>When it happened, ISO 8601 with offset.</summary>
        public string Date { get; set; }

        /// <summary>Who a sent turn went to, separated by ';'.</summary>
        public string SentTo { get; set; }

        public string Turn { get; set; }

        public static TurnRecord Create(string game, string player, ActivityState status, DateTimeOffset date, string sentTo, string turn)
        {
            return new TurnRecord
            {
                Game = game,
                Player = player,
                Status = (status == ActivityState.Pending ? ActivityState.Sent : status).ToString(),
                Date = date.ToString("o", CultureInfo.InvariantCulture),
                SentTo = string.IsNullOrWhiteSpace(sentTo) ? null : sentTo.Trim(),
                Turn = string.IsNullOrWhiteSpace(turn) ? null : turn.Trim(),
            };
        }

        [JsonIgnore]
        public ActivityState State
        {
            get
            {
                ActivityState state;
                return Enum.TryParse(Status, true, out state) ? state : ActivityState.None;
            }
        }

        [JsonIgnore]
        public DateTimeOffset? When
        {
            get
            {
                DateTimeOffset when;
                return DateTimeOffset.TryParse(Date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when) ? when : (DateTimeOffset?)null;
            }
        }

        /// <summary>
        /// True when the record can be stored: a game, a player's address, a state a Wrapper reports and a
        /// date, each within its length. Whatever a stranger posts is held to the same rules.
        /// </summary>
        [JsonIgnore]
        public bool IsValid
        {
            get
            {
                ActivityState state = State;
                return !string.IsNullOrWhiteSpace(Game) && Game.Length <= MaxGameLength &&
                    !string.IsNullOrWhiteSpace(Player) && Player.Length <= MaxAddressLength && Player.Contains("@") &&
                    (state == ActivityState.Received || state == ActivityState.Sent || state == ActivityState.Ended) &&
                    When.HasValue &&
                    (SentTo == null || SentTo.Length <= MaxSentToLength) &&
                    (Turn == null || Turn.Length <= MaxTurnLength);
            }
        }

        /// <summary>The record as the "who has the turn?" answers are kept, so both ways of asking share one display.</summary>
        public TurnState ToState()
        {
            return new TurnState
            {
                Game = Game,
                Responder = Player,
                Status = State,
                Date = When,
                SentTo = State == ActivityState.Sent ? SentTo : null,
            };
        }

        public bool SameKey(TurnRecord other)
        {
            return other != null &&
                string.Equals(Game, other.Game, StringComparison.OrdinalIgnoreCase) &&
                TurnQuery.SameAddress(Player, other.Player);
        }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, JsonOptions);
        }

        public static string ToJson(IEnumerable<TurnRecord> records)
        {
            return JsonSerializer.Serialize((records ?? Enumerable.Empty<TurnRecord>()).ToList(), JsonOptions);
        }

        /// <summary>Null for anything that is not a record.</summary>
        public static TurnRecord FromJson(string json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<TurnRecord>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Empty for anything that is not a list of records.</summary>
        public static List<TurnRecord> ListFromJson(string json)
        {
            try
            {
                List<TurnRecord> records = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<TurnRecord>>(json, JsonOptions);
                return (records ?? new List<TurnRecord>()).Where(record => record != null).ToList();
            }
            catch (JsonException)
            {
                return new List<TurnRecord>();
            }
        }
    }
}
