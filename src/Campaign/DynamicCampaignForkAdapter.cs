using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Adapter for "Dynamic Campaign Mod (fork)" - Pacific 2027 - Steam Workshop
    /// 3820272150, DynamicCampaignMod.Fork.dll, namespace DynamicCampaignModFork.
    /// A standalone fork of Bungalow's mod (its 0.8.x line), by another author with
    /// Bungalow's permission. All names below are verified against fork 0.25.0.
    ///
    /// Everything is reflection: the fork is not referenced at compile time, so the
    /// multiplayer mod still loads (and this adapter stays inert) without it.
    ///
    /// HOW THE CAMPAIGN IS SHARED
    ///   - Host runs the campaign. On the guest, CampaignSim.Run (the one method that
    ///     advances the campaign) is skipped, so its AI/movement/economy never run.
    ///   - Full copy (join, after a battle, Ctrl+F6): the fork's own save format,
    ///     CampaignSave.Write on the host -> CampaignSave.Read + CampaignSession.Load
    ///     on the guest (shows the fork's loading screen).
    ///   - Every update interval: a text delta. Task forces and flights: position,
    ///     speed and route for those that moved; whole objects (the fork's own JSON)
    ///     for ones that appeared or changed make-up; ids of ones that are gone. Plus
    ///     the clock, radar contacts and new message-feed entries. MapMirror redraws
    ///     from the state every frame, so nothing else is needed for the map.
    ///   - Fuel, stocks, intel, economy etc. are refreshed by full copies only.
    ///
    /// PLAYER ACTIONS
    ///   Orders / Missions / BaseStrikes / AirOperations methods the UI calls are
    ///   patched. On the guest the call is encoded (ids, coordinates) and sent to
    ///   the host, which runs the same method through the fork's own code. Actions
    ///   that hand back a new object the UI needs at once (prepare a flight, form a
    ///   task force) ALSO run on the guest as a prediction; the id counters are
    ///   synced, so the host's real object arrives with the same id and replaces it.
    ///
    /// OWNERSHIP: keys "tf:{id}" (task force) and "fl:{id}" (flight). Everything is
    /// Shared until claimed (Ctrl+F7 on the selected one).
    /// </summary>
    public sealed class DynamicCampaignForkAdapter : ICampaignAdapter
    {
        private const string Ns = "DynamicCampaignModFork.";

        // ── Types ─────────────────────────────────────────────────────────────
        private Type? _tSession, _tBattle, _tSim, _tState, _tSave, _tHeader, _tDefinition,
                      _tMover, _tTaskForce, _tFlight, _tHull, _tGeo, _tRoutePoint,
                      _tBaseDef, _tPendingFlight, _tMirror,
                      _tCatalogue, _tBuildOrder, _tAdmOrder, _tAdmOp, _tRelief, _tStation, _tPendingMerge,
                      _tSquadron, _tGroundUnit, _tRefitCount, _tVlsTargetCell;
        private bool _probed, _found;
        private float _nextProbeAt;
        private string _version = "";

        public string AdapterId => "dcm-fork";
        public bool IsAvailable { get { Probe(); return _found; } }
        public string ModVersion { get { Probe(); return _version; } }
        public event Action? StateChanged;

        private static DynamicCampaignForkAdapter? _instance;

        private Type? T(string name) => CampaignInterceptor.FindType(Ns + name);

        private void Probe()
        {
            if (_probed) return;
            // Not loaded yet (mod loaders start it late): look again in a while rather
            // than deciding for the whole session.
            if (UnityEngine.Time.unscaledTime < _nextProbeAt) return;
            _nextProbeAt = UnityEngine.Time.unscaledTime + 2f;
            _tSession = T("Game.Session.CampaignSession");
            _tSim     = T("Core.Sim.CampaignSim");
            if (_tSession == null || _tSim == null) return;   // fork not (yet) loaded
            _probed = true;

            _tBattle       = T("Game.Battle.BattleSession");
            _tState        = T("Core.State.CampaignState");
            _tSave         = T("Core.State.CampaignSave");
            _tHeader       = _tSave?.GetNestedType("Header", BindingFlags.Public | BindingFlags.NonPublic);
            _tDefinition   = T("Core.Data.CampaignDefinition");
            _tMover        = T("Core.State.Mover");
            _tTaskForce    = T("Core.State.TaskForce");
            _tFlight       = T("Core.State.Flight");
            _tHull         = T("Core.State.Hull");
            _tGeo          = T("Core.Geo.GeoPoint");
            _tRoutePoint   = T("Core.State.RoutePoint");
            _tBaseDef      = T("Core.Data.BaseDefinition");
            _tPendingFlight= T("Core.State.PendingFlight");
            _tMirror       = T("Game.Mirror.MapMirror");
            _tCatalogue    = T("Core.Data.CatalogueEntry");
            _tBuildOrder   = T("Core.State.BuildOrder");
            _tAdmOrder     = T("Core.State.AdmiraltyOrder");
            _tAdmOp        = T("Core.State.AdmiraltyOperation");
            _tRelief       = T("Core.State.ReliefStation");
            _tStation      = T("Core.State.Station");
            _tPendingMerge = T("Core.State.PendingMerge");
            _tSquadron     = T("Core.State.Squadron");
            _tGroundUnit   = T("Core.State.GroundUnit");
            _tRefitCount   = T("Core.Sim.RefitCount");
            _tVlsTargetCell= T("Core.State.VlsTargetCell");

            var missing = new List<string>();
            foreach (var (n, t) in new (string, Type?)[] {
                ("CampaignState", _tState), ("CampaignSave", _tSave), ("CampaignSave.Header", _tHeader),
                ("CampaignDefinition", _tDefinition), ("TaskForce", _tTaskForce), ("Flight", _tFlight),
                ("GeoPoint", _tGeo), ("RoutePoint", _tRoutePoint), ("Mover", _tMover) })
                if (t == null) missing.Add(n);
            if (missing.Count > 0)
            {
                Plugin.Log.LogError("[Campaign] fork: found the mod but not " + string.Join(", ", missing) +
                                    " - a different version? Co-op campaign stays off.");
                return;
            }

            var asm = _tSession.Assembly;
            _version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString() ?? "unknown";
            _instance = this;
            _found = true;
            Plugin.Log.LogInfo($"[Campaign] fork: Dynamic Campaign Mod (fork) {_version} found - co-op campaign available.");

            HookStateChanges();
            BindActions();
        }

        // ── Session accessors ─────────────────────────────────────────────────

        private static object? GetStatic(Type? t, string prop)
        {
            if (t == null) return null;
            var p = AccessTools.Property(t, prop);
            return p?.GetValue(null);
        }

        private static object? F(object? o, string field) =>
            o == null ? null : AccessTools.Field(o.GetType(), field)?.GetValue(o);

        private static void SetF(object o, string field, object? value) =>
            AccessTools.Field(o.GetType(), field)?.SetValue(o, value);

        private bool SessionActive => GetStatic(_tSession, "Active") is true;
        private bool BattleActive  => GetStatic(_tBattle, "Active") is true;
        private object? Sim        => GetStatic(_tSession, "Sim");
        private object? State      => F(Sim, "State");
        private object? Definition => GetStatic(_tSession, "Definition");

        private object? _lastDefinition;

        public CampaignPhase Phase
        {
            get
            {
                Probe();
                if (!_found) return CampaignPhase.None;
                if (BattleActive) return CampaignPhase.Battle;
                if (!SessionActive || Sim == null) return CampaignPhase.None;
                if (!string.IsNullOrEmpty(F(State, "Victor") as string)) return CampaignPhase.Ended;
                return CampaignPhase.Map;
            }
        }

        public double Clock => F(State, "Now") is DateTime now ? (now - DateTime.MinValue).TotalSeconds : 0;
        public float TimeScale => GameTime.IsPaused() ? 0f : GameTime.TimeCompression;

        // ── No Career in the fork ─────────────────────────────────────────────

        public bool IsCareer => false;
        public string? LocalPlayerPost => null;
        public IReadOnlyList<(string key, string name)> ListPosts() => Array.Empty<(string, string)>();
        public void SetPostHeldByHuman(string postKey, bool held) { }

        public static string KeyOf(object mover, Type tTaskForce) =>
            (tTaskForce.IsInstanceOfType(mover) ? "tf:" : "fl:") + F(mover, "Id");

        // ── Selection (claim hotkey) ──────────────────────────────────────────

        public string? SelectedAssetName()
        {
            var mover = SelectedMover();
            return mover == null ? null : F(mover, "Name") as string;
        }

        private object? SelectedMover()
        {
            try
            {
                if (!Singleton<CampaignViewModel>.InstanceExists(false)) return null;
                var selected = Singleton<CampaignViewModel>.Instance.SelectedGroup;
                var mirror = GetStatic(_tSession, "Mirror");
                if (selected == null || mirror == null) return null;
                return AccessTools.Method(_tMirror, "MoverOf")?.Invoke(mirror, new object[] { selected });
            }
            catch { return null; }
        }

        public string? SelectedAssetKey()
        {
            try
            {
                if (!Singleton<CampaignViewModel>.InstanceExists(false)) return null;
                var selected = Singleton<CampaignViewModel>.Instance.SelectedGroup;
                var mirror = GetStatic(_tSession, "Mirror");
                if (selected == null || mirror == null) return null;
                var mover = AccessTools.Method(_tMirror, "MoverOf")?.Invoke(mirror, new object[] { selected });
                return mover == null ? null : KeyOf(mover, _tTaskForce!);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Campaign] fork: SelectedAssetKey failed: {ex.Message}");
                return null;
            }
        }

        // ── Battles ───────────────────────────────────────────────────────────

        public string? OwnerKeyOfBattleUnit(ObjectBase unit)
        {
            // BattleSession.PlannedOf(ObjectBase) is the fork's own lookup from a game
            // unit to its BattleUnit { TaskForce, Flight, Squadron, ... }.
            var m = AccessTools.Method(_tBattle, "PlannedOf");
            if (m == null) return null;
            object? bu;
            try { bu = m.Invoke(null, new object[] { unit }); }
            catch { return null; }
            if (bu == null) return null;
            if (F(bu, "TaskForce") is object tf) return "tf:" + F(tf, "Id");
            if (F(bu, "Flight") is object fl) return "fl:" + F(fl, "Id");
            return null;
        }

        public void ReturnToMap()
        {
            // Nothing to do: the host's return to the map triggers a full copy, and
            // applying it (CampaignSession.Load) brings the guest's map back.
        }

        // ══════════════════════════════════════════════════════════════════════
        //  STATE: capture (host) and apply (guest)
        // ══════════════════════════════════════════════════════════════════════

        private sealed class Sent
        {
            public string Shape = "";
            public string Motion = "";
        }

        private readonly Dictionary<string, Sent> _sent = new();   // key -> what the guest has

        /// <summary>Other id'd lists the guest sees changes in (whole elements, the
        /// fork's own JSON). Squadrons are big, so they are checked every 5th update.</summary>
        private static readonly (string field, bool every)[] TrackedLists =
        {
            ("PendingFlights", true), ("BuildOrders", true), ("AdmiraltyOrders", true),
            ("Operations", true), ("ReliefStations", true), ("GroundUnits", true), ("Squadrons", false),
        };
        private readonly Dictionary<string, Dictionary<string, int>> _listSent = new();

        /// <summary>Whole fields sent when they change (the fork's own JSON). Fast =
        /// every update: the top bar (points, renown, rank, bases, victory, income) and
        /// small things. Slow = every 5th: the bigger panels' data.</summary>
        private static readonly (string field, bool fast)[] TrackedFields =
        {
            ("Points", true), ("Renown", true), ("Ranks", true), ("BaseOwner", true), ("ForkVictory", true),
            ("IncomeHistory", true), ("IncomePaidThrough", true), ("War", true), ("Outcomes", true),
            ("Messages", true), ("Victor", true), ("VictoryAt", true), ("WeaponsYear", true),
            ("Garrisons", false), ("Stocks", false), ("StocksByClass", false), ("WingStores", false),
            ("AlliedPortStores", false), ("Intel", false), ("Reports", false), ("Log", false),
            ("Batteries", false), ("Salvos", false), ("Targets", false), ("Strikes", false),
            ("ForkWar", false), ("ForkGarrison", false), ("ForkAdvance", false), ("ForkAdvanceStatus", false),
            ("Disengaged", false), ("PortStockCut", false), ("LandUnitsLost", false), ("ForkLandDamage", false),
            ("RunwayDamagedUntil", false), ("AlertLines", false), ("Recap", false),
            // Player-edited (guests send theirs with field.set) and the refit queues.
            ("Drawings", true), ("SupplyPriorities", true), ("SupplySources", true),
            ("NativeAirWingRefitOrders", false), ("NativeVlsRefitOrders", false),
        };
        private readonly Dictionary<string, int> _fieldSent = new();
        private int _deltaCount;
        private readonly HashSet<object> _feedSent = new(ReferenceEqualityComparer.Instance);
        private string _radarSent = "";

        private object? _serializer;

        private object Serializer()
        {
            if (_serializer != null) return _serializer;
            var m = AccessTools.Method(_tSave, "Serializer")
                    ?? throw new MissingMethodException("CampaignSave.Serializer");
            return _serializer = m.Invoke(null, null)!;
        }

        private string ToJson(object o)
        {
            var ser = Serializer();
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            AccessTools.Method(ser.GetType(), "Serialize", new[] { typeof(TextWriter), typeof(object) })!
                .Invoke(ser, new object[] { sw, o });
            return sw.ToString();
        }

        private object? FromJson(string json, Type t)
        {
            var ser = Serializer();
            return AccessTools.Method(ser.GetType(), "Deserialize", new[] { typeof(TextReader), typeof(Type) })!
                .Invoke(ser, new object[] { new StringReader(json), t });
        }

        private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
        private static double Dbl(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        private IEnumerable<object> Movers(object state)
        {
            if (F(state, "TaskForces") is IList tfs) foreach (var x in tfs) if (x != null) yield return x;
            if (F(state, "Flights") is IList fls) foreach (var x in fls) if (x != null) yield return x;
        }

        private string ShapeOf(object mover)
        {
            // What makes a mover look different on the map apart from movement:
            // name, side, ships (task force) or airframes (flight), mission, port.
            var sb = new StringBuilder();
            sb.Append(F(mover, "Name")).Append('|').Append(F(mover, "SideId")).Append('|');
            sb.Append(F(mover, "Mission")?.GetType().Name).Append('|');
            if (F(mover, "Ships") is IList ships)
            {
                sb.Append("ships:");
                foreach (var h in ships) sb.Append(F(h, "Id")).Append(',');
                sb.Append('|').Append(F(mover, "PortId"));
            }
            if (F(mover, "Airframes") is IList afs) sb.Append("af:").Append(afs.Count).Append('|').Append(F(mover, "Returning"));
            return sb.ToString();
        }

        private string MotionOf(object mover)
        {
            var sb = new StringBuilder();
            Geo(F(mover, "Position"), sb);
            sb.Append('|').Append(Num(F(mover, "SpeedKnots") is double s ? s : 0)).Append('|');
            if (F(mover, "Route") is IList route)
                foreach (var rp in route)
                {
                    Geo(F(rp, "Point"), sb);
                    sb.Append(F(rp, "IsWaypoint") is true ? ",w;" : ",r;");
                }
            return sb.ToString();
        }

        private static void Geo(object? g, StringBuilder sb)
        {
            if (g == null) { sb.Append("0,0"); return; }
            sb.Append(Num((double)F(g, "Lat")!)).Append(',').Append(Num((double)F(g, "Lon")!));
        }

        private object NewGeo(double lat, double lon) =>
            AccessTools.Constructor(_tGeo, new[] { typeof(double), typeof(double) })!.Invoke(new object[] { lat, lon });

        private static readonly Regex IdRef = new("\"\\$id\":\"\\d+\",?", RegexOptions.Compiled);

        public string CaptureState(bool full)
        {
            var state = State;
            if (state == null || Phase != CampaignPhase.Map) return "";
            SnapshotDrawings(state);
            return full ? CaptureFull(state) : CaptureDelta(state);
        }

        private string CaptureFull(object state)
        {
            var def = Definition;
            var header = Activator.CreateInstance(_tHeader!)!;
            SetF(header, "Format", 2);
            SetF(header, "ModVersion", _version);
            SetF(header, "CampaignId", F(def, "id") ?? AccessTools.Property(_tDefinition, "Id")?.GetValue(def));
            SetF(header, "CampaignName", F(def, "name"));
            SetF(header, "Name", "Multiplayer sync");
            SetF(header, "SavedAt", DateTime.Now);
            SetF(header, "CampaignTime", F(state, "Now"));
            SetF(header, "PlayerSideId", F(state, "PlayerSideId"));
            var write = AccessTools.Method(_tSave, "Write", new[] { _tState!, _tHeader! })
                        ?? throw new MissingMethodException("CampaignSave.Write");
            string json = (string)write.Invoke(null, new[] { state, header })!;

            // Everything the guest now has.
            _sent.Clear();
            foreach (var m in Movers(state))
                _sent[KeyOf(m, _tTaskForce!)] = new Sent { Shape = ShapeOf(m), Motion = MotionOf(m) };
            _feedSent.Clear();
            if (F(state, "Feed") is IList feed) foreach (var x in feed) if (x != null) _feedSent.Add(x);
            _radarSent = RadarOf(state);
            _fieldSent.Clear();
            foreach (var (field, _) in TrackedFields)
            {
                var v = F(state, field);
                if (v != null) _fieldSent[field] = StableHash(IdRef.Replace(ToJson(v), ""));
            }
            _listSent.Clear();
            foreach (var (field, _) in TrackedLists)
            {
                var d = _listSent[field] = new Dictionary<string, int>();
                if (F(state, field) is IList l) foreach (var x in l) if (x != null) d[IdOf(x)] = ElemHash(x);
            }

            Plugin.Log.LogInfo($"[Campaign] fork: full copy captured ({json.Length / 1024} KB, {_sent.Count} movers).");
            return "F\n" + json;
        }

        private static string IdOf(object x) => Convert.ToString(F(x, "Id"), CultureInfo.InvariantCulture) ?? "";

        private int ElemHash(object x) => StableHash(IdRef.Replace(ToJson(x), ""));

        private IEnumerable<string> KeysIn(object state, string field)
        {
            if (F(state, field) is IList l) foreach (var x in l) if (x != null) yield return IdOf(x);
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
                return (int)h;
            }
        }

        private static string RadarOf(object state) =>
            F(state, "RadarContacts") is IList rc ? string.Join("\u001f", rc.Cast<object>()) : "";

        private string CaptureDelta(object state)
        {
            var sb = new StringBuilder("D\n");
            sb.Append("now ").Append(((DateTime)F(state, "Now")!).Ticks).Append('\n');
            int changes = 0;

            var present = new HashSet<string>();
            foreach (var m in Movers(state))
            {
                string key = KeyOf(m, _tTaskForce!);
                present.Add(key);
                string shape = ShapeOf(m), motion = MotionOf(m);
                if (!_sent.TryGetValue(key, out var s))
                {
                    _sent[key] = s = new Sent();
                    s.Shape = "\0new";
                }
                if (s.Shape != shape)
                {
                    // New, or its make-up changed: the whole object, in the fork's own JSON.
                    string json = ToJson(m).Replace('\n', ' ');
                    sb.Append("obj ").Append(key).Append(' ').Append(json).Append('\n');
                    s.Shape = shape; s.Motion = motion; changes++;
                }
                else if (s.Motion != motion)
                {
                    sb.Append("mv ").Append(key).Append(' ').Append(motion).Append('\n');
                    s.Motion = motion; changes++;
                }
            }
            foreach (var key in _sent.Keys.Where(k => !present.Contains(k)).ToList())
            {
                sb.Append("del ").Append(key).Append('\n');
                _sent.Remove(key); changes++;
            }

            // Id counters: anything the guest creates locally (a prediction) then gets
            // the same id the host's real one will have.
            sb.Append("ids ").Append(F(state, "_nextId")).Append(' ').Append(F(state, "_nextShipId")).Append('\n');

            bool slow = (++_deltaCount % 5) == 0;
            if (slow)
            {
                // Full key lists: clears predictions the host never made.
                sb.Append("lk TaskForces ").Append(string.Join(",", KeysIn(state, "TaskForces"))).Append('\n');
                sb.Append("lk Flights ").Append(string.Join(",", KeysIn(state, "Flights"))).Append('\n');
            }
            foreach (var (field, every) in TrackedLists)
            {
                if (!every && !slow) continue;
                if (F(state, field) is not IList list) continue;
                if (!_listSent.TryGetValue(field, out var sentList)) _listSent[field] = sentList = new Dictionary<string, int>();
                var seen = new HashSet<string>();
                foreach (var x in list)
                {
                    if (x == null) continue;
                    string id = IdOf(x);
                    seen.Add(id);
                    string json = ToJson(x);
                    int h = StableHash(IdRef.Replace(json, ""));
                    if (sentList.TryGetValue(id, out var old) && old == h) continue;
                    sentList[id] = h;
                    sb.Append("lobj ").Append(field).Append(' ').Append(id).Append(' ').Append(json.Replace('\n', ' ')).Append('\n');
                    changes++;
                }
                var gone = sentList.Keys.Where(k => !seen.Contains(k)).ToList();
                if (gone.Count > 0)
                {
                    foreach (var g in gone) sentList.Remove(g);
                    sb.Append("ldel ").Append(field).Append(' ').Append(string.Join(",", gone)).Append('\n');
                    changes++;
                }
            }

            foreach (var (field, fast) in TrackedFields)
            {
                if (!fast && !slow) continue;
                if (AccessTools.Field(state.GetType(), field) == null) continue;   // not in this fork version
                var v = F(state, field);
                string json = v == null ? "null" : ToJson(v);
                int h = StableHash(IdRef.Replace(json, ""));
                if (_fieldSent.TryGetValue(field, out var old) && old == h) continue;
                _fieldSent[field] = h;
                sb.Append("fld ").Append(field).Append(' ').Append(json.Replace('\n', ' ')).Append('\n');
                changes++;
            }

            string radar = RadarOf(state);
            if (radar != _radarSent)
            {
                sb.Append("radar ").Append(radar.Replace("\n", " ")).Append('\n');
                _radarSent = radar; changes++;
            }

            if (F(state, "Feed") is IList feed)
                foreach (var msg in feed)
                    if (msg != null && _feedSent.Add(msg))
                    {
                        sb.Append("feed ").Append(ToJson(msg).Replace('\n', ' ')).Append('\n');
                        changes++;
                    }

            // Clock only: still worth sending while it runs (dedupe drops it when paused).
            return sb.ToString();
        }

        // ── Apply (guest) ─────────────────────────────────────────────────────

        private object? _pendingState;          // full copy handed to CampaignSession.Load, not yet live
        private readonly Queue<string> _queued = new();

        public void ApplyState(string payload, CampaignPhase phase, double hostClock, float hostTimeScale)
        {
            if (payload.StartsWith("F\n")) { ApplyFull(payload.Substring(2)); return; }
            if (!payload.StartsWith("D\n")) return;

            if (Loading)
            {
                // Arrived while the full copy is still loading: keep the order.
                if (_queued.Count < 2000) _queued.Enqueue(payload);
                else { _queued.Clear(); CampaignSync.RequestFull("too many updates while loading"); }
                return;
            }
            ApplyDelta(payload);
        }

        public bool IsLoading => _pendingState != null || _heldFull != null;

        /// <summary>True while a full copy is loading on the guest.</summary>
        private bool Loading
        {
            get
            {
                if (_pendingState == null) return false;
                if (SessionActive && ReferenceEquals(State, _pendingState))
                {
                    _pendingState = null;
                    while (_queued.Count > 0) ApplyDelta(_queued.Dequeue());
                    return false;
                }
                return true;
            }
        }

        private float _fullWaitSince = -1f;
        private string? _heldFull;

        private void ApplyFull(string json)
        {
            // Still in the multiplayer battle scene: wait for it to end (max 20 s),
            // otherwise the campaign scene and the battle would fight over the screen.
            if (SessionManager.MissionIsLive || SessionManager.SceneLoading)
            {
                if (_fullWaitSince < 0) _fullWaitSince = UnityEngine.Time.unscaledTime;
                if (UnityEngine.Time.unscaledTime - _fullWaitSince < 20f)
                {
                    _heldFull = json;
                    return;
                }
            }
            _fullWaitSince = -1f;
            _heldFull = null;

            var def = Definition ?? _lastDefinition ?? LoadDefinitionFor(json);
            if (def == null)
            {
                SimSyncManager.ReportIssue("Open Pacific 2027 to join the host's campaign.",
                    "Load any Pacific 2027 save (or start a new one), then press Ctrl+F6.");
                return;
            }
            _lastDefinition = def;

            var read = AccessTools.Method(_tSave, "Read")
                       ?? throw new MissingMethodException("CampaignSave.Read");
            var args = new object?[] { json, def, null, null };
            var state = read.Invoke(null, args);
            if (state == null)
            {
                SimSyncManager.ReportIssue("Couldn't load the host's campaign.", args[3] as string ?? "Unknown reason - see the log.");
                return;
            }
            _queued.Clear();
            _pendingState = state;
            var load = AccessTools.Method(_tSession, "Load", new[] { _tDefinition!, _tState! })
                       ?? throw new MissingMethodException("CampaignSession.Load");
            Plugin.Log.LogInfo($"[Campaign] fork: loading the host's campaign ({json.Length / 1024} KB).");
            load.Invoke(null, new[] { def, state });
        }

        /// <summary>Guest not in the campaign yet: read the definition straight from
        /// the fork's folder (fork_campaigns/{name}/campaign.json).</summary>
        private object? LoadDefinitionFor(string json)
        {
            try
            {
                var m = Regex.Match(json, "\"CampaignId\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) return null;
                string id = m.Groups[1].Value;
                string folder = Path.GetDirectoryName(_tSession!.Assembly.Location)!;
                string name = id.Substring(id.LastIndexOf('.') + 1);
                string path = Path.Combine(folder, "fork_campaigns", name, "campaign.json");
                if (!File.Exists(path)) return null;
                return AccessTools.Method(_tDefinition, "Load", new[] { typeof(string) })?.Invoke(null, new object[] { path });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Campaign] fork: couldn't read the campaign definition: {ex.Message}");
                return null;
            }
        }

        private void ApplyDelta(string payload)
        {
            if (_heldFull != null) { ApplyFull(_heldFull); return; }
            var state = State;
            if (state == null) return;   // not in the campaign (yet): the full copy will come

            var byKey = new Dictionary<string, (object mover, IList list)>();
            var tfs = (IList)F(state, "TaskForces")!;
            var fls = (IList)F(state, "Flights")!;
            foreach (var m in tfs) if (m != null) byKey[KeyOf(m, _tTaskForce!)] = (m, tfs);
            foreach (var m in fls) if (m != null) byKey[KeyOf(m, _tTaskForce!)] = (m, fls);

            bool lost = false;
            foreach (var line in payload.Split('\n'))
            {
                if (line.Length < 4) continue;
                int sp = line.IndexOf(' ');
                string kind = line.Substring(0, sp), rest = line.Substring(sp + 1);
                try
                {
                    switch (kind)
                    {
                        case "now":
                            SetF(state, "Now", new DateTime(long.Parse(rest, CultureInfo.InvariantCulture)));
                            break;
                        case "mv":
                        {
                            int k = rest.IndexOf(' ');
                            string key = rest.Substring(0, k);
                            if (byKey.TryGetValue(key, out var e)) ApplyMotion(e.mover, rest.Substring(k + 1));
                            else lost = true;
                            break;
                        }
                        case "obj":
                        {
                            int k = rest.IndexOf(' ');
                            string key = rest.Substring(0, k);
                            bool isTf = key.StartsWith("tf:");
                            var obj = FromJson(rest.Substring(k + 1), isTf ? _tTaskForce! : _tFlight!);
                            if (obj == null) { lost = true; break; }
                            var list = isTf ? tfs : fls;
                            if (byKey.TryGetValue(key, out var e))
                            {
                                int i = e.list.IndexOf(e.mover);
                                if (i >= 0) e.list[i] = obj; else list.Add(obj);
                            }
                            else list.Add(obj);
                            byKey[key] = (obj, list);
                            break;
                        }
                        case "del":
                            if (byKey.TryGetValue(rest, out var d)) { d.list.Remove(d.mover); byKey.Remove(rest); }
                            break;
                        case "ids":
                        {
                            var p2 = rest.Split(' ');
                            SetF(state, "_nextId", int.Parse(p2[0], CultureInfo.InvariantCulture));
                            SetF(state, "_nextShipId", int.Parse(p2[1], CultureInfo.InvariantCulture));
                            break;
                        }
                        case "lk":
                        {
                            int k = rest.IndexOf(' ');
                            string field = k < 0 ? rest : rest.Substring(0, k);
                            var keep = new HashSet<string>((k < 0 ? "" : rest.Substring(k + 1)).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                            if (F(state, field) is IList l)
                                for (int i = l.Count - 1; i >= 0; i--)
                                    if (l[i] != null && !keep.Contains(IdOf(l[i]!)))
                                    {
                                        byKey.Remove(KeyOf(l[i]!, _tTaskForce!));
                                        l.RemoveAt(i);
                                    }
                            break;
                        }
                        case "lobj":
                        {
                            int a = rest.IndexOf(' '), b = rest.IndexOf(' ', a + 1);
                            string field = rest.Substring(0, a), id = rest.Substring(a + 1, b - a - 1);
                            if (F(state, field) is not IList l) break;
                            var elem = l.GetType().GetGenericArguments()[0];
                            var obj = FromJson(rest.Substring(b + 1), elem);
                            if (obj == null) break;
                            int at = -1;
                            for (int i = 0; i < l.Count; i++) if (l[i] != null && IdOf(l[i]!) == id) { at = i; break; }
                            if (at >= 0) l[at] = obj; else l.Add(obj);
                            break;
                        }
                        case "ldel":
                        {
                            int a = rest.IndexOf(' ');
                            string field = rest.Substring(0, a);
                            var del = new HashSet<string>(rest.Substring(a + 1).Split(','));
                            if (F(state, field) is IList l)
                                for (int i = l.Count - 1; i >= 0; i--)
                                    if (l[i] != null && del.Contains(IdOf(l[i]!))) l.RemoveAt(i);
                            break;
                        }
                        case "fld":
                        {
                            int a = rest.IndexOf(' ');
                            string fname = rest.Substring(0, a);
                            ApplyField(state, fname, rest.Substring(a + 1));
                            AfterFieldApplied(state, fname);
                            break;
                        }
                        case "radar":
                            if (F(state, "RadarContacts") is IList rc)
                            {
                                rc.Clear();
                                if (rest.Length > 0) foreach (var c in rest.Split('\u001f')) rc.Add(c);
                            }
                            break;
                        case "feed":
                        {
                            var feed = F(state, "Feed") as IList;
                            var elem = feed?.GetType().GetGenericArguments().FirstOrDefault();
                            if (feed == null || elem == null) break;
                            var msg = FromJson(rest, elem);
                            if (msg != null) feed.Add(msg);
                            while (feed.Count > 600) feed.RemoveAt(0);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Campaign] fork: couldn't apply '{kind}': {ex.Message}");
                    lost = true;
                }
            }
            if (lost) CampaignSync.RequestFull("update referred to something this map doesn't have");
        }

        /// <summary>Replace a state field with the host's value. Collections are refilled
        /// in place (other objects may hold the same instance; many are readonly).</summary>
        private void ApplyField(object state, string name, string json)
        {
            var fi = AccessTools.Field(state.GetType(), name);
            if (fi == null) return;
            object? incoming = json == "null" ? null : FromJson(json, fi.FieldType);
            var current = fi.GetValue(state);
            if (current is IDictionary cd && incoming is IDictionary id)
            {
                cd.Clear();
                foreach (DictionaryEntry e in id) cd[e.Key] = e.Value;
                return;
            }
            if (current is IList cl && incoming is IList il)
            {
                cl.Clear();
                foreach (var x in il) cl.Add(x);
                return;
            }
            if (current != null && incoming != null && current.GetType().IsGenericType
                && AccessTools.Method(current.GetType(), "Clear") is MethodInfo clear
                && AccessTools.Method(current.GetType(), "Add") is MethodInfo add
                && incoming is IEnumerable items)
            {
                // HashSet<T> and other non-list collections.
                clear.Invoke(current, null);
                foreach (var x in items) add.Invoke(current, new[] { x });
                return;
            }
            fi.SetValue(state, incoming);
        }

        private void ApplyMotion(object mover, string motion)
        {
            var parts = motion.Split('|');
            var pos = parts[0].Split(',');
            SetF(mover, "Position", NewGeo(Dbl(pos[0]), Dbl(pos[1])));
            SetF(mover, "SpeedKnots", Dbl(parts[1]));
            if (F(mover, "Route") is IList route)
            {
                route.Clear();
                if (parts.Length > 2)
                    foreach (var p in parts[2].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var c = p.Split(',');
                        var rp = Activator.CreateInstance(_tRoutePoint!)!;
                        SetF(rp, "Point", NewGeo(Dbl(c[0]), Dbl(c[1])));
                        SetF(rp, "IsWaypoint", c.Length > 2 && c[2] == "w");
                        route.Add(rp);
                    }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  SIMULATION GATE (guest) + state-changed signal (host)
        // ══════════════════════════════════════════════════════════════════════

        private static bool _suppressed;
        private Harmony? _harmony;
        private Harmony H => _harmony ??= new Harmony(PluginInfo.PLUGIN_GUID + ".campaign.fork");

        public void SetSimulationSuppressed(bool suppressed)
        {
            _suppressed = suppressed;
            Plugin.Log.LogInfo($"[Campaign] fork: guest campaign simulation {(suppressed ? "OFF (host runs it)" : "back on")}.");
        }

        private void HookStateChanges()
        {
            // CampaignSim.Run(double seconds) is the only place the campaign advances
            // (CampaignDriver.Update calls it every frame). One patch does both jobs:
            // skip it on a suppressed guest; signal "state changed" on the host.
            var run = AccessTools.Method(_tSim, "Run", new[] { typeof(double) });
            if (run == null)
            {
                Plugin.Log.LogError("[Campaign] fork: CampaignSim.Run(double) not found - co-op campaign can't gate the guest.");
                _found = false;
                return;
            }
            H.Patch(run,
                prefix:  new HarmonyMethod(typeof(DynamicCampaignForkAdapter), nameof(RunPrefix)),
                postfix: new HarmonyMethod(typeof(DynamicCampaignForkAdapter), nameof(RunPostfix)));

            // The campaign has decided on a battle: Saves.BeforeBattle(where, now) runs
            // before the host starts loading it. Tell the guest straight away.
            var before = AccessTools.Method(T("Game.Session.Saves"), "BeforeBattle");
            if (before != null)
                H.Patch(before, prefix: new HarmonyMethod(typeof(DynamicCampaignForkAdapter), nameof(BeforeBattlePrefix)));

            // Owner colours on the map: after the fork sets each symbol's colour.
            var sync = AccessTools.Method(_tMirror, "Sync");
            if (sync != null)
                H.Patch(sync, postfix: new HarmonyMethod(typeof(DynamicCampaignForkAdapter), nameof(MirrorSyncPostfix)));
            else
                Plugin.Log.LogWarning("[Campaign] fork: MapMirror.Sync not found - claimed units won't be coloured.");
        }

        private static bool RunPrefix(ref int __result)
        {
            if (!_suppressed) { CampaignSync.SimStepping = true; return true; }
            __result = 0;
            return false;
        }

        // ── Owner colours ─────────────────────────────────────────────────────

        private readonly Dictionary<object, Noesis.Brush> _origBrush = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CampaignSeat, Noesis.SolidColorBrush> _seatBrush = new();
        private bool IsSeatBrush(Noesis.Brush? b) { foreach (var v in _seatBrush.Values) if (ReferenceEquals(v, b)) return true; return false; }
        private float _colorErrorAt = -999f;

        private static void MirrorSyncPostfix(object __instance)
        {
            try { _instance?.ApplyOwnerColors(__instance); }
            catch (Exception ex)
            {
                if (_instance != null && UnityEngine.Time.unscaledTime - _instance._colorErrorAt > 30f)
                {
                    _instance._colorErrorAt = UnityEngine.Time.unscaledTime;
                    Plugin.Log.LogWarning($"[Campaign] fork: owner colours failed: {ex.Message}");
                }
            }
        }

        private static Noesis.SolidColorBrush BrushOf(UnityEngine.Color c) =>
            new(Noesis.Color.FromArgb(255, (byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255)));

        /// <summary>A claimed task force/flight's map symbol takes its owner's colour on
        /// both screens; released, it gets the fork's own colour back.</summary>
        private void ApplyOwnerColors(object mirror)
        {
            if (F(mirror, "_groups") is not IDictionary groups) return;
            bool on = CampaignSync.IsActive;
            if (_seatBrush.Count == 0)
                foreach (var seat in new[] { CampaignSeat.Host, CampaignSeat.Guest, CampaignSeat.Guest2, CampaignSeat.Guest3 })
                    _seatBrush[seat] = BrushOf(CampaignOwnership.SeatColor(seat));

            foreach (DictionaryEntry e in groups)
            {
                if (e.Value is not GroupOfUnits g || e.Key == null) continue;
                var owner = on ? CampaignOwnership.OwnerOf(KeyOf(e.Key, _tTaskForce!)) : CampaignSeat.Shared;
                _seatBrush.TryGetValue(owner, out var want);
                var cur = g.UnitColorBrush;
                if (want != null)
                {
                    if (ReferenceEquals(cur, want)) continue;
                    // Remember the fork's colour (the latest one, if it changed it).
                    if (!IsSeatBrush(cur)) _origBrush[g] = cur;
                    g.UnitColorBrush = want;
                }
                else if (_origBrush.TryGetValue(g, out var orig))
                {
                    if (IsSeatBrush(cur)) g.UnitColorBrush = orig;
                    _origBrush.Remove(g);
                }
            }
        }

        private static void BeforeBattlePrefix(string where)
        {
            try { CampaignSync.NotifyBattle(BattleStage.Starting, where ?? ""); }
            catch { /* never break the fork's battle start */ }
        }

        private static void RunPostfix()
        {
            CampaignSync.SimStepping = false;
            _instance?.StateChanged?.Invoke();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PLAYER ACTIONS
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Sim field holding each actions class.</summary>
        private static readonly (string type, string simField)[] ActionClasses =
        {
            ("Core.Sim.Orders", "Orders"),
            ("Core.Sim.Missions", "Missions"),
            ("Core.Sim.BaseStrikes", "Strikes"),
            ("Core.Sim.AirOperations", "Air"),
            ("Core.Sim.Admiralty", "Admiralty"),
            ("Core.Sim.Shipyards", "Shipyards"),
            ("Core.Sim.CarrierRefits", "Refits"),
            ("Core.Sim.VlsRefits", "VlsRefits"),
            ("Core.Sim.CarrierWings", "Wings"),
            ("Core.Sim.Reliefs", "Reliefs"),
            ("Core.Sim.Invasions", "Invasions"),
            ("Core.Sim.Supply", "Supply"),
        };

        /// <summary>Player actions, by class. Everything else in these classes is
        /// either a query or something the simulation calls itself.</summary>
        private static readonly Dictionary<string, string[]> Actions = new()
        {
            ["Orders"]    = new[] { "SetCourse", "Reaim", "MoveWaypoint", "AddWaypoint", "Sortie", "FormTaskForce",
                                    "SplitAtSea", "Sort", "MoveShips", "Rename", "SetStation", "ResetFormation",
                                    "SetLeader", "MergeRefused", "EndMerge", "MergeAll", "ReturnToBase" },
            ["Missions"]  = new[] { "Patrol", "Hunt", "Relieve", "Cap", "Search", "Redraw", "StrikeBase", "Refuel",
                                    "TankerStation", "Cover", "Escort", "Follow", "Rendezvous", "Replenish",
                                    "Invade", "Join", "Cancel", "Course", "End" },
            ["Strikes"]   = new[] { "Strike", "Release" },
            ["Air"]       = new[] { "Prepare", "Launch", "Cancel", "Detach" },
            ["Admiralty"] = new[] { "Accept", "Decline" },
            ["Shipyards"] = new[] { "Order", "Cancel", "Deliver" },
            ["Refits"]    = new[] { "SaveDraft", "Cancel" },
            ["VlsRefits"] = new[] { "SaveDraft", "Cancel" },
            ["Wings"]     = new[] { "Replenish" },
            ["Reliefs"]   = new[] { "Begin", "Stop" },
            ["Invasions"] = new[] { "Embark", "Disembark", "Land" },
            ["Supply"]    = new[] { "Hold", "Resume", "Reroute", "SetSource" },   // the Convoys panel
        };

        private void BindActions()
        {
            int synced = 0, hostOnly = 0;
            foreach (var (typeName, simField) in ActionClasses)
            {
                var t = T(typeName);
                if (t == null || !Actions.TryGetValue(simField, out var names)) continue;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!names.Contains(m.Name)) continue;
                    // Methods the sim itself calls with the same name but other shapes
                    // (e.g. AirOperations.ReturnToBase with optional flags) are left alone
                    // unless they are on the list above for that class.
                    bool predict = !Syncable(m) && Predictable(m);
                    if (Syncable(m) || predict)
                    {
                        string id = $"{simField}.{m.Name}/{string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))}";
                        var method = m;
                        string field = simField;
                        CampaignInterceptor.Bind(new CampaignInterceptor.Binding
                        {
                            CommandId = id,
                            Target = m,
                            Capture = (inst, args) => Encode(id, method, args),
                            Execute = (CampaignCommand c, out string why) => Execute(field, method, c, out why),
                            Predict = predict,
                        });
                        synced++;
                    }
                    else
                    {
                        BindHostOnly(m);
                        hostOnly++;
                    }
                }
            }
            Plugin.Log.LogInfo($"[Campaign] fork: {synced} player actions shared with the host, {hostOnly} host-only.");
        }

        /// <summary>Can be sent to the host: every argument encodes, and the return
        /// value is nothing the guest's UI needs straight away (void, string, bool).</summary>
        private bool Syncable(MethodInfo m)
        {
            var r = m.ReturnType;
            if (r != typeof(void) && r != typeof(string) && r != typeof(bool)) return false;
            foreach (var p in m.GetParameters())
                if (p.IsOut || p.ParameterType.IsByRef || !CanEncode(p.ParameterType)) return false;
            return true;
        }

        /// <summary>Returns something the guest's UI needs at once (or has an out
        /// parameter): the guest runs it locally as a prediction AND the host runs
        /// the real one. Every in-argument still has to encode.</summary>
        private bool Predictable(MethodInfo m)
        {
            foreach (var p in m.GetParameters())
            {
                if (p.IsOut) continue;
                if (p.ParameterType.IsByRef || !CanEncode(p.ParameterType)) return false;
            }
            return true;
        }

        // ── Host-only actions on the guest ────────────────────────────────────

        private static readonly Dictionary<MethodBase, string> _hostOnly = new();
        private const string HostOnlyText = "Only the host can do this in a co-op campaign (for now).";

        private void BindHostOnly(MethodInfo m)
        {
            try
            {
                _hostOnly[m] = m.DeclaringType!.Name + "." + m.Name;
                var prefix = m.ReturnType == typeof(string)
                    ? nameof(HostOnlyStringPrefix)
                    : nameof(HostOnlyPrefix);
                H.Patch(m, prefix: new HarmonyMethod(typeof(DynamicCampaignForkAdapter), prefix));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Campaign] fork: couldn't guard {m.DeclaringType?.Name}.{m.Name}: {ex.Message}");
            }
        }

        private static bool GuestBlocked(MethodBase original)
        {
            if (!CampaignSync.IsActive || Plugin.Instance.CfgIsHost.Value || CampaignSync.ApplyingFromNetwork) return false;
            Plugin.Log.LogInfo($"[Campaign] fork: guest tried host-only action {(_hostOnly.TryGetValue(original, out var n) ? n : original.Name)}.");
            SimSyncManager.ReportIssue("Host only.", HostOnlyText, warning: true);
            return true;
        }

        private static bool HostOnlyStringPrefix(MethodBase __originalMethod, ref string __result)
        {
            if (!GuestBlocked(__originalMethod)) return true;
            __result = HostOnlyText;
            return false;
        }

        private static bool HostOnlyPrefix(MethodBase __originalMethod) => !GuestBlocked(__originalMethod);

        // ── Encoding ──────────────────────────────────────────────────────────

        private bool IsList(Type t, out Type elem)
        {
            elem = typeof(object);
            if (!t.IsGenericType) return false;
            var g = t.GetGenericTypeDefinition();
            if (g != typeof(List<>) && g != typeof(IReadOnlyList<>) && g != typeof(IList<>)
                && g != typeof(IEnumerable<>) && g != typeof(IReadOnlyCollection<>)) return false;
            elem = t.GetGenericArguments()[0];
            return true;
        }

        private bool CanEncode(Type t)
        {
            if (t == typeof(string) || t == typeof(int) || t == typeof(double) || t == typeof(float)
                || t == typeof(bool) || t.IsEnum) return true;
            if (t == _tGeo) return true;
            var nn = Nullable.GetUnderlyingType(t);
            if (nn != null) return CanEncode(nn);
            if (_tMover!.IsAssignableFrom(t) || t == _tHull || t == _tBaseDef || t == _tPendingFlight || t == _tRoutePoint
                || t == _tCatalogue || t == _tBuildOrder || t == _tAdmOrder || t == _tAdmOp || t == _tRelief
                || t == _tStation || t == _tPendingMerge || t == _tSquadron || t == _tGroundUnit
                || t == _tRefitCount || t == _tVlsTargetCell) return true;
            if (IsList(t, out var e))
            {
                if (e.IsGenericType && e.GetGenericTypeDefinition() == typeof(ValueTuple<,>))
                {
                    var a = e.GetGenericArguments();
                    return CanEncode(a[0]) && CanEncode(a[1]) && !IsList(a[0], out _) && !IsList(a[1], out _);
                }
                return CanEncode(e) && !IsList(e, out _);
            }
            return false;
        }

        private CampaignCommand? Encode(string id, MethodInfo m, object?[] args)
        {
            var cmd = new CampaignCommand { Id = id };
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                string enc = Enc(args[i]);
                cmd.Arg("a" + i, enc);
                if (cmd.AssetKey.Length == 0 && (enc.StartsWith("tf:") || enc.StartsWith("fl:")))
                    cmd.AssetKey = enc;
            }
            return cmd;
        }

        private string Enc(object? v)
        {
            switch (v)
            {
                case null: return "n:";
                case string s: return "s:" + s;
                case bool b: return b ? "v:1" : "v:0";
                case int i: return "v:" + i.ToString(CultureInfo.InvariantCulture);
                case double d: return "v:" + Num(d);
                case float f: return "v:" + f.ToString("R", CultureInfo.InvariantCulture);
                case Enum e: return "v:" + Convert.ToInt32(e).ToString(CultureInfo.InvariantCulture);
            }
            var t = v.GetType();
            if (t == _tGeo) { var sb = new StringBuilder("g:"); Geo(v, sb); return sb.ToString(); }
            if (_tMover!.IsInstanceOfType(v)) return KeyOf(v, _tTaskForce!);
            if (t == _tHull) return "h:" + F(v, "Id");
            if (t == _tBaseDef) return "b:" + F(v, "id");
            if (t == _tPendingFlight) return "pf:" + F(v, "Id");
            if (t == _tRoutePoint) return EncRoutePoint(v);
            if (t == _tSquadron) return "sq:" + F(v, "Id");
            if (t == _tGroundUnit) return "gu:" + F(v, "Id");
            if (t == _tRefitCount || t == _tVlsTargetCell)    // plain data: the fork's own JSON
                return "j:" + ToJson(v).Replace('\n', ' ').Replace("\u001d", " ").Replace("\u001e", " ");
            if (t == _tBuildOrder) return "bo:" + F(v, "Id");
            if (t == _tAdmOrder) return "ao:" + F(v, "Id");
            if (t == _tAdmOp) return "aop:" + F(v, "Id");
            if (t == _tRelief) return "rs:" + F(v, "Id");
            if (t == _tStation) return "st:" + Num((double)F(v, "Right")!) + "," + Num((double)F(v, "Ahead")!);
            if (t == _tPendingMerge) return "pm:" + F(v, "FromId") + "," + F(v, "IntoId");
            if (t == _tCatalogue) return "ce:" + ToJson(v).Replace('\n', ' ').Replace("\u001d", " ").Replace("\u001e", " ");
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTuple<,>))
                return "t:" + Esc(Enc(t.GetField("Item1")!.GetValue(v))) + "\u001e" + Esc(Enc(t.GetField("Item2")!.GetValue(v)));
            if (v is IEnumerable list)
                return "l:" + string.Join("\u001d", list.Cast<object?>().Select(x => Esc(Enc(x))));
            throw new NotSupportedException("can't send a " + t.Name);
        }

        private static string Esc(string s) => s.Replace("\u001d", " ").Replace("\u001e", " ");

        private string EncRoutePoint(object rp)
        {
            var state = State;
            if (state != null)
                foreach (var m in Movers(state))
                    if (F(m, "Route") is IList route)
                    {
                        int i = route.IndexOf(rp);
                        if (i >= 0) return "rp:" + KeyOf(m, _tTaskForce!) + "#" + i;
                    }
            throw new InvalidOperationException("route point not found");
        }

        private object? Dec(string s, Type t)
        {
            int c = s.IndexOf(':');
            string kind = s.Substring(0, c), body = s.Substring(c + 1);
            var nn = Nullable.GetUnderlyingType(t);
            switch (kind)
            {
                case "n": return null;
                case "s": return body;
                case "v":
                {
                    var target = nn ?? t;
                    if (target.IsEnum) return Enum.ToObject(target, int.Parse(body, CultureInfo.InvariantCulture));
                    if (target == typeof(bool)) return body == "1";
                    return Convert.ChangeType(body, target, CultureInfo.InvariantCulture);
                }
                case "g": { var p = body.Split(','); return NewGeo(Dbl(p[0]), Dbl(p[1])); }
                case "tf":
                case "fl":
                    return FindMover(s) ?? throw new KeyNotFoundException((kind == "tf" ? "task force " : "flight ") + body + " no longer exists");
                case "h": return FindHull(int.Parse(body, CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("ship no longer exists");
                case "b": return FindBase(body) ?? throw new KeyNotFoundException("base " + body);
                case "pf": return FindIn("PendingFlights", int.Parse(body, CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("prepared flight no longer exists");
                case "sq": return FindIn("Squadrons", int.Parse(body, CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("squadron no longer exists");
                case "gu": return FindIn("GroundUnits", int.Parse(body, CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("ground unit no longer exists");
                case "j": return FromJson(body, nn ?? t);
                case "bo": return FindIn("BuildOrders", int.Parse(body, CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("build order no longer exists");
                case "ao": return FindIn("AdmiraltyOrders", int.Parse(body, CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("Admiralty order no longer exists");
                case "aop": return FindIn("Operations", int.Parse(body, CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("operation no longer exists");
                case "rs": return FindIn("ReliefStations", int.Parse(body, CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("relief station no longer exists");
                case "st":
                {
                    var p = body.Split(',');
                    var st = Activator.CreateInstance(_tStation!)!;   // boxed struct: set fields, then unbox on use
                    SetF(st, "Right", Dbl(p[0]));
                    SetF(st, "Ahead", Dbl(p[1]));
                    return st;
                }
                case "pm":
                {
                    var p = body.Split(',');
                    var pm = Activator.CreateInstance(_tPendingMerge!)!;
                    SetF(pm, "FromId", int.Parse(p[0], CultureInfo.InvariantCulture));
                    SetF(pm, "IntoId", int.Parse(p[1], CultureInfo.InvariantCulture));
                    return pm;
                }
                case "ce": return FindCatalogue(body);
                case "rp":
                {
                    int h = body.LastIndexOf('#');
                    var mover = FindMover(body.Substring(0, h)) ?? throw new KeyNotFoundException("route owner gone");
                    var route = (IList)F(mover, "Route")!;
                    int i = int.Parse(body.Substring(h + 1), CultureInfo.InvariantCulture);
                    return i < route.Count ? route[i] : throw new KeyNotFoundException("waypoint gone");
                }
                case "t":
                {
                    var parts = body.Split('\u001e');
                    var a = t.GetGenericArguments();
                    return Activator.CreateInstance(t, Dec(parts[0], a[0]), Dec(parts[1], a[1]));
                }
                case "l":
                {
                    IsList(t, out var e);
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(e))!;
                    if (body.Length > 0) foreach (var x in body.Split('\u001d')) list.Add(Dec(x, e));
                    return list;
                }
            }
            throw new NotSupportedException("unknown argument kind " + kind);
        }

        private object? FindMover(string key)
        {
            var state = State;
            if (state == null) return null;
            foreach (var m in Movers(state)) if (KeyOf(m, _tTaskForce!) == key) return m;
            return null;
        }

        private object? FindIn(string listField, int id)
        {
            if (F(State, listField) is IList list)
                foreach (var x in list) if (x != null && F(x, "Id") is int i && i == id) return x;
            return null;
        }

        private object? FindHull(int id)
        {
            if (F(State, "TaskForces") is IList tfs)
                foreach (var tf in tfs)
                    if (F(tf, "Ships") is IList ships)
                        foreach (var h in ships) if (h != null && F(h, "Id") is int i && i == id) return h;
            return null;
        }

        /// <summary>The host's own catalogue entry with the same unit/side/nation (the
        /// shipyard compares entries, so a copy would not do); a copy if not listed.</summary>
        private object? FindCatalogue(string json)
        {
            var copy = FromJson(json, _tCatalogue!);
            if (copy == null) return null;
            if (F(Definition, "catalogue") is IList cat)
                foreach (var e in cat)
                    if (e != null && Equals(F(e, "unit"), F(copy, "unit")) && Equals(F(e, "side"), F(copy, "side"))
                        && Equals(F(e, "nation"), F(copy, "nation")) && Equals(F(e, "aircraft"), F(copy, "aircraft"))
                        && Equals(F(e, "ground"), F(copy, "ground")))
                        return e;
            return copy;
        }

        private object? FindBase(string id)
        {
            if (F(Definition, "bases") is IList bases)
                foreach (var b in bases) if (b != null && (F(b, "id") as string) == id) return b;
            return null;
        }

        private bool Execute(string simField, MethodInfo m, CampaignCommand c, out string why)
        {
            var target = F(Sim, simField);
            if (target == null) { why = "The host's campaign isn't running."; return false; }
            var ps = m.GetParameters();
            var args = new object?[ps.Length];
            try
            {
                for (int i = 0; i < ps.Length; i++)
                {
                    string? enc = c.Get("a" + i);
                    args[i] = enc == null ? (ps[i].HasDefaultValue ? ps[i].DefaultValue : null) : Dec(enc, ps[i].ParameterType);
                }
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return false;
            }
            var result = m.Invoke(target, args);
            if (result is string err && !string.IsNullOrEmpty(err)) { why = err; return false; }
            if (result is false) { why = "The campaign refused that."; return false; }
            // Predicted actions report failure through an out string ("refused"), a
            // (thing, error) tuple, or a null result.
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].IsOut && args[i] is string outErr && outErr.Length > 0) { why = outErr; return false; }
            if (result != null && result.GetType().IsGenericType
                && result.GetType().GetGenericTypeDefinition() == typeof(ValueTuple<,>)
                && result.GetType().GetField("Item2")?.GetValue(result) is string tupleErr && tupleErr.Length > 0)
            { why = tupleErr; return false; }
            if (result == null && m.ReturnType != typeof(void) && m.ReturnType != typeof(string) && !m.ReturnType.IsValueType)
            { why = "The campaign refused that."; return false; }
            why = "";
            RaiseStateChanged();
            return true;
        }

        public bool TryExecute(CampaignCommand command, out string reason)
        {
            if (command.Id == FieldSetCommand) return ExecuteFieldSet(command, out reason);
            return CampaignInterceptor.TryExecute(command, out reason);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PLAYER-EDITED FIELDS: map drawings, supply priorities
        //
        //  Neither goes through a method we can forward: drawings live in the game's
        //  own drawing layer (the fork copies it into state.Drawings only when it
        //  saves), and the Convoys panel writes SupplyPriorities directly. So each
        //  machine polls them; a guest sends its edited copy to the host
        //  (field.set), the host takes it and every guest gets the host's in the
        //  normal updates ("fld" lines). Last edit wins.
        // ══════════════════════════════════════════════════════════════════════

        private const string FieldSetCommand = "field.set";
        private static readonly string[] GuestEditedFields = { "Drawings", "SupplyPriorities" };
        private readonly Dictionary<string, int> _guestBaseline = new();
        private float _nextGuestPoll;

        private Type? _tDrawingTools;
        private FieldInfo? _keptField;
        private MethodInfo? _snapshotDrawings, _restoreDrawings;
        private bool _drawWarned;

        private bool ResolveDrawingTools()
        {
            _tDrawingTools ??= T("UI.Map.DrawingTools");
            if (_tDrawingTools == null) return false;
            _keptField        ??= AccessTools.Field(_tDrawingTools, "_kept");
            _snapshotDrawings ??= AccessTools.Method(_tDrawingTools, "Snapshot");
            _restoreDrawings  ??= AccessTools.Method(_tDrawingTools, "Restore");
            return _keptField != null && _snapshotDrawings != null && _restoreDrawings != null;
        }

        /// <summary>Copy the live drawing layer into state.Drawings (what the fork's own
        /// save does). Only when the layer exists - before the map has shown, the
        /// state's list is the truth and must not be wiped.</summary>
        private void SnapshotDrawings(object state)
        {
            try
            {
                if (!ResolveDrawingTools() || _keptField!.GetValue(null) == null) return;
                _snapshotDrawings!.Invoke(null, new[] { state });
            }
            catch (Exception ex)
            {
                if (!_drawWarned) { _drawWarned = true; Plugin.Log.LogWarning($"[Campaign] fork: reading map drawings failed: {ex.Message}"); }
            }
        }

        /// <summary>Show state.Drawings on the map: rebuild the live layer's graphics
        /// in place (the map keeps the layer object).</summary>
        private void RedrawFromState(object state)
        {
            try
            {
                if (!ResolveDrawingTools()) return;
                var kept = _keptField!.GetValue(null);
                if (kept == null) return;   // map not shown yet: it restores from state.Drawings itself
                var fresh = _restoreDrawings!.Invoke(null, new[] { F(state, "Drawings") });
                var have = AccessTools.Property(kept.GetType(), "Graphics")?.GetValue(kept) as IList;
                var want = fresh == null ? null : AccessTools.Property(fresh.GetType(), "Graphics")?.GetValue(fresh) as IList;
                if (have == null || want == null) return;
                var items = want.Cast<object>().ToList();
                want.Clear();
                have.Clear();
                foreach (var g in items) have.Add(g);
            }
            catch (Exception ex)
            {
                if (!_drawWarned) { _drawWarned = true; Plugin.Log.LogWarning($"[Campaign] fork: showing map drawings failed: {ex.Message}"); }
            }
        }

        private int FieldHash(object state, string field)
        {
            var v = F(state, field);
            return StableHash(IdRef.Replace(v == null ? "null" : ToJson(v), ""));
        }

        private void AfterFieldApplied(object state, string field)
        {
            if (field == "Drawings") RedrawFromState(state);
            if (Array.IndexOf(GuestEditedFields, field) >= 0)
            {
                if (field == "Drawings") SnapshotDrawings(state);   // what this machine now produces
                _guestBaseline[field] = FieldHash(state, field);
            }
        }

        /// <summary>GUEST, every frame: send what the local player changed.</summary>
        public void Tick()
        {
            if (Plugin.Instance.CfgIsHost.Value || !CampaignSync.IsActive || Phase != CampaignPhase.Map || IsLoading) return;
            if (UnityEngine.Time.unscaledTime < _nextGuestPoll) return;
            _nextGuestPoll = UnityEngine.Time.unscaledTime + 1f;
            var state = State;
            if (state == null) return;
            if (!ReferenceEquals(state, _baselineState)) { _guestBaseline.Clear(); _baselineState = state; }
            SnapshotDrawings(state);
            foreach (var field in GuestEditedFields)
            {
                if (AccessTools.Field(state.GetType(), field) == null) continue;
                int h = FieldHash(state, field);
                if (!_guestBaseline.TryGetValue(field, out var old)) { _guestBaseline[field] = h; continue; }  // first look
                if (h == old) continue;
                _guestBaseline[field] = h;
                var v = F(state, field);
                var cmd = new CampaignCommand { Id = FieldSetCommand };
                cmd.Arg("f", field).Arg("v", v == null ? "null" : ToJson(v));
                CampaignSync.InterceptLocal(cmd);
                Plugin.Log.LogInfo($"[Campaign] fork: sent my {field} to the host.");
            }
        }
        private object? _baselineState;

        private bool ExecuteFieldSet(CampaignCommand c, out string reason)
        {
            reason = "";
            string field = c.Get("f") ?? "";
            string json  = c.Get("v") ?? "null";
            if (Array.IndexOf(GuestEditedFields, field) < 0) { reason = $"'{field}' can't be set by a player."; return false; }
            var state = State;
            if (state == null) { reason = "No campaign loaded on the host."; return false; }
            try
            {
                ApplyField(state, field, json);
                if (field == "Drawings") RedrawFromState(state);
                RaiseStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                reason = "The host couldn't read it.";
                Plugin.Log.LogWarning($"[Campaign] fork: field.set {field} failed: {ex.Message}");
                return false;
            }
        }


        internal void RaiseStateChanged() => StateChanged?.Invoke();

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
