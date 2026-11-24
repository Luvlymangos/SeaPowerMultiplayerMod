using System;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Adapter for Bungalow's Dynamic Campaign Mod (Steam Workshop 3813157776).
    ///
    /// STATUS: SKELETON. Every name marked TODO(decompile) is a placeholder until
    /// the mod's DLL has been decompiled. Until <see cref="Probe"/> finds the real
    /// types, <see cref="IsAvailable"/> is false and CampaignSync stays inert, so
    /// this file is safe to ship as-is.
    ///
    /// What has to be filled in, in order:
    ///   1. Detection   - assembly/type names, version field.
    ///   2. State       - how the mod saves a campaign (reuse it for Capture/Apply).
    ///   3. Simulation  - the update/turn loop + AI commander, to suppress on the guest.
    ///   4. Actions     - one Binding per player action on the map (see BindActions).
    ///   5. Battles     - how a battle is launched and how its result is fed back.
    /// </summary>
    public sealed class BungalowCampaignAdapter : ICampaignAdapter
    {
        // ── 1. Detection ── TODO(decompile) ──────────────────────────────────
        private const string CampaignManagerType = "TODO.DynamicCampaign.CampaignManager";

        private Type? _managerType;
        private bool  _probed;
        private bool  _available;
        private string _version = "";

        public string AdapterId => "bungalow-dc";

        public bool IsAvailable { get { Probe(); return _available; } }

        public string ModVersion { get { Probe(); return _version; } }

        public event Action? StateChanged;

        private void Probe()
        {
            if (_probed) return;
            _probed = true;

            _managerType = CampaignInterceptor.FindType(CampaignManagerType);
            if (_managerType == null) return;  // mod not installed (or names not filled in yet)

            // TODO(decompile): read the mod's version (BepInPlugin attribute, a const, or _info.ini).
            _version = _managerType.Assembly.GetName().Version?.ToString() ?? "unknown";

            BindActions();
            HookStateChanges();
            _available = true;
        }

        // ── Phase ── TODO(decompile) ─────────────────────────────────────────

        public CampaignPhase Phase
        {
            get
            {
                if (!IsAvailable) return CampaignPhase.None;
                // TODO(decompile): map the mod's own state to Map / Battle / Resolving / Ended.
                // A campaign battle is any live mission the campaign launched - likely a
                // flag on the manager, or the current mission path under the mod's folder.
                return CampaignPhase.None;
            }
        }

        // ── Clock ── TODO(decompile) ─────────────────────────────────────────
        // The campaign map runs on a continuous clock. FIRST question in phase 1:
        // is that clock the game's own GameTime / time compression? If yes, the
        // battle mod's TimeSyncManager patch (GameTime.SetTimeCompression) already
        // routes the guest's pause/speed buttons through the host and the time.speed
        // binding below is unnecessary. If the campaign keeps its own clock, read it
        // here and add a time.speed binding on its speed control.

        public double Clock => 0;       // TODO(decompile)
        public float TimeScale => 0;    // TODO(decompile)

        // ── Career ── TODO(decompile) ────────────────────────────────────────
        // Needed from the decompile: how Career stores the player's officer/post,
        // the list of posts (ships, squadrons, task forces?), and the friendly AI's
        // per-post control loop - SetPostHeldByHuman gates that loop with a Harmony
        // prefix, the same pattern as the guest-side simulation gates.

        public bool IsCareer => false;                  // TODO(decompile)
        public string? LocalPlayerPost => null;         // TODO(decompile)

        public System.Collections.Generic.IReadOnlyList<(string key, string name)> ListPosts() =>
            System.Array.Empty<(string, string)>();     // TODO(decompile)

        private readonly System.Collections.Generic.HashSet<string> _humanPosts = new();

        /// <summary>Read by the friendly-AI gate prefix (to be added).</summary>
        internal bool IsHumanPost(string postKey) => _humanPosts.Contains(postKey);

        public void SetPostHeldByHuman(string postKey, bool held)
        {
            if (held) _humanPosts.Add(postKey); else _humanPosts.Remove(postKey);
        }

        public string? OwnerKeyOfBattleUnit(SeaPower.ObjectBase unit)
        {
            // TODO(decompile): how does the mod build a battle from campaign forces?
            // Likely it writes a mission ini per fleet/post - match the unit by name,
            // taskforce or a tag the mod sets when spawning it.
            return null;
        }

        // ── 2. State ── TODO(decompile) ──────────────────────────────────────

        public string? SelectedAssetKey() => null;   // TODO(decompile)

        public string CaptureState(bool full)
        {
            // TODO(decompile): call the mod's save serializer into a string
            // (or save to a temp file and read it back - SessionManager does this
            // for battles). Exclude anything only the host's UI needs.
            return "";
        }

        public void ApplyState(string state, CampaignPhase phase, double hostClock, float hostTimeScale)
        {
            // TODO(decompile): also set the local clock to hostClock and the speed to
            // hostTimeScale, so presentation-only movement runs at the host's rate.
            // TODO(decompile): load the state through the mod's loader WITHOUT
            // re-running "campaign start" side effects, then refresh the map UI.
            // Must keep the guest's camera/selection where it was.
        }

        // ── 3. Simulation ── TODO(decompile) ─────────────────────────────────

        private bool _suppressed;

        /// <summary>Read by the guest-side gate prefixes (to be added).</summary>
        internal bool Suppressed => _suppressed && !CampaignSync.ApplyingFromNetwork;

        public void SetSimulationSuppressed(bool suppressed)
        {
            _suppressed = suppressed;
            // TODO(decompile): Harmony prefixes on the mod's turn/time advance, AI
            // commander, land war, production and random-event methods, each
            // returning !_suppressed. Pattern:
            //   [prefix] static bool Gate() => !Instance._suppressed || CampaignSync.ApplyingFromNetwork;
        }

        public void ReturnToMap()
        {
            // TODO(decompile): whatever the mod calls after a battle to show the map.
        }

        // ── 4. Actions ── TODO(decompile) ────────────────────────────────────

        private void BindActions()
        {
            // One block per player action. Example shape (names are placeholders):
            //
            // var fleetType = CampaignInterceptor.FindType("TODO.DynamicCampaign.Fleet");
            // CampaignInterceptor.Bind(new CampaignInterceptor.Binding
            // {
            //     CommandId = "fleet.move",
            //     Target    = CampaignInterceptor.FindMethod(fleetType, "SetDestination")!,
            //     Capture   = (inst, args) => new CampaignCommand { AssetKey = FleetId(inst) }
            //                     .Arg("dest", NodeId(args[0])),
            //     Execute   = (CampaignCommand c, out string why) =>
            //     {
            //         var fleet = FindFleet(c.AssetKey);
            //         if (fleet == null) { why = "Fleet no longer exists."; return false; }
            //         SetDestination.Invoke(fleet, new[] { FindNode(c.Get("dest")) });
            //         why = ""; return true;
            //     },
            // });
            //
            // Expected action list (confirm against the decompiled UI):
            //   fleet.move, fleet.patrol, fleet.split, fleet.merge, fleet.rebase,
            //   air.mission, op.launch, op.cancel, landing.plan, commander.assign,
            //   build.queue, time.speed, turn.end
        }

        public bool TryExecute(CampaignCommand command, out string reason) =>
            CampaignInterceptor.TryExecute(command, out reason);

        private void HookStateChanges()
        {
            // TODO(decompile): Harmony postfix on the mod's "state changed"/"turn
            // advanced"/"save dirty" point(s) that calls RaiseStateChanged().
            // Coarse is fine - CampaignSync debounces and skips unchanged snapshots.
        }

        internal void RaiseStateChanged() => StateChanged?.Invoke();
    }
}
