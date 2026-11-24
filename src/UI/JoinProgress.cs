using System.Collections.Generic;
using LiteNetLib.Utils;
using SeapowerMultiplayer.Campaign;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Fork addition: a progress bar while a guest joins.
    ///
    /// The guest works out how far it has got - connecting, handshake, mods, the
    /// host's campaign arriving and loading - shows it, and reports it to the host
    /// a couple of times a second so the host sees the same bar for that player.
    /// It disappears a few seconds after the guest is ready (or stays, saying so,
    /// when a restart is needed for new mods).
    /// </summary>
    public class JoinProgress : MonoBehaviour
    {
        private struct State
        {
            public float Percent;     // 0..100, < 0 = indeterminate
            public string Text;
            public bool Done;
        }

        private static JoinProgress? _instance;
        private static State _local;
        private static bool _localActive;
        private static float _localDoneAt = -1f;
        private static float _lastReport;
        private static bool _wasJoining;
        private static float _establishedAt = -1f;

        private sealed class Remote { public float Percent; public string Text = ""; public float Seen; public bool Done; public float DoneAt = -1f; }
        private static readonly Dictionary<byte, Remote> _remotes = new();

        private const float HideAfterDone = 4f;

        // ── Guest: compute ────────────────────────────────────────────────────

        private static State Compute()
        {
            var nm = NetworkManager.Instance;
            if (nm.IsJoining && !nm.IsConnected) return new State { Percent = -1, Text = "Connecting to the host…" };
            if (nm.IsJoining)                     return new State { Percent = 10, Text = "Connected - checking versions…" };

            if (Plugin.Instance.CfgModSync.Value)
            {
                if (!ModSync.ListComplete)
                    return new State { Percent = 15 + 10 * ModSync.ListFraction, Text = "Comparing mods with the host…" };
                if (ModSync.DownloadsLeft > 0)
                {
                    int total = Mathf.Max(1, ModSync.DownloadsTotal), done = total - ModSync.DownloadsLeft;
                    return new State { Percent = 25 + 30f * done / total, Text = $"Downloading the host's mods ({done}/{total})…" };
                }
                if (ModSync.RestartNeeded)
                    return new State { Percent = 100, Text = "Mods updated - restart Sea Power and rejoin.", Done = true };
            }

            var adapter = CampaignSync.Adapter;
            if (!CampaignSync.IsActive)
                return new State { Percent = 100, Text = "Connected.", Done = true };

            if (adapter.IsLoading) return new State { Percent = -1, Text = "Loading the host's campaign…" };
            var (got, of) = CampaignSync.Receiving;
            if (of > 0) return new State { Percent = 55 + 30f * got / of, Text = $"Receiving the host's campaign ({got * 100 / of}%)…" };
            if (!CampaignSync.AppliedAny)
            {
                // Host isn't in a campaign: don't sit on a bar forever - this is also
                // how an ordinary co-op mission starts.
                if (_establishedAt > 0 && Time.unscaledTime - _establishedAt > 15f)
                    return new State { Percent = 100, Text = "Connected. The campaign loads when the host opens Pacific 2027.", Done = true };
                return new State { Percent = 55, Text = "Waiting for the host's campaign…" };
            }
            if (adapter.Phase != CampaignPhase.Map && CampaignSync.HostPhase == CampaignPhase.Map)
                return new State { Percent = 90, Text = "Opening the campaign map…" };
            return new State { Percent = 100, Text = "Ready - you're in the host's campaign.", Done = true };
        }

        /// <summary>Plugin.Update.</summary>
        public static void Tick()
        {
            EnsureCreated();
            var nm = NetworkManager.Instance;
            bool isGuest = !Plugin.Instance.CfgIsHost.Value;

            if (nm.IsEstablished && _establishedAt < 0) _establishedAt = Time.unscaledTime;
            if (!nm.IsEstablished) _establishedAt = -1f;

            if (isGuest && (nm.IsJoining || nm.IsEstablished))
            {
                if (!_wasJoining) { _wasJoining = true; _localActive = true; _localDoneAt = -1f; }
                if (_localActive)
                {
                    _local = Compute();
                    if (_local.Done && _localDoneAt < 0) _localDoneAt = Time.unscaledTime;
                    if (!_local.Done) _localDoneAt = -1f;
                    bool keep = !_local.Done || ModSync.RestartNeeded || Time.unscaledTime - _localDoneAt < HideAfterDone;

                    if (nm.IsEstablished && Time.unscaledTime - _lastReport > 0.5f)
                    {
                        _lastReport = Time.unscaledTime;
                        nm.SendToServer(new JoinProgressMessage { Percent = _local.Percent, Text = _local.Text, Done = _local.Done });
                    }
                    if (!keep) _localActive = false;
                }
            }
            else if (!nm.IsJoining && !nm.IsEstablished)
            {
                _wasJoining = false;
                _localActive = false;
            }
        }

        // ── Host: hear about guests ───────────────────────────────────────────

        internal static void OnReport(byte slot, JoinProgressMessage msg)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!_remotes.TryGetValue(slot, out var r)) _remotes[slot] = r = new Remote();
            r.Percent = msg.Percent;
            r.Text = msg.Text;
            r.Seen = Time.unscaledTime;
            if (msg.Done && !r.Done) r.DoneAt = Time.unscaledTime;
            r.Done = msg.Done;
        }

        public static void Reset()
        {
            _remotes.Clear();
            _wasJoining = false;
            _localActive = false;
        }

        // ── Draw ──────────────────────────────────────────────────────────────

        private static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("SPMP_JoinProgress");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<JoinProgress>();
        }

        private GUIStyle? _box, _title, _text;
        private Texture2D? _bg, _barBg, _barFill;

        private void OnGUI()
        {
            var rows = new List<(string title, State s)>();
            if (_localActive) rows.Add(("Joining the host", _local));
            foreach (var kv in _remotes)
            {
                var r = kv.Value;
                if (Time.unscaledTime - r.Seen > 10f) continue;                       // stopped reporting
                if (r.Done && Time.unscaledTime - r.DoneAt > HideAfterDone) continue;
                rows.Add(($"{PlayerRegistry.DisplayName(kv.Key)} is joining", new State { Percent = r.Percent, Text = r.Text, Done = r.Done }));
            }
            if (rows.Count == 0) return;

            Styles();
            const float w = 420f, rowH = 64f;
            float h = 32f + rowH * rows.Count;
            var box = new Rect((Screen.width - w) / 2f, 70f, w, h);
            GUI.Box(box, GUIContent.none, _box);
            GUI.Label(new Rect(box.x + 14, box.y + 8, w - 28, 20), "Dynamic Campaign MP", _title);

            for (int i = 0; i < rows.Count; i++)
            {
                var (title, s) = rows[i];
                float y = box.y + 32 + i * rowH;
                GUI.Label(new Rect(box.x + 14, y, w - 28, 20), title, _title);
                GUI.Label(new Rect(box.x + 14, y + 18, w - 28, 20), s.Text, _text);

                var bar = new Rect(box.x + 14, y + 42, w - 28, 10);
                GUI.DrawTexture(bar, _barBg);
                if (s.Percent < 0)
                {
                    // Indeterminate: a block sliding back and forth.
                    float t = Mathf.PingPong(Time.unscaledTime * 0.6f, 1f);
                    float bw = bar.width * 0.25f;
                    GUI.DrawTexture(new Rect(bar.x + (bar.width - bw) * t, bar.y, bw, bar.height), _barFill);
                }
                else
                {
                    GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(s.Percent / 100f), bar.height), _barFill);
                }
            }
        }

        private void Styles()
        {
            if (_box != null) return;
            _bg = Solid(new Color(0.08f, 0.09f, 0.11f, 0.92f));
            _barBg = Solid(new Color(1f, 1f, 1f, 0.12f));
            _barFill = Solid(new Color(0.30f, 0.70f, 1.00f, 1f));
            _box = new GUIStyle(GUI.skin.box) { normal = { background = _bg } };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _text = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(0.85f, 0.88f, 0.92f) } };
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}

namespace SeapowerMultiplayer.Messages
{
    /// <summary>Guest → host: how far the guest has got joining.</summary>
    public class JoinProgressMessage : INetMessage
    {
        public float  Percent;   // < 0 = indeterminate
        public string Text = "";
        public bool   Done;

        public MessageType Type => MessageType.JoinProgress;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Percent);
            w.Put(Text.Length > 200 ? Text.Substring(0, 200) : Text);
            w.Put(Done);
        }

        public static JoinProgressMessage Deserialize(NetDataReader r) => new()
        {
            Percent = r.GetFloat(),
            Text = r.GetString(),
            Done = r.GetBool(),
        };
    }
}
