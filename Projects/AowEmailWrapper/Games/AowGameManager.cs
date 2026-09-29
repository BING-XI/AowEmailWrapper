using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using AowEmailWrapper.ASG;
using AowEmailWrapper.Helpers;
using AowEmailWrapper.ConfigFramework;
using MimeKit;

namespace AowEmailWrapper.Games
{
    public delegate void AowGameSavedEventHandler(object sender, AowGameSavedEventArgs e);

    /// <summary>
    /// Knows every installed copy of the games and decides which copy an incoming turn belongs to
    /// and which copy an outgoing turn came from. Detection results are merged with the labels
    /// and defaults the player set on the Games tab.
    /// </summary>
    public class AowGameManager
    {
        #region Private Members

        private List<AowGame> _games;
        private readonly string _checkEmailFolder;

        #endregion

        #region Constructors

        public AowGameManager()
            : this(AppDataHelper.CheckEmail.FullName, (GamesConfigValues)null)
        {
        }

        /// <summary>
        /// Detects the games from the cheap sources and the copies remembered in config. When nothing is
        /// remembered yet, NeedsDeepScan asks the caller to run the slow drive walk in the background.
        /// </summary>
        public AowGameManager(string checkEmailFolder, GamesConfigValues config)
            : this(checkEmailFolder, config, false)
        {
            NeedsDeepScan = config == null || config.Installs.Count == 0;
        }

        public AowGameManager(string checkEmailFolder, GamesConfigValues config, bool deepScan)
        {
            _checkEmailFolder = checkEmailFolder;
            Reload(config, deepScan);
        }

        /// <summary>For tests and previews: uses the given installs instead of detecting.</summary>
        public AowGameManager(string checkEmailFolder, IEnumerable<AowGame> installs, GamesConfigValues config)
        {
            _checkEmailFolder = checkEmailFolder;
            _ignored = IgnoredOf(config);
            _games = Merge(installs.ToList(), config);
        }

        #endregion

        #region Public Properties

        public AowGameSavedEventHandler OnGameSaved;

        /// <summary>
        /// Asked which folder a game was last seen in (from the activity log), so a turn keeps
        /// going to the copy it started in even when the label is missing.
        /// </summary>
        public Func<AowGameType, string, string> InstallHint { get; set; }

        /// <summary>True until the drive walk has run once and its result was saved to config.</summary>
        public bool NeedsDeepScan { get; private set; }

        /// <summary>Every known copy, including manually added folders whose game has gone missing.</summary>
        public List<AowGame> Games
        {
            get { return _games; }
        }

        public string CheckEmailFolder
        {
            get { return _checkEmailFolder; }
        }

        #endregion

        #region Detection and configuration

        /// <summary>Runs detection again and re-applies the labels and defaults in the config.</summary>
        public void Reload(GamesConfigValues config)
        {
            Reload(config, false);
        }

        public void Reload(GamesConfigValues config, bool deepScan)
        {
            Stopwatch timer = Stopwatch.StartNew();
            List<AowGame> detected = GameDetector.Detect(config != null ? config.Installs : null, deepScan);
            _ignored = IgnoredOf(config);
            _games = Merge(detected, config);
            Trace.TraceInformation("Game detection ({0}) took {1} ms and found {2} copies", deepScan ? "deep scan" : "known folders", timer.ElapsedMilliseconds, _games.Count);

            foreach (AowGame game in _games)
            {
                Trace.TraceInformation("Game copy: {0}{1}", game, game.IsInstalled ? string.Empty : " (missing)");
            }
        }

        /// <summary>Takes the result of a detection run made elsewhere (a background deep scan) and merges it with the config.</summary>
        public void Apply(List<AowGame> detected, GamesConfigValues config)
        {
            _ignored = IgnoredOf(config);
            _games = Merge(detected, config);
            NeedsDeepScan = false;
            Trace.TraceInformation("Deep scan result applied: {0} copies", _games.Count);
        }

        /// <summary>
        /// Takes the Games tab's list as the player left it, so a copy removed or added there, a label
        /// or the default counts at once for everything that asks the manager: Move to on the Activity
        /// Log, the tray menu and the routing of turns. No detection runs; the tab's list is the truth.
        /// </summary>
        public void Accept(IEnumerable<AowGame> games, GamesConfigValues config)
        {
            _ignored = IgnoredOf(config);
            _games = Merge(games.ToList(), config);
            Trace.TraceInformation("Games tab applied: {0} copies", _games.Count);
        }

