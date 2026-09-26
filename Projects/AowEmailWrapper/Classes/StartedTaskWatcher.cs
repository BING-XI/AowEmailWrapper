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
        private bool _stop = false;

        public Process Process
        {
            get { return _process; }
            set { _process = value; }
        }

        public void Stop()
        {
            _stop = true;
        }

        public StartedTaskWatcher(AowGame theGame, StartedTaskCompleteEventHandler callBack)
        {
            _theGame = theGame;
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
            process.StartInfo.FileName = _theGame.ExePath;
            process.StartInfo.WorkingDirectory = _theGame.Root.FullName;
            process.StartInfo.UseShellExecute = useShellExecute;
            process.Start();
            return process;
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
