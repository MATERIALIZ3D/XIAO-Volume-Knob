// Reads / controls the Windows default output device master volume + mute
// (Core Audio via NAudio) and raises Changed when either moves.
using System;
using NAudio.CoreAudioApi;

namespace KnobConfig
{
    class AudioMonitor : IDisposable
    {
        readonly MMDeviceEnumerator _enum = new MMDeviceEnumerator();
        MMDevice? _device;
        string? _deviceId;

        public event Action<int, bool>? Changed;   // (volume 0-100, muted)

        public AudioMonitor() { Attach(); }

        void Attach()
        {
            Detach();
            try
            {
                _device = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceId = _device.ID;
                _device.AudioEndpointVolume.OnVolumeNotification += OnVol;
            }
            catch { _device = null; _deviceId = null; }
        }

        void Detach()
        {
            if (_device != null)
            {
                try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVol; } catch { }
                try { _device.Dispose(); } catch { }
                _device = null;
            }
        }

        void OnVol(AudioVolumeNotificationData d)
            => Changed?.Invoke((int)Math.Round(d.MasterVolume * 100), d.Muted);

        // Polled periodically: if the default output device changed, re-attach.
        public bool RefreshDevice()
        {
            try
            {
                var def = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                bool changed = def.ID != _deviceId;
                def.Dispose();
                if (changed) Attach();
                return changed;
            }
            catch { return false; }
        }

        public (int vol, bool muted) Get()
        {
            if (_device == null) return (0, false);
            try
            {
                var v = _device.AudioEndpointVolume;
                return ((int)Math.Round(v.MasterVolumeLevelScalar * 100), v.Mute);
            }
            catch { return (0, false); }
        }

        public void SetVolume(int pct)
        {
            if (_device == null) return;
            try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(pct, 0, 100) / 100f; }
            catch { }
        }

        public void ToggleMute()
        {
            if (_device == null) return;
            try { _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute; } catch { }
        }

        public void Dispose() { Detach(); try { _enum.Dispose(); } catch { } }
    }
}
