using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using MapControl;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Fork addition: see your teammates' mouse on the map.
    ///
    /// The campaign globe and the battle map are both the game's MapControl.MapBase.
    /// The game calls MapBase.MapPointerMoved(viewPoint, geo, offDisc) whenever the
    /// mouse moves over a map, already converted to a GeoPosition - so a postfix
    /// there is the whole capture side. Positions travel as lat/lon, and each
    /// player's own MapBase.TryLocationToView puts them back on screen, so a cursor
    /// lands on the same spot of the world whatever the two players' zoom and pan.
    ///
    /// Teammates only (SendToMyTeam / RelayToTeam): in PvP the other side never
    /// sees where you are pointing.
    /// </summary>
    public class PartnerCursors : MonoBehaviour
    {
        private sealed class Remote
        {
            public byte Context;
            public double Lat, Lon;
            public float Seen;
            public Vector2 Shown;     // smoothed screen position
            public bool HasShown;
        }

        private static readonly Dictionary<byte, Remote> _remotes = new();
        private static MapBase? _localMap;
        private static double _lat, _lon;
        private static byte _context;           // what we last saw locally
        private static byte _sentContext = 255;
        private static double _sentLat, _sentLon;
        private static float _lastSend;
        private static bool _patched;
        private static PartnerCursors? _drawer;

        private const float SendInterval = 0.1f;   // 10 Hz
        private const float StaleAfter   = 8f;     // hide a cursor that stopped updating

        private static bool Enabled =>
            Plugin.Instance.CfgPartnerCursors.Value && NetworkManager.Instance.IsEstablished;

        // ── Capture ───────────────────────────────────────────────────────────

        private static void EnsureSetup()
        {
            if (!_patched)
            {
                _patched = true;
                var m = AccessTools.Method(typeof(MapBase), nameof(MapBase.MapPointerMoved));
                if (m == null)
                    Plugin.Log.LogWarning("[Cursors] MapBase.MapPointerMoved not found - partner cursors off.");
                else
                    new Harmony(PluginInfo.PLUGIN_GUID + ".cursors").Patch(m,
                        postfix: new HarmonyMethod(typeof(PartnerCursors), nameof(PointerMovedPostfix)));
            }
            if (_drawer == null)
            {
                var go = new GameObject("SPMP_PartnerCursors");
                DontDestroyOnLoad(go);
                _drawer = go.AddComponent<PartnerCursors>();
            }
        }

        private static void PointerMovedPostfix(MapBase __instance, GeoPosition geo, bool offDisc)
        {
            _localMap = __instance;
            if (offDisc) { _context = 0; return; }
            _lat = geo._latitude;
            _lon = geo._longitude;
            _context = CurrentContext();
        }

        private static byte CurrentContext() =>
            Campaign.CampaignSync.Adapter.Phase == Campaign.CampaignPhase.Map ? (byte)1 : (byte)2;

        /// <summary>Plugin.Update.</summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                if (_remotes.Count > 0) _remotes.Clear();
                return;
            }
            EnsureSetup();

            // Mouse left the game window, or no map on screen: hide ours.
            if (_localMap == null || !Application.isFocused) _context = 0;

            if (Time.unscaledTime - _lastSend < SendInterval) return;
            bool moved = _context != 0 && (System.Math.Abs(_lat - _sentLat) > 1e-6 || System.Math.Abs(_lon - _sentLon) > 1e-6);
            bool heartbeat = _context != 0 && Time.unscaledTime - _lastSend > 2f;
            if (_context == _sentContext && !moved && !heartbeat) return;

            _lastSend = Time.unscaledTime;
            _sentContext = _context; _sentLat = _lat; _sentLon = _lon;
            var msg = new PartnerCursorMessage
            {
                Slot = PlayerRegistry.LocalSlot, Context = _context, Lat = _lat, Lon = _lon,
            };
            NetworkManager.Instance.SendToMyTeam(msg, DeliveryMethod.Unreliable);
        }

        internal static void OnReceived(PartnerCursorMessage msg)
        {
            if (!Plugin.Instance.CfgPartnerCursors.Value) return;
            if (msg.Slot == PlayerRegistry.LocalSlot) return;
            // Host relays a guest's cursor to that guest's other teammates.
            if (Plugin.Instance.CfgIsHost.Value)
                NetworkManager.Instance.RelayToTeam(msg.Slot, msg, DeliveryMethod.Unreliable);

            if (!_remotes.TryGetValue(msg.Slot, out var r)) _remotes[msg.Slot] = r = new Remote();
            r.Context = msg.Context;
            r.Lat = msg.Lat;
            r.Lon = msg.Lon;
            r.Seen = Time.unscaledTime;
        }

        // ── Draw ──────────────────────────────────────────────────────────────

        private static readonly Color[] Colors =
        {
            new Color(1.00f, 0.75f, 0.10f),   // amber
            new Color(0.25f, 0.85f, 1.00f),   // cyan
            new Color(0.55f, 1.00f, 0.35f),   // green
            new Color(1.00f, 0.45f, 0.85f),   // pink
        };

        private GUIStyle? _label;

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || _remotes.Count == 0) return;
            var map = _localMap;
            if (map == null) return;
            byte here = CurrentContext();

            foreach (var kv in _remotes)
            {
                var r = kv.Value;
                if (r.Context == 0 || r.Context != here) { r.HasShown = false; continue; }
                if (Time.unscaledTime - r.Seen > StaleAfter) { r.HasShown = false; continue; }

                Noesis.Point view;
                try
                {
                    if (!map.TryLocationToView(new GeoPosition(r.Lat, r.Lon), out view)) { r.HasShown = false; continue; }
                    view = map.PointToScreen(view);
                }
                catch { r.HasShown = false; continue; }

                var target = new Vector2(view.X, view.Y);
                // Ease toward the target: updates arrive at 10 Hz, frames at 60+.
                r.Shown = r.HasShown ? Vector2.Lerp(r.Shown, target, 0.35f) : target;
                r.HasShown = true;
                DrawCursor(r.Shown, Colors[kv.Key % Colors.Length], PlayerRegistry.DisplayName(kv.Key));
            }
        }

        private void DrawCursor(Vector2 p, Color c, string name)
        {
            var old = GUI.color;
            // Dark outline, then the coloured arrow-ish marker: a 4-px square tip and a
            // short tail, readable on both the dark globe and bright battle maps.
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(new Rect(p.x - 1, p.y - 1, 12, 4), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(p.x - 1, p.y - 1, 4, 12), Texture2D.whiteTexture);
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x, p.y, 10, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(p.x, p.y, 2, 10), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(p.x + 3, p.y + 3, 3, 3), Texture2D.whiteTexture);

            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            var rect = new Rect(p.x + 12, p.y + 6, 220, 22);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), name, _label);
            GUI.color = c;
            GUI.Label(rect, name, _label);
            GUI.color = old;
        }

        public static void Reset()
        {
            _remotes.Clear();
            _sentContext = 255;
        }
    }
}