        /// <summary>The current installs as config entries, so labels and defaults survive a restart.</summary>
        public GamesConfigValues ToConfig()
        {
            GamesConfigValues config = new GamesConfigValues();
            foreach (IgnoredInstallConfigValues ignored in _ignored)
            {
                config.Ignored.Add(new IgnoredInstallConfigValues { GameType = ignored.GameType, Folder = ignored.Folder });
            }
            foreach (AowGame game in _games)
            {
                config.Installs.Add(new GameInstallConfigValues(game));
            }
            return config;
        }

        private List<IgnoredInstallConfigValues> _ignored = new List<IgnoredInstallConfigValues>();

        /// <summary>Copies the player removed from the Games tab; detection leaves them out.</summary>
        public IEnumerable<IgnoredInstallConfigValues> IgnoredInstalls
        {
            get { return _ignored; }
        }

        private static List<IgnoredInstallConfigValues> IgnoredOf(GamesConfigValues config)
        {
            return config == null ? new List<IgnoredInstallConfigValues>() : config.Clone().Ignored;
        }

        private static List<AowGame> Merge(List<AowGame> detected, GamesConfigValues config)
        {
            List<AowGame> games = detected.Where(game => (game.IsInstalled || game.IsManual) && (config == null || !config.IsIgnored(game))).ToList();

            if (config != null)
            {
                foreach (AowGame game in games)
                {
                    GameInstallConfigValues entry = config.Find(game);
                    if (entry != null)
                    {
                        game.Label = entry.Label;
                        game.IsDefault = entry.IsDefault;
                    }
                }

                //A folder the player added whose game is no longer there stays visible so it can be removed
                foreach (GameInstallConfigValues entry in config.Installs.Where(install => install.Manual))
                {
                    if (!games.Any(game => entry.Matches(game)) && !string.IsNullOrEmpty(entry.Folder))
                    {
                        AowGame missing = new AowGame(entry.GameType, entry.Folder, InstallSource.Manual);
                        missing.Label = entry.Label;
                        games.Add(missing);
                    }
                }
            }

            //A copy without a label gets the one its contents call for (the mod found in it, or the stock game),
            //unless another copy of that game already carries it: a label routes turns to exactly one copy.
            //A copy started through the mod's own executable is the mod's home and takes its label first: the
            //game folder Ziggurat was installed into may carry Ziggurat text tables, but it is the vanilla game.
            foreach (AowGame game in games.Where(game => game.IsInstalled && string.IsNullOrEmpty(game.Label))
                                          .OrderBy(game => game.RunsModExecutable ? 0 : 1).ToList())
            {
                string label = game.SuggestedLabel;
                if (!games.Any(other => other != game && other.GameType == game.GameType && AowGame.SameLabel(other.Label, label)))
                {
                    game.Label = label;
                    Trace.TraceInformation("Copy {0} labelled '{1}' ({2})", game.Folder, label,
                        game.DetectedMods.Count > 0 ? string.Join("; ", game.DetectedMods.Select(mod => mod.Evidence)) : "nothing found in it");
                }
            }

            //Exactly one default per game type, preferring the copy from the most trustworthy source
            foreach (AowGameType type in AowGame.AllTypes)
            {
                List<AowGame> installed = games.Where(game => game.GameType == type && game.IsInstalled).ToList();
                AowGame current = installed.FirstOrDefault(game => game.IsDefault);
                foreach (AowGame game in games.Where(game => game.GameType == type))
                {
                    game.IsDefault = false;
                }
                if (current == null)
                {
                    current = installed.OrderBy(game => game.Source).FirstOrDefault();
                }
                if (current != null)
                {
                    current.IsDefault = true;
                }
            }

            return games.OrderBy(game => game.GameType).ThenBy(game => game.IsDefault ? 0 : 1).ThenBy(game => game.Folder).ToList();
        }

        #endregion

        #region Lookup

        public bool IsInstalled(AowGameType theGameType)
        {
            return GetGameByType(theGameType) != null;
        }

        /// <summary>Installed copies of one game type, default first.</summary>
        public List<AowGame> GetInstalls(AowGameType theGameType)
        {
            return _games.Where(game => game.GameType == theGameType && game.IsInstalled)
                         .OrderBy(game => game.IsDefault ? 0 : 1)
                         .ToList();
        }

