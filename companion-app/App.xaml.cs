using System.Threading;
using System.Windows;

namespace KnobConfig
{
    public partial class App : Application
    {
        private Mutex? _single;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Single instance: if one is already running, bail out quietly.
            _single = new Mutex(true, "KnobConfig_SingleInstance_5da10000", out bool createdNew);
            if (!createdNew) { Shutdown(); return; }
            base.OnStartup(e);
        }
    }
}
