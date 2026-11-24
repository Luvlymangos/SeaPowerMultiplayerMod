using UnityEngine;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Fork addition: a small panel on the co-op campaign map. Select a task force or
    /// flight and it shows who commands it, with a Claim / Release button (the same
    /// as Ctrl+F7). Hidden when nothing is selected or the campaign isn't shared.
    /// </summary>
    public class ClaimPanel : MonoBehaviour
    {
        private static ClaimPanel? _instance;

        private string? _key, _name;
        private float _nextLook;
        private GUIStyle? _box, _title, _status, _button;

        public static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("SPMP_ClaimPanel");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ClaimPanel>();
        }

        private void Update()
        {
            // Reflection lookups: a few times a second is plenty.
            if (Time.unscaledTime < _nextLook) return;
            _nextLook = Time.unscaledTime + 0.2f;
            if (!CampaignSync.IsActive || CampaignSync.Adapter.Phase != CampaignPhase.Map) { _key = null; return; }
            _key = CampaignSync.Adapter.SelectedAssetKey();
            _name = _key == null ? null : CampaignSync.Adapter.SelectedAssetName();
        }

        private void OnGUI()
        {
            if (_key == null) return;
            if (!CampaignSync.IsActive || CampaignSync.Adapter.Phase != CampaignPhase.Map) return;

            Styles();
            var owner = CampaignOwnership.OwnerOf(_key);
            bool isHost = Plugin.Instance.CfgIsHost.Value;
            var me = isHost ? CampaignSeat.Host : CampaignSeat.Guest;
            string partner = PartnerName();

            string status;
            Color tint;
            if (owner == me)                       { status = "Commanded by you";               tint = new Color(0.45f, 0.9f, 0.45f); }
            else if (owner == CampaignSeat.Shared) { status = "Shared - either of you can order it"; tint = new Color(0.85f, 0.85f, 0.85f); }
            else                                   { status = $"Commanded by {partner}";         tint = new Color(1f, 0.75f, 0.3f); }

            const float w = 300f, h = 86f;
            var r = new Rect((Screen.width - w) / 2f, Screen.height - h - 110f, w, h);
            GUI.Box(r, GUIContent.none, _box);

            GUI.Label(new Rect(r.x + 12, r.y + 8, w - 24, 22), string.IsNullOrEmpty(_name) ? _key : _name, _title);
            var old = GUI.color;
            GUI.color = tint;
            GUI.Label(new Rect(r.x + 12, r.y + 30, w - 24, 20), status, _status);
            GUI.color = old;

            var btn = new Rect(r.x + 12, r.y + h - 32, w - 24, 24);
            if (owner == me)
            {
                if (GUI.Button(btn, "Release (Ctrl+F7)", _button)) CampaignSync.ClaimLocal(_key, claim: false);
            }
            else if (owner == CampaignSeat.Shared)
            {
                if (GUI.Button(btn, "Claim - only you can order it (Ctrl+F7)", _button)) CampaignSync.ClaimLocal(_key, claim: true);
            }
            else
            {
                GUI.enabled = false;
                GUI.Button(btn, $"Ask {partner} to release it", _button);
                GUI.enabled = true;
            }
        }

        private static string PartnerName()
        {
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].Connected && all[i].Slot != PlayerRegistry.LocalSlot)
                    return all[i].DisplayName;
            return "your partner";
        }

        private void Styles()
        {
            if (_box != null) return;
            var bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.11f, 0.88f));
            bg.Apply();
            _box = new GUIStyle(GUI.skin.box) { normal = { background = bg } };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _status = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Color.white } };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        }
    }
}
