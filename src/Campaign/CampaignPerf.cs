using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Main-thread cost of the campaign sync, so a "it feels laggy" report comes with
    /// numbers: every minute, the log gets each piece's calls, average and worst time
    /// (only when something took over 2 ms). A frame at 60 fps is 16.7 ms.
    /// </summary>
    internal static class CampaignPerf
    {
        private sealed class Stat { public int Calls; public double Total, Max; }
        private static readonly Dictionary<string, Stat> _stats = new();
        private static float _nextReport = 60f;

        public static long Start() => Stopwatch.GetTimestamp();

        public static void Stop(string what, long started)
        {
            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (!_stats.TryGetValue(what, out var s)) _stats[what] = s = new Stat();
            s.Calls++; s.Total += ms; if (ms > s.Max) s.Max = ms;
        }

        public static void Tick()
        {
            if (Time.unscaledTime < _nextReport) return;
            _nextReport = Time.unscaledTime + 60f;
            var parts = new List<string>();
            bool worth = false;
            foreach (var kv in _stats)
            {
                var s = kv.Value;
                if (s.Calls == 0) continue;
                if (s.Max > 2.0) worth = true;
                parts.Add($"{kv.Key} {s.Calls}x avg {s.Total / s.Calls:0.0} ms max {s.Max:0.0} ms");
            }
            _stats.Clear();
            float fps = Time.smoothDeltaTime > 0 ? 1f / Time.smoothDeltaTime : 0f;
            if (worth) Plugin.Log.LogInfo($"[Perf] last minute ({fps:0} fps): {string.Join("; ", parts)}");
        }
    }
}
