namespace AowEmailWrapper.Games
{
    public class AowGameSavedEventArgs
    {
        private AowGameType _theGameType;
        private string _fileName;
        private string _gameTitle;
        private string _mapTitle;
        private string _turnNo;

        public AowGameType GameType
        {
            get { return _theGameType; }
            set { _theGameType = value; }
        }

        public string FileName
        {
            get { return _fileName; }
            set { _fileName = value; }
        }

        public string GameTitle
        {
            get { return _gameTitle; }
            set { _gameTitle = value; }
        }

        public string MapTitle
        {
            get { return _mapTitle; }
            set { _mapTitle = value; }
        }

        /// <summary>Name of the email account the turn was downloaded from, if any.</summary>
        public string AccountName { get; set; }

        /// <summary>The copy of the game the turn was put in, null for an unknown game.</summary>
        public AowGame Install { get; set; }

        /// <summary>Mod label carried by the email, if any.</summary>
        public string ModLabel { get; set; }

        /// <summary>Email address the turn was sent from, empty when it did not come by email.</summary>
        public string Sender { get; set; }

        /// <summary>Every player's address from the save, separated by ';'.</summary>
        public string Players { get; set; }

        /// <summary>True when the turn said the sender's Wrapper wants to be told where turns go.</summary>
        public bool WantsNotices { get; set; }

        /// <summary>The names the turn carried for the game's players, from the sender's Aliases tab and their own name.</summary>
        public System.Collections.Generic.IList<ConfigFramework.PlayerAlias> SharedNames { get; set; }

        /// <summary>
        /// Set by the handler once the turn is in the saved activity log. Left false when the Wrapper is closing and
        /// the log can no longer take it, so the mail checker leaves the email to be fetched again.
        /// </summary>
        public bool Recorded { get; set; }

        public string TurnNumber
        {
            get { return _turnNo; }
            set { _turnNo = value; }
        }

        public AowGameSavedEventArgs(AowGameType type, string fileName)
            : this(type, fileName, null, null, null)
        { }

        public AowGameSavedEventArgs(AowGameType type, string fileName, string gameTitle, string mapTitle, string turnNo)
        {
            _theGameType = type;
            _fileName = fileName;
            _gameTitle = gameTitle;
            _mapTitle = mapTitle;
            _turnNo = turnNo;
        }
    }
}
