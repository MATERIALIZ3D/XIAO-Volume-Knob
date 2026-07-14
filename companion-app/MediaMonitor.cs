// Watches the Windows System Media Transport Controls (the media overlay) for the
// real play/pause state of whatever app is playing (Spotify, browsers, etc.).
using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace KnobConfig
{
    class MediaMonitor
    {
        GlobalSystemMediaTransportControlsSessionManager? _mgr;
        GlobalSystemMediaTransportControlsSession? _session;

        public event Action<bool, bool>? Changed;   // (playing, hasSession)

        public async Task StartAsync()
        {
            try
            {
                _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mgr.CurrentSessionChanged += (s, e) => Hook();
                Hook();
            }
            catch { /* SMTC unavailable — leave hasSession=false */ }
        }

        void Hook()
        {
            if (_session != null) { try { _session.PlaybackInfoChanged -= OnPlayback; } catch { } }
            _session = _mgr?.GetCurrentSession();
            if (_session != null) _session.PlaybackInfoChanged += OnPlayback;
            Emit();
        }

        void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Emit();

        void Emit()
        {
            var (p, h) = Get();
            Changed?.Invoke(p, h);
        }

        public (bool playing, bool has) Get()
        {
            if (_session == null) return (false, false);
            try
            {
                var info = _session.GetPlaybackInfo();
                bool playing = info != null &&
                    info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                return (playing, true);
            }
            catch { return (false, true); }
        }
    }
}
