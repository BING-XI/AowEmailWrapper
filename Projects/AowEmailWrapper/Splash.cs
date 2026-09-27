using System.Windows.Forms;
using System.Threading;
using AowEmailWrapper.Classes;

namespace AowEmailWrapper
{
    public partial class Splash : Form
    {
        private static Splash _frmSplash = null;
        private static Thread _frmThread = null;
        //The splash is built on its own thread; the main window may finish loading first
        private static readonly object Sync = new object();
        private static bool _closeRequested;

        private double _opacityIncrement = .05;
        private double _opacityDecrement = .1;
        private const int TIMER_INTERVAL = 50;
        private System.Windows.Forms.Timer _timer;

        public Splash()
        {
            InitializeComponent();
            this.ClientSize = this.BackgroundImage.Size;
            this.Opacity = .5;

            _timer = new System.Windows.Forms.Timer(this.components);
            _timer.Tick += new System.EventHandler(this.timer_Tick);
            _timer.Interval = TIMER_INTERVAL;
            _timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // Turn on WS_EX_TOOLWINDOW style bit
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= ExtendedWindowStyles.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        private void timer_Tick(object sender, System.EventArgs e)
        {
            if (_opacityIncrement > 0)
            {
                if (this.Opacity < 1)
                    this.Opacity += _opacityIncrement;
            }
            else
            {
                if (this.Opacity > 0)
                    this.Opacity += _opacityIncrement;
                else
                    this.Close();
            }
        }

        private static void ShowForm()
        {
            Splash splash = new Splash();
            lock (Sync)
            {
                //Asked to close before it existed: it fades out at once instead of staying for good
                if (_closeRequested)
                {
                    splash._opacityIncrement = -splash._opacityDecrement;
                }
                _frmSplash = splash;
            }
            Application.Run(splash);
        }

        public static  void ShowSplashScreen()
        {
            if (_frmThread != null)
                return;
            _frmThread = new Thread(new ThreadStart(Splash.ShowForm));
            _frmThread.IsBackground = true;
            _frmThread.SetApartmentState(ApartmentState.STA);
            _frmThread.Start();
        }

        public static void CloseForm()
        {
            lock (Sync)
            {
                _closeRequested = true;
                if (_frmSplash != null && _frmSplash.IsDisposed == false)
                {
                    // Make it start going away.
                    _frmSplash._opacityIncrement = -_frmSplash._opacityDecrement;
                }
            }
        }
    }
}
