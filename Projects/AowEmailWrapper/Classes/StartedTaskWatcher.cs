using System.Diagnostics;
using System.Threading;
using AowEmailWrapper.Games;

namespace AowEmailWrapper.Classes
{
    public delegate void StartedTaskCompleteEventHandler(object sender, AowGameType gameType);

    public class StartedTaskWatcher
    {
        private Process _process;
        private StartedTaskCompleteEventHandler _callBack;
        private AowGame _theGame;
        private string _saveToLoad;
        private bool _stop = false;

        public Process Process
        {
            get { return _process; }
            set { _process = value; }
        }

        public AowGame Game
        {
            get { return _theGame; }
        }

        /// <summary>True when this is the copy of the game, started with this save (null for the main menu).</summary>
        public bool Loads(AowGame theGame, string saveToLoad)
        {
            //By id: a rescan or a save on the Games tab builds the copies afresh
            return _theGame != null && theGame != null && _theGame.Id == theGame.Id &&
                string.Equals(_saveToLoad, saveToLoad, System.StringComparison.OrdinalIgnoreCase);
        }

        public void Stop()
        {
            _stop = true;
        }

        public StartedTaskWatcher(AowGame theGame, StartedTaskCompleteEventHandler callBack)
            : this(theGame, null, callBack)
        { }

        /// <param name="saveToLoad">A save for the game to load, as AowGame.StartArgumentFor gives it; null to start at the main menu.</param>
        public StartedTaskWatcher(AowGame theGame, string saveToLoad, StartedTaskCompleteEventHandler callBack)
        {
            _theGame = theGame;
            _saveToLoad = saveToLoad;
            _callBack = callBack;
        }

        private const int ErrorElevationRequired = 740;

        public void Start()
        {
            //The full path: since .NET Core a bare file name is looked up in the Wrapper's own folder and
            //on the PATH, not in the working directory, so "AoW.exe" was not found and no game started.
            //Started directly rather than through the shell: an executable that came out of a downloaded
            //zip (a mod) carries the mark of the web, and the shell answers with an "Open File - Security
            //Warning" that opens behind other windows when the Wrapper sits in the tray, so the game seemed
            //not to start. Only when Windows insists on elevation is the shell asked, for its UAC prompt.
            try
            {
                _process = StartProcess(false);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
            {
                _process = StartProcess(true);
            }

            new Thread(new ThreadStart(this.Watch)).Start();
        }

        private Process StartProcess(bool useShellExecute)
        {
            Process process = new Process();
            process.StartInfo = StartInfoFor(_theGame, _saveToLoad, useShellExecute);
            process.Start();
            return process;
        }

        internal static ProcessStartInfo StartInfoFor(AowGame theGame, string saveToLoad, bool useShellExecute)
        {
            ProcessStartInfo start = new ProcessStartInfo(theGame.ExePath)
            {
                WorkingDirectory = theGame.Root.FullName,
                UseShellExecute = useShellExecute
            };
            if (!string.IsNullOrEmpty(saveToLoad))
            {
                //In double quotes, as the game splits its command line at spaces outside them
                start.Arguments = string.Concat("\"", saveToLoad, "\"");
            }
            return start;
        }

        private void Watch()
        {
            if (_callBack != null)
            {
                do
                {
                    if (!_process.HasExited)
                    {
                        _process.Refresh();
                    }
                }
                while (!_process.WaitForExit(1000) && !_stop);

                if (!_stop)
                {
                    _callBack(this, _theGame.GameType);
                }

                _process.Dispose();
            }
        }
    }
}
