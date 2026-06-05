using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Virinco.WATS.Converter.KohYoung
{
    static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        static void Main()
        {
            AttachConsole(-1); // Attach to parent console if launched from terminal
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new MainForm());
        }
    }
}
