using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using AOSharp.Core;
using AOSharp.Core.UI;

namespace ManagerSync
{
    // Counts Sync callbacks only. File/chat output is restricted to boundaries.
    internal sealed class SyncBenchmark
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        private readonly uint _processId;
        private readonly string _character;
        private readonly string _path;
        private bool _failed, _active, _zoning, _lastForeground;
        private long _start, _last, _zoneStart, _zoneEnd;
        private long _foregroundCycles, _backgroundCycles;
        private double _foregroundSeconds, _backgroundSeconds, _maxGap;
        private string _area, _startedUtc, _summary, _transition;

        internal SyncBenchmark(string directory, string character, string version)
        {
            _character = character;
            try
            {
                _path = Path.Combine(directory, "sync-benchmark.log");
                using (var process = Process.GetCurrentProcess())
                    _processId = (uint)process.Id;
                Write("LOAD " + version + " engine=" + (Game.IsNewEngine ? "new" : "classic")
                    + " pid=" + _processId + " metric=Sync.OnUpdate");
                Chat.WriteLine("[Sync bench] Area summaries enabled. Log: " + _path);
            }
            catch (Exception error) { Fail(error); }
        }

        private static long Now => Stopwatch.GetTimestamp();
        private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
        private static string Number(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
        private static string Rate(long cycles, double seconds) =>
            seconds > 0 ? (cycles / seconds).ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

        private bool Foreground()
        {
            uint processId;
            GetWindowThreadProcessId(GetForegroundWindow(), out processId);
            return processId == _processId;
        }

        private void Write(string text)
        {
            File.AppendAllText(_path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                + " char=" + _character + " " + text + Environment.NewLine);
        }

        private void Account(long now)
        {
            double elapsed = Seconds(now - _last);
            if (_lastForeground) _foregroundSeconds += elapsed;
            else _backgroundSeconds += elapsed;
            if (elapsed > _maxGap) _maxGap = elapsed;
            _last = now;
        }

        internal void Tick()
        {
            if (_failed) return;
            try
            {
                if (_zoning || Game.IsZoning) return;
                long now = Now;
                bool foreground = Foreground();
                if (!_active)
                {
                    _area = Playfield.ModelIdentity.Instance + "/" + Playfield.Identity.Instance;
                    _startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    _start = _last = now;
                    _foregroundCycles = _backgroundCycles = 0;
                    _foregroundSeconds = _backgroundSeconds = _maxGap = 0;
                    _lastForeground = foreground;
                    _active = true;
                    Write("AREA_START area=" + _area + " started_utc=" + _startedUtc
                        + (_zoneEnd != 0 ? " first_update_after_zone_end_s=" + Number(Seconds(now - _zoneEnd)) : ""));
                    if (_summary != null)
                        Chat.WriteLine("[Sync bench] " + _character + " " + _summary + _transition);
                    _summary = _transition = null;
                    _zoneEnd = 0;
                }
                Account(now);
                if (foreground) _foregroundCycles++;
                else _backgroundCycles++;
                _lastForeground = foreground;
            }
            catch (Exception error) { Fail(error); }
        }

        private void FinishArea(string reason, long now)
        {
            if (!_active) return;
            Account(now);
            double elapsed = Seconds(now - _start);
            long cycles = _foregroundCycles + _backgroundCycles;
            _summary = "area=" + _area + " cycles=" + cycles + " seconds=" + Number(elapsed)
                + " FG=" + _foregroundCycles + "@" + Rate(_foregroundCycles, _foregroundSeconds)
                + "Hz BG=" + _backgroundCycles + "@" + Rate(_backgroundCycles, _backgroundSeconds)
                + "Hz max_gap_s=" + Number(_maxGap);
            Write("AREA_END reason=" + reason + " started_utc=" + _startedUtc + " " + _summary
                + " foreground_s=" + Number(_foregroundSeconds)
                + " background_s=" + Number(_backgroundSeconds));
            _active = false;
        }

        internal void ZoneStarted()
        {
            if (_failed || _zoning) return;
            try
            {
                _zoneStart = Now;
                FinishArea("zone_start", _zoneStart);
                _zoning = true;
                Write("ZONE_START from=" + _area);
            }
            catch (Exception error) { Fail(error); }
        }

        internal void ZoneEnded(bool failed)
        {
            if (_failed || !_zoning) return;
            try
            {
                _zoneEnd = Now;
                _transition = " zone_s=" + Number(Seconds(_zoneEnd - _zoneStart))
                    + (failed ? " zone_failed=true" : "");
                Write((failed ? "ZONE_FAILED" : "ZONE_END") + " from=" + _area + _transition);
                _zoning = false;
            }
            catch (Exception error) { Fail(error); }
        }

        internal void PlayfieldInitialized()
        {
            if (_failed || _zoning) return;
            try { FinishArea("playfield_init", Now); }
            catch (Exception error) { Fail(error); }
        }

        internal void Stop()
        {
            if (_failed) return;
            try
            {
                FinishArea("unload", Now);
                if (_zoning) Write("UNLOAD_DURING_ZONE observed_zone_s=" + Number(Seconds(Now - _zoneStart)));
                Write("UNLOAD");
            }
            catch (Exception error) { Fail(error); }
        }

        private void Fail(Exception error)
        {
            _failed = true;
            try { Chat.WriteLine("[Sync bench] Stopped: " + error.Message + ". Log: " + _path); }
            catch (Exception) { }
        }
    }
}
