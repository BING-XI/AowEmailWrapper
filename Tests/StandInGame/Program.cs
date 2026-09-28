using System;
using System.Drawing;
using System.Windows.Forms;

namespace StandInGame
{
    /// <summary>Opens one window titled "Stand-in game" and runs until it is closed or killed.</summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            using (Form window = new Form { Text = "Stand-in game", ClientSize = new Size(320, 200), StartPosition = FormStartPosition.CenterScreen })
            {
                Application.Run(window);
            }
        }
    }
}
