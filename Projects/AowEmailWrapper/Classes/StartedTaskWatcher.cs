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

        public void Start()
        {
            _process = new Process();
            //The full path, started through the shell as on .NET Framework: since .NET Core a bare file
            //name is looked up in the Wrapper's own folder and on the PATH, not in the working directory,
            //so "AoW.exe" was not found and no game started
            _process.StartInfo.FileName = _theGame.ExePath;
            _process.StartInfo.WorkingDirectory = _theGame.Root.FullName;
            _process.StartInfo.UseShellExecute = true;

            _process.Start();
            
            new Thread(new ThreadStart(this.Watch)).Start();
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