        /// <summary>The default copy of a game type, or null when it is not installed.</summary>
        public AowGame GetGameByType(AowGameType theGameType)
        {
            return GetInstalls(theGameType).FirstOrDefault();
        }

        public AowGame GetGameById(string id)
        {
            return _games.FirstOrDefault(game => game.Id == id);
        }

        public AowGame GetGameByFolder(AowGameType theGameType, string folder)
        {
            return string.IsNullOrEmpty(folder) ? null : GetInstalls(theGameType).FirstOrDefault(game => game.IsFolder(folder));
        }

        public AowGame GetGameByLabel(AowGameType theGameType, string label)
        {
            return string.IsNullOrEmpty(label) ? null : GetInstalls(theGameType).FirstOrDefault(game => AowGame.SameLabel(game.Label, label));
        }

        /// <summary>The copy an activity log entry points at, falling back to the default copy.</summary>
        public AowGame GetGameForActivity(ConfigFramework.Activity activity)
        {
            if (activity == null)
            {
                return null;
            }
            return GetGameByFolder(activity.GameType, activity.InstallFolder) ?? GetGameByType(activity.GameType);
        }

        /// <summary>The icon for an activity log entry: its copy's, or the one its label calls for when the copy is unknown.</summary>
        public string ImageKeyFor(ConfigFramework.Activity activity)
        {
            AowGame game = GetGameByFolder(activity.GameType, activity.InstallFolder);
            return game != null ? game.ImageKey : AowGame.ImageKeyFor(activity.GameType, activity.ModLabel);
        }

        #endregion

        #region Routing

        /// <summary>
        /// Where an incoming turn goes: the copy whose label the email names, else the copy the
        /// game was last seen in, else the only copy that already holds a turn of that game, else
        /// the default copy. Null when the game is not installed at all.
        /// </summary>
        public AowGame ResolveIncoming(AowGameType theGameType, string modLabel, string fileName)
        {
            List<AowGame> installs = GetInstalls(theGameType);
            if (installs.Count == 0)
            {
                return null;
            }
            if (installs.Count == 1)
            {
                return installs[0];
            }

            AowGame byLabel = GetGameByLabel(theGameType, modLabel);
            if (byLabel != null)
            {
                return byLabel;
            }

            return ResolveKnown(theGameType, fileName, installs) ?? installs[0];
        }

        /// <summary>
        /// Where an outgoing turn came from: the copy whose game is running right now, else the
        /// copy the game was last seen in, else the only copy holding a turn of it, else the default.
        /// </summary>
        public AowGame ResolveOutgoing(AowGameType theGameType, string fileName)
        {
            List<AowGame> installs = GetInstalls(theGameType);
            if (installs.Count == 0)
            {
                return null;
            }
            if (installs.Count == 1)
            {
                return installs[0];
            }

            List<AowGame> running = installs.Where(IsRunning).ToList();
            if (running.Count == 1)
            {
                return running[0];
            }

            return ResolveKnown(theGameType, fileName, installs) ?? installs[0];
        }

        private AowGame ResolveKnown(AowGameType theGameType, string fileName, List<AowGame> installs)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            if (InstallHint != null)
            {
                string folder = InstallHint(theGameType, fileName);
                AowGame hinted = GetGameByFolder(theGameType, folder);
                if (hinted != null)
                {
                    return hinted;
                }
            }

            List<AowGame> holding = installs.Where(game => game.HoldsGameFile(fileName)).ToList();
            return holding.Count == 1 ? holding[0] : null;
        }

