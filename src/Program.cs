// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: start-up. "--selftest [report.txt]" runs the checks and exits
// with the number that failed.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PSLite
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
            {
                string report = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-" + Proto.Version + ".txt");
                return SelfTest.Run(report);
            }
            try { SetProcessDPIAware(); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Init();
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Crash(e.Exception);
            };
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception e)
            {
                Crash(e);
                return 1;
            }
            return 0;
        }

        static void Crash(Exception e)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                string path = Path.Combine(Settings.Folder, "crash.txt");
                File.AppendAllText(path, DateTime.Now + Environment.NewLine + e + Environment.NewLine + Environment.NewLine);
                MessageBox.Show(e.Message + "\n\nDetails were saved to " + path, Proto.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception) { }
        }
    }
}
