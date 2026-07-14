using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace KnobConfig
{
    public partial class App : Application
    {
        Mutex? _single;
        EventWaitHandle? _showEvent;
        public static MainWindow? Win;

        protected override void OnStartup(StartupEventArgs e)
        {
            _single = new Mutex(true, "KnobConfig_SingleInstance_5da10000", out bool createdNew);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "KnobConfig_Show_5da10000");

            if (!createdNew)
            {
                _showEvent.Set();   // tell the already-running instance to surface its window
                Shutdown();
                return;
            }

            // Background waiter: when a second instance signals, show our window.
            var t = new Thread(() =>
            {
                while (true)
                {
                    try { _showEvent.WaitOne(); } catch { break; }
                    Dispatcher.BeginInvoke(new Action(() => Win?.ShowFromTray()));
                }
            }) { IsBackground = true };
            t.Start();

            base.OnStartup(e);

            bool startup = e.Args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            Win = new MainWindow();
            if (!startup) Win.Show();   // launched by hand -> show; launched at login -> stay in tray
        }
    }
}
