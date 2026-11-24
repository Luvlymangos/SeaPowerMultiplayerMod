using System;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Everything CampaignSync needs from a campaign mod, and nothing more.
    ///
    /// One implementation per campaign mod. The adapter is the ONLY code that knows
    /// the mod's internals (type names, methods, save format), so when the mod
    /// updates, the adapter is the only file that has to change.
    ///
    /// Threading: every member is called on the Unity main thread.
    /// </summary>
    public interface ICampaignAdapter
    {
        /// <summary>Stable id for the wire header ("bungalow-dc").</summary>
        string AdapterId { get; }

        /// <summary>True when the campaign mod is installed, enabled and the adapter
        /// recognised its version. False makes CampaignSync inert.</summary>
        bool IsAvailable { get; }

        /// <summary>The campaign mod's own version string, compared between players.</summary>
        string ModVersion { get; }

        /// <summary>Current phase of the local campaign.</summary>
        CampaignPhase Phase { get; }

        /// <summary>The campaign's continuous clock (any monotonic unit the adapter
        /// can also apply, e.g. campaign seconds since start).</summary>
        double Clock { get; }

        /// <summary>Current campaign time compression (0 = paused).</summary>
        float TimeScale { get; }

        // ── Career mode ───────────────────────────────────────────────────────
        // In Career each player is one officer holding one post; every other
        // friendly force is run by the campaign's own AI. Posts are the unit of
        // ownership: in Career, a command's AssetKey is the POST that issues it
        // (the adapter maps a fleet to its post when capturing).

        /// <summary>True when the running campaign is in Career mode.</summary>
        bool IsCareer { get; }

        /// <summary>The post the local player holds in the mod's own game (host: the
        /// host's officer). Null when none / not Career.</summary>
        string? LocalPlayerPost { get; }

        /// <summary>Posts a second player could take (key + display name).</summary>
        System.Collections.Generic.IReadOnlyList<(string key, string name)> ListPosts();

        /// <summary>
        /// HOST: a human now holds (or released) this post. The mod's friendly AI must
        /// stop commanding a post a human holds - otherwise it keeps re-tasking the
        /// guest's forces, the campaign-layer version of the bug the battle mod fixed
        /// for the remote player's taskforce (Suppression.HostSuppressesRemoteTfAi).
        /// </summary>
        void SetPostHeldByHuman(string postKey, bool held);

        /// <summary>In a campaign battle: the ownership key of the campaign asset a
        /// friendly battle unit came from - its Career post, or (no Career) its task
        /// force / squadron key. Null = unknown. Used to give each player their own
        /// units when the battle starts.</summary>
        string? OwnerKeyOfBattleUnit(SeaPower.ObjectBase unit);

        /// <summary>Raised (host side) whenever campaign state changed in a way the
        /// guest should see: a command executed, a turn/time step advanced, the enemy
        /// commander moved. CampaignSync debounces it into snapshots.</summary>
        event Action? StateChanged;

        // ── Host side ─────────────────────────────────────────────────────────

        /// <summary>
        /// Serialize what the guest's map needs. <paramref name="full"/> = a complete
        /// copy (join, after a battle, resync); otherwise only what changed since the
        /// last capture. The adapter tracks what it last sent; a full capture resets that.
        /// Return "" when there is nothing to send.
        /// </summary>
        string CaptureState(bool full);

        /// <summary>Ownership key of what is selected on the local campaign map
        /// (for the claim hotkey), or null.</summary>
        string? SelectedAssetKey();

        /// <summary>Run a command the guest asked for, through the mod's own code
        /// path (as if the host had clicked it). Return false + reason to reject.</summary>
        bool TryExecute(CampaignCommand command, out string reason);

        // ── Client side ───────────────────────────────────────────────────────

        /// <summary>
        /// Replace the local campaign state with the host's and redraw the map.
        /// <paramref name="hostClock"/> and <paramref name="hostTimeScale"/> are the
        /// host's at capture time: set the local clock and speed to them, so that
        /// between snapshots the guest's presentation-only movement (below) carries
        /// fleets forward at the host's rate.
        /// </summary>
        void ApplyState(string state, CampaignPhase phase, double hostClock, float hostTimeScale);

        /// <summary>
        /// Turn the local campaign's DECISIONS on/off: enemy commander, land war
        /// resolution, production, battle generation, random events, contact/detection
        /// rolls - anything that changes the outcome. While suppressed the local
        /// campaign draws, captures input, and may keep running PURE MOVEMENT along
        /// paths the host already set (continuous clock): that only smooths the map
        /// between snapshots, and each snapshot overwrites it. If the mod cannot
        /// separate movement from decisions, suppress everything and let snapshots
        /// alone move the map.
        /// </summary>
        void SetSimulationSuppressed(bool suppressed);

        /// <summary>Bring the guest back to the campaign map after a battle ends
        /// (the host's battle end is synced by the existing mission-end path).</summary>
        void ReturnToMap();
    }

    /// <summary>Used when no supported campaign mod is present. Everything is inert.</summary>
    internal sealed class NullCampaignAdapter : ICampaignAdapter
    {
        public static readonly NullCampaignAdapter Instance = new();

        public string AdapterId => "none";
        public bool IsAvailable => false;
        public string ModVersion => "";
        public CampaignPhase Phase => CampaignPhase.None;
        public double Clock => 0;
        public float TimeScale => 0;
        public bool IsCareer => false;
        public string? LocalPlayerPost => null;
        public System.Collections.Generic.IReadOnlyList<(string key, string name)> ListPosts() =>
            System.Array.Empty<(string, string)>();
        public void SetPostHeldByHuman(string postKey, bool held) { }
        public string? OwnerKeyOfBattleUnit(SeaPower.ObjectBase unit) => null;
        public event Action? StateChanged { add { } remove { } }

        public string CaptureState(bool full) => "";
        public string? SelectedAssetKey() => null;
        public bool TryExecute(CampaignCommand command, out string reason)
        {
            reason = "No campaign mod is loaded on the host.";
            return false;
        }
        public void ApplyState(string state, CampaignPhase phase, double hostClock, float hostTimeScale) { }
        public void SetSimulationSuppressed(bool suppressed) { }
        public void ReturnToMap() { }
    }
}