        /// <summary>True when a process of this copy's executable is running from this folder.</summary>
        public static bool IsRunning(AowGame game)
        {
            if (game == null || !game.IsInstalled)
            {
                return false;
            }

            try
            {
                foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.ExeFile)))
                {
                    using (process)
                    {
                        try
                        {
                            string path = process.MainModule != null ? process.MainModule.FileName : null;
                            if (!string.IsNullOrEmpty(path) && AowGame.SameFolder(Path.GetDirectoryName(path), game.Folder))
                            {
                                return true;
                            }
                        }
                        catch (Exception)
                        {
                            //A process we may not inspect is not ours
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Could not list running games: {0}", ex.Message);
            }

            return false;
        }

        #endregion

        #region Incoming turns

        public void StoreDownloadFile(ASGFileInfo theAsgFile)
        {
            StoreDownloadFile(theAsgFile, EmailSaveFolder.EmailIn);
        }

        public void StoreDownloadFile(ASGFileInfo theAsgFile, EmailSaveFolder saveFolder)
        {
            StoreDownloadFile(theAsgFile, saveFolder, null, null);
        }

        public void StoreDownloadFile(ASGFileInfo theAsgFile, EmailSaveFolder saveFolder, string accountName)
        {
            StoreDownloadFile(theAsgFile, saveFolder, accountName, null);
        }

        public void StoreDownloadFile(ASGFileInfo theAsgFile, EmailSaveFolder saveFolder, string accountName, string modLabel)
        {
            StoreDownloadFile(theAsgFile, saveFolder, accountName, modLabel, null);
        }

        public void StoreDownloadFile(ASGFileInfo theAsgFile, EmailSaveFolder saveFolder, string accountName, string modLabel, string sender)
        {
            AowGame theGame = theAsgFile.IsValid ? ResolveIncoming(theAsgFile.GameType, modLabel, theAsgFile.FileNameTrue) : null;

            if (theGame != null)
            {
                DirectoryInfo saveFolderInfo = GetSaveFolder(theGame, saveFolder);
                theAsgFile.SaveToFolder(saveFolderInfo.FullName);

                Trace.TraceInformation("Turn {0} ({1}, label '{2}') stored in {3}", theAsgFile.FileNameTrue, theAsgFile.GameType, modLabel, saveFolderInfo.FullName);

                RaiseOnGameSaved(new AowGameSavedEventArgs(theAsgFile.GameType, theAsgFile.FileNameTrue, theAsgFile.GameTitle, theAsgFile.MapTitle, theAsgFile.TurnNumber.ToString())
                {
                    AccountName = accountName,
                    Install = theGame,
                    ModLabel = modLabel,
                    Sender = sender,
                    Players = ASGFileInfo.JoinAddresses(theAsgFile.PlayerEmails)
                });
            }
            else
            {
                theAsgFile.SaveToFolder(_checkEmailFolder);
                RaiseOnGameSaved(new AowGameSavedEventArgs(AowGameType.Unknown, theAsgFile.FileName) { AccountName = accountName, ModLabel = modLabel, Sender = sender, Players = ASGFileInfo.JoinAddresses(theAsgFile.PlayerEmails) });
            }
        }

        #endregion

        #region Game folders

        /// <summary>Points every installed game at the Wrapper. Copies of one game share the registry key, so each key is written once.</summary>
        public void SetEmailConfigAll(string attachmentDir, string localEmailAddress, string smtpServer)
        {
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AowGame game in _games.Where(game => game.IsInstalled))
            {
                if (done.Add(game.GameName))
                {
                    game.SetEmailConfig(attachmentDir, localEmailAddress, smtpServer);
                }
            }
        }

        public bool CheckWriteAccess()
        {
            return _games.Where(game => game.IsInstalled).All(game => game.WriteAccess);
        }

        public void ResetWriteAccess()
        {
            foreach (AowGame game in _games)
            {
                game.ResetWriteAccess();
            }
        }

        /// <summary>Game folders the Wrapper cannot write into, each named once.</summary>
        public List<string> RootsWithoutWriteAccess
        {
            get
            {
                List<string> roots = new List<string>();
                foreach (AowGame game in _games.Where(game => game.IsInstalled && !game.WriteAccess))
                {
                    if (!roots.Any(existing => AowGame.SameFolder(existing, game.Folder)))
                    {
                        roots.Add(game.Folder);
                    }
                }
                return roots;
            }
        }

        public string GetEmailInFolderList()
        {
            List<string> folders = new List<string>();

            foreach (AowGame game in _games.Where(game => game.IsInstalled && !game.WriteAccess))
            {
                foreach (string folder in new[] { game.EmailIn.FullName, game.Save.FullName })
                {
                    if (!folders.Any(existing => AowGame.SameFolder(existing, folder)))
                    {
                        folders.Add(folder);
                    }
                }
            }

            return string.Join(Environment.NewLine, folders);
        }

        /// <summary>Deletes a game's files from every copy of that game type.</summary>
        public void DeleteGame(AowGameType theGameType, string fileName)
        {
            if (theGameType == AowGameType.Unknown)
            {
                //If it's an unknown game just delete
                ClearCheckEmailFolder(fileName);
                return;
            }

            foreach (AowGame theGame in GetInstalls(theGameType))
            {
                foreach (DirectoryInfo folder in theGame.TurnFolders)
                {
                    foreach (FileInfo file in GetAllGameFiles(fileName, folder))
                    {
                        if (File.Exists(file.FullName))
                        {
                            File.Delete(file.FullName);
                        }
                    }
                }
            }
        }

        /// <summary>Moves a game's files into an Ended sub folder in every copy that holds them.</summary>
        public void ArchiveEndedGame(AowGameType theGameType, string fileName, string endedFolderName)
        {
            if (theGameType == AowGameType.Unknown)
            {
                //If it's an unknown game just delete
                ClearCheckEmailFolder(fileName);
                return;
            }

            foreach (AowGame theGame in GetInstalls(theGameType))
            {
                foreach (DirectoryInfo folder in theGame.TurnFolders)
                {
                    FileInfo[] matchingFiles = GetAllGameFiles(fileName, folder);
                    if (matchingFiles.Length > 0)
                    {
                        string endedFolderPath = Path.Combine(folder.FullName, endedFolderName);
                        Directory.CreateDirectory(endedFolderPath);

                        foreach (FileInfo file in matchingFiles)
                        {
                            MoveFile(file, Path.Combine(endedFolderPath, file.Name));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Moves a game's turn files from whichever copies hold them into the target copy, keeping
        /// EmailIn, EmailOut and Save apart. Used when a first turn landed in the wrong copy.
        /// </summary>
        public void MoveGame(AowGameType theGameType, string fileName, AowGame target)
        {
            if (target == null || !target.IsInstalled || string.IsNullOrEmpty(fileName))
            {
                return;
            }

            foreach (AowGame source in GetInstalls(theGameType).Where(game => game.Id != target.Id))
            {
                MoveFiles(fileName, source.EmailIn, target.EmailIn);
                MoveFiles(fileName, source.EmailOut, target.EmailOut);
                MoveFiles(fileName, source.Save, target.Save);
            }
        }

        public void CopyToEmailOut(MimePart theAttachment, AowGame theGame)
        {
            if (theAttachment != null && theGame != null)
            {
                //The attachment name is sender supplied, so it must stay a plain file inside EmailOut
                string destPath = ASGFileInfo.GetPathInside(theGame.EmailOut.FullName, theAttachment.FileName);

                if (destPath != null)
                {
                    if (File.Exists(destPath))
                    {
                        File.Delete(destPath);
                    }

                    MailHelper.SaveAttachment(theAttachment, destPath);
                }
            }
        }

        #endregion

        #region Private Methods

        private void MoveFiles(string fileName, DirectoryInfo from, DirectoryInfo to)
        {
            if (AowGame.SameFolder(from.FullName, to.FullName))
            {
                return;
            }
            foreach (FileInfo file in GetAllGameFiles(fileName, from))
            {
                Directory.CreateDirectory(to.FullName);
                MoveFile(file, Path.Combine(to.FullName, file.Name));
            }
        }

        private static void MoveFile(FileInfo file, string destination)
        {
            try
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
                //This is in a Try Catch incase it tries to move a non virtualized file in virtualization mode (UAC on)
                File.Move(file.FullName, destination);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Could not move {0} to {1}: {2}", file.FullName, destination, ex.Message);
            }
        }

        private void ClearCheckEmailFolder(string fileName)
        {
            string filePath = Path.Combine(_checkEmailFolder, fileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        private static FileInfo[] GetAllGameFiles(string fileName, DirectoryInfo folder)
        {
            try
            {
                return folder.Exists ? folder.GetFiles(AowGame.SearchPattern(fileName)) : new FileInfo[0];
            }
            catch (Exception)
            {
                return new FileInfo[0];
            }
        }

        private void RaiseOnGameSaved(AowGameSavedEventArgs e)
        {
            if (OnGameSaved != null)
            {
                OnGameSaved(this, e);
            }
        }

        private static DirectoryInfo GetSaveFolder(AowGame theGame, EmailSaveFolder saveFolder)
        {
            return saveFolder == EmailSaveFolder.Save ? theGame.Save : theGame.EmailIn;
        }

        #endregion
    }
}
