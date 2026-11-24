using System.Collections.Generic;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Who may command which campaign asset. Host-owned table, mirrored to the guest.
    ///
    /// Without this, two players on one map issue conflicting orders to the same
    /// fleet and the last packet wins - the campaign-layer version of the ally-lock
    /// problem UnitLockManager solves in battle. Default is Shared so the feature
    /// works before anyone assigns anything; the host narrows it from there.
    ///
    /// The GUEST checks too, before sending, so a refused action gives instant
    /// feedback instead of a round trip. The host's check is the one that counts.
    /// </summary>
    public static class CampaignOwnership
    {
        private static readonly Dictionary<string, CampaignSeat> _owners = new();
        private static CampaignSeat _default = CampaignSeat.Shared;

        /// <summary>Raised on the host when the table changes, so it can be broadcast.</summary>
        internal static event System.Action? Changed;

        public static CampaignSeat DefaultOwner => _default;

        /// <summary>Each seat's colour - the same on both screens, and the same as the
        /// player's map cursor (slot 0 = host amber, slot 1 = guest cyan).</summary>
        public static UnityEngine.Color SeatColor(CampaignSeat seat) => seat switch
        {
            CampaignSeat.Host   => new UnityEngine.Color(1.00f, 0.75f, 0.10f),   // amber
            CampaignSeat.Guest  => new UnityEngine.Color(0.25f, 0.85f, 1.00f),   // cyan
            CampaignSeat.Guest2 => new UnityEngine.Color(0.55f, 1.00f, 0.35f),   // green
            CampaignSeat.Guest3 => new UnityEngine.Color(1.00f, 0.45f, 0.85f),   // pink
            _                  => new UnityEngine.Color(0.85f, 0.85f, 0.85f),
        };

        /// <summary>The asset/post the given seat holds, if exactly one is assigned
        /// to it (Career: each player's post).</summary>
        public static string? HeldBy(CampaignSeat seat)
        {
            foreach (var kv in _owners) if (kv.Value == seat) return kv.Key;
            return null;
        }

        public static CampaignSeat OwnerOf(string assetKey)
        {
            if (string.IsNullOrEmpty(assetKey)) return CampaignSeat.Shared;
            return _owners.TryGetValue(assetKey, out var s) ? s : _default;
        }

        public static bool MayCommand(CampaignSeat seat, string assetKey)
        {
            var owner = OwnerOf(assetKey);
            return owner == CampaignSeat.Shared || owner == seat;
        }

        // ── Host-side edits ───────────────────────────────────────────────────

        public static void Assign(string assetKey, CampaignSeat owner)
        {
            if (string.IsNullOrEmpty(assetKey)) return;
            if (_owners.TryGetValue(assetKey, out var cur) && cur == owner) return;
            _owners[assetKey] = owner;
            Changed?.Invoke();
        }

        /// <summary>Hand everything one seat holds to another (e.g. guest left).</summary>
        public static int ReassignAll(CampaignSeat from, CampaignSeat to)
        {
            var keys = new List<string>();
            foreach (var kv in _owners) if (kv.Value == from) keys.Add(kv.Key);
            foreach (var k in keys)
            {
                if (to == CampaignSeat.Shared && _default == CampaignSeat.Shared) _owners.Remove(k);
                else _owners[k] = to;
            }
            if (keys.Count > 0) Changed?.Invoke();
            return keys.Count;
        }

        public static void SetDefault(CampaignSeat owner)
        {
            if (_default == owner) return;
            _default = owner;
            Changed?.Invoke();
        }

        // ── Wire ──────────────────────────────────────────────────────────────

        internal static CampaignOwnershipMessage ToMessage()
        {
            var msg = new CampaignOwnershipMessage { DefaultOwner = _default };
            foreach (var kv in _owners) msg.Rows.Add((kv.Key, kv.Value));
            return msg;
        }

        /// <summary>Client: replace the mirror with the host's table.</summary>
        internal static void ApplyReceived(CampaignOwnershipMessage msg)
        {
            _owners.Clear();
            _default = msg.DefaultOwner;
            for (int i = 0; i < msg.Rows.Count; i++)
                _owners[msg.Rows[i].assetKey] = msg.Rows[i].owner;
            Plugin.Log.LogInfo($"[Campaign] Ownership table received: {msg.Rows.Count} rows, default={msg.DefaultOwner}");
        }

        internal static void Reset()
        {
            _owners.Clear();
            _default = CampaignSeat.Shared;
        }
    }
}
