using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Adapter for "Dynamic Campaign Mod (fork)" - Pacific 2027 - Steam Workshop
    /// 3820272150, DynamicCampaignMod.Fork.dll, namespace DynamicCampaignModFork.
    /// A standalone fork of Bungalow's mod (based on its 0.8.x line), written by
    /// another author with Bungalow's permission.
    ///
    /// No Career mode: one player normally commands the whole side. Co-op splits it
    /// by CLAIMING task forces and squadrons (CampaignSync.ClaimLocal); everything
    /// starts Shared. Ownership keys: "tf:{Id}" for task forces, "sq:{Id}" for
    /// squadrons.
    ///
    /// Everything here is reflection: the fork is not referenced at compile time,
    /// so the multiplayer mod still loads (and this adapter stays inert) for players
    /// who don't have it.
    ///
    /// STATUS (phase 1): every name below is verified against fork 0.25.0's IL.
    /// <see cref="Ready"/> keeps the adapter unavailable until phase 2 (state
    /// capture/apply) is implemented and tested - until then CampaignSync stays
    /// inert, exactly as in test build 1.
    /// </summary>
    public sealed class DynamicCampaignForkAdapter : ICampaignAdapter
    {
        /// <summary>Flip to true once CaptureState/ApplyState work (phase 2).</summary>
        private const bool Ready = false;

        // ── Verified type and member names (fork 0.25.0) ─────────────────────
        private const string NsRoot           = "DynamicCampaignModFork.";
        private const string TCampaignSession = NsRoot + "Game.Session.CampaignSession"; // static: Active, Sim, Mirror, UI, Load(def, state, ...), LeaveForBattle(bool)
        private const string TBattleSession   = NsRoot + "Game.Battle.BattleSession";    // static: Active, Plan (BattlePlan)
        private const string TCampaignSim     = NsRoot + "Core.Sim.CampaignSim";         // State, Orders, Battles, AI; int Run(double seconds); Advance(DateTime)
        private const string TCampaignState   = NsRoot + "Core.State.CampaignState";     // Now (DateTime), TaskForces, Squadrons, Victor ...
        private const string TOrders          = NsRoot + "Core.Sim.Orders";              // player orders, each returns an error string or null

        private Type? _session, _battle, _sim;
        private bool _probed, _found;
        private string _version = "";

        public string AdapterId => "dcm-fork";

        public bool IsAvailable { get { Probe(); return Ready && _found; } }

        public string ModVersion { get { Probe(); return _version; } }

        public event Action? StateChanged;

        private void Probe()
        {
            if (_probed) return;
            _probed = true;
            _session = CampaignInterceptor.FindType(TCampaignSession);
            _battle  = CampaignInterceptor.FindType(TBattleSession);
            _sim     = CampaignInterceptor.FindType(TCampaignSim);
            if (_session == null || _sim == null) return;   // fork not installed

            var asm = _session.Assembly;
            _version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString() ?? "unknown";
            _found = true;
            Plugin.Log.LogInfo($"[Campaign] Found Dynamic Campaign Mod (fork) {_version}" +
                               (Ready ? "" : " - co-op campaign support not finished yet, staying inert."));
        }

        // ── Session / phase ───────────────────────────────────────────────────

        private bool StaticBool(Type? t, string prop) =>
            t != null && Traverse.Create(t).Property(prop).GetValue<bool>();

        private object? Sim => _session == null ? null : Traverse.Create(_session).Property("Sim").GetValue();

        private object? State => Sim == null ? null : Traverse.Create(Sim).Field("State").GetValue();

        public CampaignPhase Phase
        {
            get
            {
                Probe();
                if (!_found) return CampaignPhase.None;
                if (StaticBool(_battle, "Active")) return CampaignPhase.Battle;
                if (!StaticBool(_session, "Active")) return CampaignPhase.None;
                var state = State;
                if (state != null && !string.IsNullOrEmpty(Traverse.Create(state).Field("Victor").GetValue<string>()))
                    return CampaignPhase.Ended;
                return CampaignPhase.Map;
            }
        }

        // ── Clock: the campaign runs on the game's own GameTime ───────────────

        public double Clock
        {
            get
            {
                var state = State;
                if (state == null) return 0;
                var now = Traverse.Create(state).Field("Now").GetValue<DateTime>();
                return (now - DateTime.MinValue).TotalSeconds;
            }
        }

        public float TimeScale => GameTime.IsPaused() ? 0f : GameTime.TimeCompression;

        // ── No Career in the fork ─────────────────────────────────────────────

        public bool IsCareer => false;
        public string? LocalPlayerPost => null;
        public IReadOnlyList<(string key, string name)> ListPosts() => Array.Empty<(string, string)>();
        public void SetPostHeldByHuman(string postKey, bool held) { }

        public static string TaskForceKey(int id) => "tf:" + id;
        public static string SquadronKey(string id) => "sq:" + id;

        public string? OwnerKeyOfBattleUnit(ObjectBase unit)
        {
            // TODO(phase 4): BattleSession.Plan is a BattlePlan whose units are
            // BattleUnit { TaskForce, Squadron, Flight, Tag, GroupName, ... }. Find how
            // the fork labels the spawned game unit (Tag / GroupName -> unit name or
            // mission ini section), match it, and return TaskForceKey(bu.TaskForce.Id)
            // or SquadronKey(bu.Squadron.Id).
            return null;
        }

        // ── State (phase 2) ───────────────────────────────────────────────────

        public string CaptureState()
        {
            // TODO(phase 2): serialize CampaignSim.State with the fork's own save
            // settings (CampaignSave / Saves - Newtonsoft, PreserveReferences gives
            // the "$id" fields). Full copy: ~1.6 MB raw, ~154 KB gzipped (Pacific
            // 2027, June 2027). Per-second updates must be per-task-force deltas with
            // "$id" stripped - see the plan's Phase 1 findings.
            return "";
        }

        public void ApplyState(string state, CampaignPhase phase, double hostClock, float hostTimeScale)
        {
            // TODO(phase 2): full copy -> CampaignSession.Load(definition, state, ...);
            // deltas -> patch TaskForces/Squadrons in place and let MapMirror redraw.
        }

        // ── Simulation gate (guest) ───────────────────────────────────────────

        private static bool _suppressed;
        private Harmony? _harmony;

        public void SetSimulationSuppressed(bool suppressed)
        {
            _suppressed = suppressed;
            if (!suppressed || _harmony != null || _sim == null) return;
            // CampaignSim.Run(double seconds) is the only place the campaign advances:
            // CampaignDriver.Update calls it every frame. Skipping it on the guest stops
            // the AI, movement, economy and battle detection there in one place.
            var run = AccessTools.Method(_sim, "Run", new[] { typeof(double) });
            if (run == null)
            {
                Plugin.Log.LogError("[Campaign] fork: CampaignSim.Run(double) not found - guest campaign NOT suppressed.");
                return;
            }
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID + ".campaign.fork");
            _harmony.Patch(run, prefix: new HarmonyMethod(typeof(DynamicCampaignForkAdapter), nameof(RunGate)));
            Plugin.Log.LogInfo("[Campaign] fork: guest campaign simulation gated (CampaignSim.Run).");
        }

        private static bool RunGate(ref int __result)
        {
            if (!_suppressed) return true;
            __result = 0;
            return false;
        }

        public void ReturnToMap()
        {
            // TODO(phase 4): what the fork does after BattleExit to show the map
            // (CampaignSession.LoadCampaignScene / OnCampaignSceneLoaded).
        }

        // ── Player actions (phase 3) ──────────────────────────────────────────
        // Orders (each returns string error or null):
        //   SetCourse(Mover, GeoPoint target, bool append, bool enterPort = false, bool toField = false)
        //   Reaim(TaskForce, GeoPoint)            AddWaypoint(Mover, GeoPoint)
        //   MoveWaypoint(Mover, int, GeoPoint)    MoveWaypoint(Mover, RoutePoint, GeoPoint)
        //   Sortie(TaskForce)                     FormTaskForce(IReadOnlyList<(TaskForce,int)>, string)
        //   SplitAtSea(TaskForce, IReadOnlyList<int>, string)   MoveShips(IReadOnlyList<(TaskForce,int)>, TaskForce)
        //   Sort(TaskForce, TaskForce, IReadOnlyList<Hull>, IReadOnlyList<Hull>)
        //   MergeAll(TaskForce, TaskForce)        Rename(TaskForce, string)
        //   SetStation(TaskForce, Hull, Station)  ResetFormation(TaskForce)   SetLeader(TaskForce, Hull)
        //   ReturnToBase(Flight[, string])
        // Still to map: AirOperations, Missions, Admiralty, BaseStrikes, Shipyards,
        // Economy (whatever the UI calls).
        // Mover = abstract base of TaskForce/Flight { int Id, Name, SideId, Position, Route }.
        // GeoPoint = struct { double Lat, Lon }.

        public bool TryExecute(CampaignCommand command, out string reason) =>
            CampaignInterceptor.TryExecute(command, out reason);

        internal void RaiseStateChanged() => StateChanged?.Invoke();
    }
}
