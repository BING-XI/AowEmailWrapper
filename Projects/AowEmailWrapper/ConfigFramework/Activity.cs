using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using AowEmailWrapper.Games;

namespace AowEmailWrapper.ConfigFramework
{
    public enum ActivityState
    {
        [XmlEnum(Name = "None")]
        None = 1,
        [XmlEnum(Name = "New")]
        New,
        [XmlEnum(Name = "Received")]
        Received,
        [XmlEnum(Name = "Sent")]
        Sent,
        [XmlEnum(Name = "Ended")]
        Ended,
        [XmlEnum(Name = "Pending")]
        Pending
    }

    [XmlRoot("activity")]
    public class Activity
    {
        private AowGameType _type;
        private string _fileName;
        private ActivityState _status;
        private string _dateTicks;
        private string _mapTitle = string.Empty;
        private string _turnNo = string.Empty;

        [XmlAttribute("game_type")]
        public AowGameType GameType
        {
            get { return _type; }
            set { _type = value; }
        }

        [XmlAttribute("file_name")]
        public string FileName
        {
            get { return _fileName; }
            set { _fileName = value; }
        }

        [XmlAttribute("map_title")]
        public string MapTitle
        {
            get { return _mapTitle; }
            set { _mapTitle = value; }
        }

        [XmlAttribute("turn")]
        public string TurnNumber
        {
            get { return _turnNo; }
            set { _turnNo = value; }
        }

        //Depricated
        [XmlAttribute("in")]
        public string Inwards
        {
            get { return null; }
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _status = Helpers.ConfigHelper.ParseEnumString<ActivityState>(value);
                }
            }
        }

        //Depricated
        [XmlAttribute("out")]
        public string Outwards
        {
            get { return null; }
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    ActivityState theState = Helpers.ConfigHelper.ParseEnumString<ActivityState>(value);
                    if (!theState.Equals(ActivityState.None))
                    {
                        _status = theState;
                    }
                }
            }
        }

        [XmlAttribute("status")]
        public ActivityState Status
        {
            get { return _status; }
            set { _status = value; }
        }

        /// <summary>The account this game arrived on (or was last sent from); replies go out the same way.</summary>
        [XmlAttribute("account")]
        public string AccountName { get; set; }

        /// <summary>Folder of the copy of the game this turn lives in, when the player has more than one.</summary>
        [XmlAttribute("install")]
        public string InstallFolder { get; set; }

        /// <summary>The mod label the turn arrived with or was sent under.</summary>
        [XmlAttribute("mod")]
        public string ModLabel { get; set; }

        /// <summary>Address a received turn came from.</summary>
        [XmlAttribute("sender")]
        public string Sender { get; set; }

        /// <summary>Addresses a sent turn went to, separated by ';'.</summary>
        [XmlAttribute("recipients")]
        public string Recipients { get; set; }

        /// <summary>
        /// True when nobody at the sender's address had sent the player a turn, or been sent one,
        /// before this arrived. The Wrapper files a turn from anyone who emails one, so this is
        /// the player's cue that the turn did not come from someone they are playing with.
        /// </summary>
        [XmlAttribute("new_sender")]
        public bool NewSender { get; set; }

        public bool ShouldSerializeNewSender()
        {
            return NewSender;
        }

        /// <summary>Every player's address from the save file, separated by ';'.</summary>
        [XmlAttribute("players")]
        public string Players { get; set; }

        /// <summary>What other players' wrappers last said about where this turn is.</summary>
        [XmlAttribute("whereabouts")]
        public string Whereabouts { get; set; }

        /// <summary>The player whose wrapper last said it holds the turn, if any.</summary>
        [XmlAttribute("holder")]
        public string Holder { get; set; }

        /// <summary>
        /// When no wrapper says it holds the turn: the player who most probably has it, worked out from the
        /// answers (the newest send known, whose recipient has not answered). A guess, shown as one.
        /// </summary>
        [XmlAttribute("likely_holder")]
        public string LikelyHolder { get; set; }

        /// <summary>What each player's wrapper last said, one entry per player, for working out who has the turn.</summary>
        [XmlElement("answer")]
        public List<TurnAnswer> Answers
        {
            get { return _answers; }
            set { _answers = value ?? new List<TurnAnswer>(); }
        }
        private List<TurnAnswer> _answers = new List<TurnAnswer>();

        public bool ShouldSerializeAnswers()
        {
            return _answers.Count > 0;
        }

        [XmlAttribute("ticks")]
        public string DateTicks
        {
            get { return _dateTicks; }
            set { _dateTicks = value; }
        }

        public Activity()
        { }

        public Activity(AowGameSavedEventArgs e)
            : this(ActivityState.Received, e.GameType, e.FileName, e.MapTitle, e.TurnNumber)
        {
            AccountName = e.AccountName;
            InstallFolder = e.Install != null ? e.Install.Folder : null;
            ModLabel = e.ModLabel;
            Sender = e.Sender;
            Players = e.Players;
        }

        public Activity(ActivityState status, AowGameType type, string fileName, string mapTitle, string turnNo)
        {
            _status = status;
            _type = type;
            _fileName = fileName;
            _mapTitle = mapTitle;
            _turnNo = turnNo;
            _dateTicks = DateTime.Now.Ticks.ToString();
        }
    }

    /// <summary>One player's wrapper's answer to "who has the turn?".</summary>
    public class TurnAnswer
    {
        [XmlAttribute("from")]
        public string Responder { get; set; }

        [XmlAttribute("status")]
        public ActivityState Status { get; set; }

        /// <summary>When the responder received or sent the turn, as an ISO date; empty when unknown.</summary>
        [XmlAttribute("date")]
        public string Date { get; set; }

        /// <summary>Who the responder sent it to, separated by ';', when they sent it.</summary>
        [XmlAttribute("sent_to")]
        public string SentTo { get; set; }
    }
}
