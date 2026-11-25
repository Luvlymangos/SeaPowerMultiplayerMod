using System;
using System.Collections.Generic;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Both Dynamic Campaign mods can be loaded at once (each runs only its own
    /// campaigns), so CampaignSync talks to this, and this forwards to whichever
    /// one matters right now:
    ///   - the host: the one whose campaign is open;
    ///   - a guest: the one the host's updates name (so a guest still on the menu,
    ///     or in the other mod's campaign, loads the right one).
    /// Commands carry their adapter's id ("dcm-fork:Orders.SetCourse/...") and go
    /// to that adapter whatever is active.
    /// </summary>
    internal sealed class MultiCampaignAdapter : ICampaignAdapter
    {
        private readonly ICampaignAdapter[] _all;
        private ICampaignAdapter? _last;
        private string? _preferred;

        public MultiCampaignAdapter(params ICampaignAdapter[] all)
        {
            _all = all;
            foreach (var a in _all) a.StateChanged += () => { if (ReferenceEquals(a, Active)) StateChanged?.Invoke(); };
        }

        /// <summary>Guest: the host's campaign is this adapter's.</summary>
        public void Prefer(string adapterId)
        {
            if (_preferred == adapterId) return;
            _preferred = adapterId;
            Plugin.Log.LogInfo($"[Campaign] Following the host's campaign mod '{adapterId}'.");
        }

        private ICampaignAdapter Active
        {
            get
            {
                if (_preferred != null && !Plugin.Instance.CfgIsHost.Value)
                    foreach (var a in _all) if (a.AdapterId == _preferred && a.IsAvailable) return _last = a;
                foreach (var a in _all) if (a.IsAvailable && a.Phase != CampaignPhase.None) return _last = a;
                if (_last != null) return _last;
                foreach (var a in _all) if (a.IsAvailable) return a;
                return NullCampaignAdapter.Instance;
            }
        }

        private ICampaignAdapter ForCommand(CampaignCommand c)
        {
            int i = c.Id.IndexOf(':');
            if (i > 0)
            {
                string id = c.Id.Substring(0, i);
                foreach (var a in _all) if (a.AdapterId == id && a.IsAvailable) return a;
            }
            return Active;
        }

        public string AdapterId => Active.AdapterId;
        public bool IsAvailable { get { bool any = false; foreach (var a in _all) any |= a.IsAvailable; return any; } }
        public string ModVersion => Active.ModVersion;
        public CampaignPhase Phase => Active.Phase;
        public double Clock => Active.Clock;
        public float TimeScale => Active.TimeScale;
        public bool IsCareer => Active.IsCareer;
        public string? LocalPlayerPost => Active.LocalPlayerPost;
        public IReadOnlyList<(string key, string name)> ListPosts() => Active.ListPosts();
        public void SetPostHeldByHuman(string postKey, bool held) => Active.SetPostHeldByHuman(postKey, held);
        public string? OwnerKeyOfBattleUnit(SeaPower.ObjectBase unit) => Active.OwnerKeyOfBattleUnit(unit);
        public event Action? StateChanged;
        public string CaptureState(bool full) => Active.CaptureState(full);
        public string? SelectedAssetKey() => Active.SelectedAssetKey();
        public bool IsLoading => Active.IsLoading;
        public string? SelectedAssetName() => Active.SelectedAssetName();
        public bool TryExecute(CampaignCommand command, out string reason) => ForCommand(command).TryExecute(command, out reason);
        public void Tick() { foreach (var a in _all) if (a.IsAvailable) a.Tick(); }
        public void ApplyState(string state, CampaignPhase phase, double hostClock, float hostTimeScale) =>
            Active.ApplyState(state, phase, hostClock, hostTimeScale);

        /// <summary>A guest runs no campaign of either mod: the host runs it.</summary>
        public void SetSimulationSuppressed(bool suppressed) { foreach (var a in _all) if (a.IsAvailable) a.SetSimulationSuppressed(suppressed); }
        public void ReturnToMap() => Active.ReturnToMap();
        public System.Collections.IEnumerator? LeaveMapForBattle()
        {
            foreach (var a in _all) if (a.IsAvailable && a.LeaveMapForBattle() is { } e) return e;
            return null;
        }
    }
}
