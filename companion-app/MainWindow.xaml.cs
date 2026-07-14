// =============================================================================
//  KnobConfig (WPF)  -  companion app for the XIAO ESP32-C3 Volume Knob
//  Talks to the knob's custom BLE GATT service (see ../src/main.cpp):
//    Service 5da10000-...   Status 5da10001 (notify)   Config 5da10002 (r/w)
// =============================================================================
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace KnobConfig
{
    // 14-byte config, byte-for-byte matching the firmware's packed struct.
    class Cfg
    {
        public byte Mode, R, G, B, Brightness, RainbowSpeed, RainbowSpread, BreatheSpeed;
        public ushort IdleDimS, IdleOffS, LowBattMv;

        public byte[] ToBytes()
        {
            var b = new byte[14];
            b[0] = Mode; b[1] = R; b[2] = G; b[3] = B; b[4] = Brightness;
            b[5] = RainbowSpeed; b[6] = RainbowSpread;
            b[7] = (byte)(IdleDimS & 0xFF); b[8] = (byte)(IdleDimS >> 8);
            b[9] = (byte)(IdleOffS & 0xFF); b[10] = (byte)(IdleOffS >> 8);
            b[11] = (byte)(LowBattMv & 0xFF); b[12] = (byte)(LowBattMv >> 8);
            b[13] = BreatheSpeed;
            return b;
        }

        public static Cfg FromBytes(byte[] b) => new Cfg
        {
            Mode = b[0], R = b[1], G = b[2], B = b[3], Brightness = b[4],
            RainbowSpeed = b[5], RainbowSpread = b[6],
            IdleDimS = (ushort)(b[7] | (b[8] << 8)),
            IdleOffS = (ushort)(b[9] | (b[10] << 8)),
            LowBattMv = (ushort)(b[11] | (b[12] << 8)),
            BreatheSpeed = b[13],
        };
    }

    public partial class MainWindow : Window
    {
        static readonly Guid SVC    = new Guid("5da10000-9f2b-4c7e-8a3d-2b6c1e4f7a90");
        static readonly Guid STATUS = new Guid("5da10001-9f2b-4c7e-8a3d-2b6c1e4f7a90");
        static readonly Guid CONFIG = new Guid("5da10002-9f2b-4c7e-8a3d-2b6c1e4f7a90");
        static readonly Guid PCSTATE = new Guid("5da10003-9f2b-4c7e-8a3d-2b6c1e4f7a90");
        const string DEVICE_NAME = "Eugene's Knob";
        const string AUTOSTART_NAME = "VolumeKnob";

        static readonly string LogPath = System.IO.Path.Combine(AppContext.BaseDirectory, "knobconfig.log");
        static void Log(string s)
        {
            try { System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine); }
            catch { }
        }

        BluetoothLEDevice? _device;
        GattCharacteristic? _statusChar;
        GattCharacteristic? _configChar;
        GattCharacteristic? _pcStateChar;

        // PC audio/media bridge
        AudioMonitor? _audio;
        MediaMonitor? _media;
        System.Windows.Threading.DispatcherTimer? _pollTimer;
        bool _suppressVol;
        bool _pcMuted, _pcPlaying, _pcHasSession;

        // tray / lifecycle
        System.Windows.Forms.NotifyIcon? _tray;
        bool _exiting;

        bool _loading;
        bool _connecting;
        int  _mode;
        ushort _lastMv;
        byte _lastPct;

        // colour state (HSV drives the wheel; _r/_g/_b is what we send)
        double _hue = 210, _sat = 1, _val = 1;
        byte _r, _g, _b;
        bool _suppressColor;
        const double WheelR = 89, WheelC = 91;   // for a 182 px wheel
        readonly System.Windows.Threading.DispatcherTimer _writeTimer;

        static readonly (byte, byte, byte)[] PRESETS =
        {
            (0,120,255),(255,255,255),(255,0,0),(0,220,60),
            (255,120,0),(255,0,180),(150,0,255),(0,220,200)
        };

        public MainWindow()
        {
            InitializeComponent();

            _writeTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(200) };
            _writeTimer.Tick += async (_, __) => { _writeTimer.Stop(); await WriteConfigAsync(); };

            // preset swatches
            foreach (var p in PRESETS)
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("Swatch"),
                    Background = new SolidColorBrush(Color.FromRgb(p.Item1, p.Item2, p.Item3)),
                    Tag = p
                };
                btn.Click += Preset_Click;
                Presets.Children.Add(btn);
            }

            // mode
            ModeRainbow.Click += (_, __) => SelectMode(0);
            ModeSolid.Click   += (_, __) => SelectMode(1);
            ModeBreathe.Click += (_, __) => SelectMode(2);

            // colour wheel
            WheelImage.Source = BuildWheel(182);
            WheelHost.MouseLeftButtonDown += Wheel_Down;
            WheelHost.MouseMove          += Wheel_Move;
            WheelHost.MouseLeftButtonUp  += Wheel_Up;
            ValueSlider.ValueChanged += (_, __) =>
            {
                if (_suppressColor || _loading) return;
                _val = ValueSlider.Value / 255.0;
                RecalcColorFromHsv();
                Schedule();
            };

            // value sliders
            BrightnessSlider.ValueChanged += (_, __) => { BrightnessVal.Text = ((int)BrightnessSlider.Value).ToString(); Schedule(); };
            SpeedSlider.ValueChanged      += (_, __) => { SpeedVal.Text = ((int)SpeedSlider.Value).ToString(); Schedule(); };
            SpreadSlider.ValueChanged     += (_, __) => { SpreadVal.Text = ((int)SpreadSlider.Value).ToString(); Schedule(); };
            BreatheSlider.ValueChanged    += (_, __) => { BreatheVal.Text = ((int)BreatheSlider.Value).ToString(); Schedule(); };
            IdleDimSlider.ValueChanged    += (_, __) => { IdleDimVal.Text = ((int)IdleDimSlider.Value) + " s"; Schedule(); };
            IdleOffSlider.ValueChanged    += (_, __) => { IdleOffVal.Text = ((int)IdleOffSlider.Value) + " s"; Schedule(); };
            LowBattSlider.ValueChanged    += (_, __) => { LowBattVal.Text = (((int)LowBattSlider.Value) / 1000.0).ToString("0.00") + "V"; Schedule(); };

            BattTrack.SizeChanged += (_, __) => ApplyBatteryWidth();

            // PC volume + mute
            MuteBtn.Click += (_, __) => _audio?.ToggleMute();
            VolumeSlider.ValueChanged += (_, __) =>
            {
                if (_suppressVol) return;
                VolPct.Text = ((int)VolumeSlider.Value) + "%";
                _audio?.SetVolume((int)VolumeSlider.Value);
            };

            ReconnectBtn.Click += async (_, __) => await ConnectWithRetryAsync();
            Closing += MainWindow_Closing;

            SetupTray();
            RegisterAutostart(true);   // keep it starting with Windows by default
            UpdateSections();

            // Kick off audio/media + BLE after construction. Posted (not Loaded) so it
            // runs even when the app launches straight to the tray (window never shown).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartAudioMedia();
                _ = ConnectWithRetryAsync();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ---- window / tray lifecycle -----------------------------------------
        void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_exiting) { e.Cancel = true; Hide(); }   // close button -> hide to tray
        }

        public void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;   // bounce to the foreground
        }

        void SetupTray()
        {
            _tray = new System.Windows.Forms.NotifyIcon { Text = "Volume Knob", Visible = true };
            _tray.Icon = LoadTrayIcon();
            _tray.DoubleClick += (_, __) => ShowFromTray();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            var open = new System.Windows.Forms.ToolStripMenuItem("Open");
            open.Click += (_, __) => ShowFromTray();
            var startup = new System.Windows.Forms.ToolStripMenuItem("Start with Windows")
            { Checked = IsAutostart(), CheckOnClick = true };
            startup.CheckedChanged += (_, __) => RegisterAutostart(startup.Checked);
            var exit = new System.Windows.Forms.ToolStripMenuItem("Exit");
            exit.Click += (_, __) => ExitApp();
            menu.Items.Add(open);
            menu.Items.Add(startup);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exit);
            _tray.ContextMenuStrip = menu;
        }

        // The knob.ico is embedded as a WPF Resource (not a loose file), so load the
        // 16px tray icon from that resource; fall back to the exe's own icon.
        static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("knob.ico", UriKind.Relative));
                if (info != null) return new System.Drawing.Icon(info.Stream, new System.Drawing.Size(16, 16));
            }
            catch { }
            try
            {
                var exe = ExePath();
                if (!string.IsNullOrEmpty(exe))
                {
                    var ic = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                    if (ic != null) return ic;
                }
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        void ExitApp()
        {
            _exiting = true;
            try { _pollTimer?.Stop(); } catch { }
            try { _audio?.Dispose(); } catch { }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            Cleanup();
            System.Windows.Application.Current.Shutdown();
        }

        // ---- run-at-login (HKCU Run) -----------------------------------------
        static string ExePath()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { return ""; }
        }
        static bool IsAutostart()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return k?.GetValue(AUTOSTART_NAME) != null;
            }
            catch { return false; }
        }
        static void RegisterAutostart(bool on)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (k == null) return;
                if (on) k.SetValue(AUTOSTART_NAME, "\"" + ExePath() + "\" --startup");
                else k.DeleteValue(AUTOSTART_NAME, false);
            }
            catch { }
        }

        // ---- PC audio + media bridge -----------------------------------------
        async void StartAudioMedia()
        {
            _audio = new AudioMonitor();
            _audio.Changed += (v, m) => Dispatcher.BeginInvoke(new Action(() => OnAudio(v, m)));
            var a = _audio.Get();
            OnAudio(a.vol, a.muted);

            _media = new MediaMonitor();
            _media.Changed += (p, h) => Dispatcher.BeginInvoke(new Action(() => OnMedia(p, h)));
            await _media.StartAsync();

            _pollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += (_, __) =>
            {
                if (_audio != null && _audio.RefreshDevice()) { var g = _audio.Get(); OnAudio(g.vol, g.muted); }
                PushPcState();   // heartbeat: keeps the knob synced even if a write was lost
            };
            _pollTimer.Start();
        }

        void OnAudio(int vol, bool muted)
        {
            _suppressVol = true;
            VolumeSlider.Value = Math.Max(0, Math.Min(100, vol));
            _suppressVol = false;
            VolPct.Text = vol + "%";
            MuteBtn.IsChecked = muted;
            MuteBtn.Content = muted ? "🔇" : "🔊";   // 🔇 / 🔊
            _pcMuted = muted;
            PushPcState();
        }

        void OnMedia(bool playing, bool has)
        {
            _pcPlaying = playing; _pcHasSession = has;
            PushPcState();
        }

        async void PushPcState()
        {
            var ch = _pcStateChar;
            if (ch == null) return;
            byte flags = (byte)((_pcMuted ? 1 : 0) | (_pcPlaying ? 2 : 0) | (_pcHasSession ? 4 : 0));
            try
            {
                var buf = CryptographicBuffer.CreateFromByteArray(new byte[] { flags });
                await ch.WriteValueWithResultAsync(buf, GattWriteOption.WriteWithoutResponse);
            }
            catch (Exception ex) { Log("pcstate write: " + ex.Message); }
        }

        // dark title bar
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch { }
        }
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // ---- UI helpers -------------------------------------------------------
        void SelectMode(int m)
        {
            _mode = m;
            ModeRainbow.IsChecked = m == 0;
            ModeSolid.IsChecked   = m == 1;
            ModeBreathe.IsChecked = m == 2;
            UpdateSections();
            Schedule();
        }

        void UpdateSections()
        {
            ColorSection.Visibility   = _mode == 0 ? Visibility.Collapsed : Visibility.Visible;
            RainbowSection.Visibility = _mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            BreatheSection.Visibility = _mode == 2 ? Visibility.Visible : Visibility.Collapsed;
            MotionCard.Visibility     = _mode == 1 ? Visibility.Collapsed : Visibility.Visible;
        }

        void UpdateSwatch() => Swatch.Background = new SolidColorBrush(Color.FromRgb(_r, _g, _b));

        void Preset_Click(object sender, RoutedEventArgs e)
        {
            var (r, g, b) = (((byte, byte, byte))((Button)sender).Tag);
            SetColorFromRgb(r, g, b);
            Schedule();
        }

        // ---- colour wheel -----------------------------------------------------
        void Wheel_Down(object s, MouseButtonEventArgs e) { WheelHost.CaptureMouse(); HandleWheel(e.GetPosition(WheelHost)); }
        void Wheel_Move(object s, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) HandleWheel(e.GetPosition(WheelHost)); }
        void Wheel_Up(object s, MouseButtonEventArgs e) { WheelHost.ReleaseMouseCapture(); }

        void HandleWheel(Point p)
        {
            double dx = p.X - WheelC, dy = p.Y - WheelC;
            double r = Math.Sqrt(dx * dx + dy * dy);
            double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI; if (ang < 0) ang += 360;
            _hue = ang;
            _sat = Math.Min(1.0, r / WheelR);
            RecalcColorFromHsv();
            Schedule();
        }

        void RecalcColorFromHsv()
        {
            var (r, g, b) = HsvToRgb(_hue, _sat, _val);
            _r = (byte)r; _g = (byte)g; _b = (byte)b;
            UpdateSwatch(); MoveSelector();
        }

        void SetColorFromRgb(byte r, byte g, byte b)
        {
            _r = r; _g = g; _b = b;
            var (h, s, v) = RgbToHsv(r, g, b);
            _hue = h; _sat = s; _val = v;
            _suppressColor = true; ValueSlider.Value = Math.Round(v * 255); _suppressColor = false;
            UpdateSwatch(); MoveSelector();
        }

        void MoveSelector()
        {
            double ang = _hue * Math.PI / 180.0, rr = _sat * WheelR;
            Canvas.SetLeft(WheelSelector, WheelC + Math.Cos(ang) * rr - 7.5);
            Canvas.SetTop(WheelSelector, WheelC + Math.Sin(ang) * rr - 7.5);
        }

        static BitmapSource BuildWheel(int size)
        {
            double R = size / 2.0 - 2, C = size / 2.0;
            var px = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double dx = x - C + 0.5, dy = y - C + 0.5;
                    double r = Math.Sqrt(dx * dx + dy * dy);
                    int i = (y * size + x) * 4;
                    if (r <= R)
                    {
                        double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI; if (ang < 0) ang += 360;
                        var (rr, gg, bb) = HsvToRgb(ang, Math.Min(1.0, r / R), 1.0);
                        px[i] = (byte)bb; px[i + 1] = (byte)gg; px[i + 2] = (byte)rr; px[i + 3] = 255;
                    }
                }
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, size, size), px, size * 4, 0);
            bmp.Freeze();
            return bmp;
        }

        static (int, int, int) HsvToRgb(double h, double s, double v)
        {
            double c = v * s, hp = h / 60.0;
            double xx = c * (1 - Math.Abs(hp % 2 - 1));
            double r1 = 0, g1 = 0, b1 = 0;
            if (hp < 1) { r1 = c; g1 = xx; }
            else if (hp < 2) { r1 = xx; g1 = c; }
            else if (hp < 3) { g1 = c; b1 = xx; }
            else if (hp < 4) { g1 = xx; b1 = c; }
            else if (hp < 5) { r1 = xx; b1 = c; }
            else { r1 = c; b1 = xx; }
            double m = v - c;
            return ((int)Math.Round((r1 + m) * 255), (int)Math.Round((g1 + m) * 255), (int)Math.Round((b1 + m) * 255));
        }

        static (double, double, double) RgbToHsv(byte r, byte g, byte b)
        {
            double rr = r / 255.0, gg = g / 255.0, bb = b / 255.0;
            double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
            double d = max - min, h = 0;
            if (d > 1e-6)
            {
                if (max == rr) h = 60 * (((gg - bb) / d) % 6);
                else if (max == gg) h = 60 * (((bb - rr) / d) + 2);
                else h = 60 * (((rr - gg) / d) + 4);
            }
            if (h < 0) h += 360;
            double s = max <= 0 ? 0 : d / max;
            return (h, s, max);
        }

        void Schedule()
        {
            if (_loading) return;
            _writeTimer.Stop();
            _writeTimer.Start();
        }

        void ApplyBatteryWidth()
        {
            double w = BattTrack.ActualWidth * _lastPct / 100.0;
            BattFill.Width = (double.IsNaN(w) || w < 0) ? 0 : w;
        }

        void ShowBattery(ushort mv, byte pct)
        {
            _lastMv = mv; _lastPct = pct;
            ApplyBatteryWidth();
            BattFill.Background = new SolidColorBrush(
                pct > 50 ? Color.FromRgb(0x2E, 0xCE, 0x8A) :
                pct > 20 ? Color.FromRgb(0xF0, 0xA8, 0x2A) :
                           Color.FromRgb(0xE0, 0x55, 0x55));
            BattText.Text = $"{pct}%     {mv / 1000.0:0.00} V";
        }

        void SetStatus(string s, Color c)
        {
            StatusText.Text = s;
            StatusText.Foreground = new SolidColorBrush(c);
            StatusDot.Fill = new SolidColorBrush(c);
            Log("STATUS: " + s);
        }

        static readonly Color CDim   = Color.FromRgb(0x93, 0x94, 0xA0);
        static readonly Color CGood  = Color.FromRgb(0x2E, 0xCE, 0x8A);
        static readonly Color CWarn  = Color.FromRgb(0xE0, 0x8A, 0x3A);
        static readonly Color CErr   = Color.FromRgb(0xE0, 0x55, 0x55);

        void LoadCfgToUi(Cfg c)
        {
            _loading = true;
            SelectMode(Math.Min(2, (int)c.Mode));
            SetColorFromRgb(c.R, c.G, c.B);
            BrightnessSlider.Value = c.Brightness;
            SpeedSlider.Value  = Math.Max(1, Math.Min(80, (int)c.RainbowSpeed));
            SpreadSlider.Value = Math.Min(40, (int)c.RainbowSpread);
            BreatheSlider.Value = Math.Max(3, Math.Min(40, (int)c.BreatheSpeed));
            IdleDimSlider.Value = Math.Max(1, Math.Min(120, (int)c.IdleDimS));
            IdleOffSlider.Value = Math.Max(1, Math.Min(300, (int)c.IdleOffS));
            LowBattSlider.Value = Math.Max(3000, Math.Min(4000, (int)c.LowBattMv));
            _loading = false;
        }

        Cfg ReadCfgFromUi() => new Cfg
        {
            Mode = (byte)_mode,
            R = _r, G = _g, B = _b,
            Brightness = (byte)BrightnessSlider.Value,
            RainbowSpeed = (byte)SpeedSlider.Value,
            RainbowSpread = (byte)SpreadSlider.Value,
            BreatheSpeed = (byte)BreatheSlider.Value,
            IdleDimS = (ushort)IdleDimSlider.Value,
            IdleOffS = (ushort)IdleOffSlider.Value,
            LowBattMv = (ushort)LowBattSlider.Value,
        };

        // ---- BLE --------------------------------------------------------------
        async Task ConnectWithRetryAsync()
        {
            if (_connecting) return;
            _connecting = true;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    if (await ConnectAsync()) return;
                    if (i < 4) { SetStatus($"Connecting… (retry {i + 1})", CDim); await Task.Delay(1500); }
                }
            }
            finally { _connecting = false; }
        }

        async Task<bool> ConnectAsync()
        {
            SetStatus("Searching for paired knob…", CDim);
            ReconnectBtn.IsEnabled = false;
            try
            {
                Cleanup();
                var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var devices = await DeviceInformation.FindAllAsync(selector);
                var di = devices.FirstOrDefault(d => d.Name == DEVICE_NAME);
                if (di == null) { SetStatus($"'{DEVICE_NAME}' not found — pair it first.", CErr); return false; }

                _device = await BluetoothLEDevice.FromIdAsync(di.Id);
                if (_device == null) { SetStatus("Could not open device.", CErr); return false; }
                _device.ConnectionStatusChanged += OnConnectionChanged;
                Log($"Opened '{di.Name}', status={_device.ConnectionStatus}");

                GattDeviceServicesResult? sres = null;
                for (int attempt = 1; attempt <= 6; attempt++)
                {
                    try
                    {
                        sres = await _device.GetGattServicesForUuidAsync(SVC, BluetoothCacheMode.Uncached);
                        Log($"lookup #{attempt}: status={sres.Status} count={sres.Services.Count}");
                        if (sres.Status == GattCommunicationStatus.Success && sres.Services.Count > 0) break;
                    }
                    catch (Exception ex) { Log($"lookup #{attempt} threw: {ex.Message}"); }
                    await Task.Delay(500);
                }
                if (sres == null || sres.Status != GattCommunicationStatus.Success || sres.Services.Count == 0)
                { SetStatus("Config service not found — re-pair the knob.", CErr); return false; }

                var svc = sres.Services[0];
                GattCharacteristicsResult? cres = null, tres = null;
                for (int attempt = 1; attempt <= 6; attempt++)
                {
                    try
                    {
                        cres = await svc.GetCharacteristicsForUuidAsync(CONFIG, BluetoothCacheMode.Uncached);
                        tres = await svc.GetCharacteristicsForUuidAsync(STATUS, BluetoothCacheMode.Uncached);
                        Log($"chars #{attempt}: cfg={cres.Characteristics.Count} status={tres.Characteristics.Count}");
                        if (cres.Characteristics.Count > 0 && tres.Characteristics.Count > 0) break;
                    }
                    catch (Exception ex) { Log($"chars #{attempt} threw: {ex.Message}"); }
                    await Task.Delay(500);
                }
                if (cres == null || tres == null || cres.Characteristics.Count == 0 || tres.Characteristics.Count == 0)
                { SetStatus("Characteristics missing — reconnect.", CErr); return false; }

                _configChar = cres.Characteristics[0];
                _statusChar = tres.Characteristics[0];

                // PC-state characteristic is optional (older firmware won't have it).
                try
                {
                    var pres = await svc.GetCharacteristicsForUuidAsync(PCSTATE, BluetoothCacheMode.Uncached);
                    _pcStateChar = pres.Characteristics.Count > 0 ? pres.Characteristics[0] : null;
                    Log("pcstate char present: " + (_pcStateChar != null));
                }
                catch (Exception ex) { _pcStateChar = null; Log("pcstate lookup: " + ex.Message); }

                _statusChar.ValueChanged += OnStatusChanged;
                await _statusChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);

                var rd = await _configChar.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (rd.Status == GattCommunicationStatus.Success)
                {
                    CryptographicBuffer.CopyToByteArray(rd.Value, out var bytes);
                    if (bytes.Length == 14) LoadCfgToUi(Cfg.FromBytes(bytes));
                    else Log($"config read wrong length: {bytes.Length}");
                }

                var sd = await _statusChar.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (sd.Status == GattCommunicationStatus.Success)
                {
                    CryptographicBuffer.CopyToByteArray(sd.Value, out var sb);
                    if (sb.Length >= 3) ShowBattery((ushort)(sb[0] | (sb[1] << 8)), sb[2]);
                }

                // push the current PC audio/media state so the ring syncs immediately
                if (_audio != null) { var a = _audio.Get(); _pcMuted = a.muted; }
                if (_media != null) { var m = _media.Get(); _pcPlaying = m.playing; _pcHasSession = m.has; }
                PushPcState();

                SetStatus("Connected", CGood);
                return true;
            }
            catch (Exception ex)
            {
                Log("EXCEPTION during connect:\r\n" + ex);
                SetStatus("Error: " + ex.Message, CErr);
                return false;
            }
            finally { ReconnectBtn.IsEnabled = true; }
        }

        void OnConnectionChanged(BluetoothLEDevice sender, object args)
        {
            var connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
            Dispatcher.BeginInvoke(new Action(() =>
                SetStatus(connected ? "Connected" : "Link dropped — click Reconnect",
                          connected ? CGood : CWarn)));
        }

        void OnStatusChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);
            if (data.Length >= 3)
            {
                ushort mv = (ushort)(data[0] | (data[1] << 8));
                byte pct = data[2];
                Dispatcher.BeginInvoke(new Action(() => ShowBattery(mv, pct)));
            }
        }

        async Task WriteConfigAsync()
        {
            if (_configChar == null) return;
            try
            {
                var buf = CryptographicBuffer.CreateFromByteArray(ReadCfgFromUi().ToBytes());
                var res = await _configChar.WriteValueWithResultAsync(buf, GattWriteOption.WriteWithResponse);
                if (res.Status != GattCommunicationStatus.Success)
                    SetStatus("Write failed: " + res.Status, CErr);
            }
            catch (Exception ex) { SetStatus("Write error: " + ex.Message, CErr); }
        }

        void Cleanup()
        {
            try
            {
                if (_statusChar != null) _statusChar.ValueChanged -= OnStatusChanged;
                if (_device != null) _device.ConnectionStatusChanged -= OnConnectionChanged;
                _device?.Dispose();
            }
            catch { }
            _device = null; _statusChar = null; _configChar = null; _pcStateChar = null;
        }
    }
}
