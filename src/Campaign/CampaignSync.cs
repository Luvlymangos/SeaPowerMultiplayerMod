using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Co-op campaign layer (fork addition).
    ///
    /// HOST-AUTHORITATIVE, SAME AS THE BATTLES. The host's campaign mod runs the
    /// only real campaign. On the guest the campaign mod keeps drawing the map and
    /// taking clicks, but:
    ///   - its autonomous logic is switched off (adapter.SetSimulationSuppressed), so
    ///     the enemy commander, land war, production and turn advance run once, on
    ///     the host - never twice;
    ///   - its player actions are intercepted before they run (adapter Harmony
    ///     prefixes call <see cref="InterceptLocal"/>) and sent to the host as
    ///     CampaignCommand. The host checks ownership, executes through the mod's
    ///     own code, and the guest sees the result in the next snapshot.
    ///
    /// Battles launched from the campaign go through the existing session path
    /// (SessionManager.CaptureAndSend), so everything the battle sync already does
    /// applies unchanged.
    ///
    /// Co-op only: in PvP the two players would need separate fogged views of the
    /// strategic map, which is a different design. CampaignSync stays inert there.
    /// </summary>
    public static class CampaignSync
    {
        private static ICampaignAdapter _adapter = NullCampaignAdapter.Instance;
        private static bool _adapterResolved;

        // ── Host state ────────────────────────────────────────────────────────
        private static uint  _revision;
        private static bool  _dirty;
        private static float _lastSnapshotAt = -999f;
        private static int   _lastSentHash;
        private static bool  _ownershipDirty = true;
        private static CampaignPhase _hostLastPhase = CampaignPhase.None;
        private static bool  _careerConfigured;
        private static bool  _fullRequested = true;
        private static float _lastFullAt = -999f;

        /// <summary>Command id a guest sends to take a Career post. Handled by
        /// CampaignSync itself, not by an adapter binding.</summary>
        public const string RequestPostCommand = "career.requestPost";

        /// <summary>Command ids for claiming / releasing a campaign asset (task force,
        /// squadron) outside Career. AssetKey = the asset. Handled by CampaignSync.</summary>
        public const string ClaimCommand   = "own.claim";
        public const string ReleaseCommand = "own.release";
        private static bool  _battleSentThisPhase;

        // ── Client state ──────────────────────────────────────────────────────
        private static uint  _appliedRevision;
        private static uint  _nextSeq = 1;
        private static readonly Dictionary<uint, CampaignCommand> _pending = new();
        private static bool  _suppressedLocally;
        private static bool  _resyncRequested;
        private static bool  _versionWarned;
        /// <summary>Host phase from the last applied snapshot. The guest's battle is
        /// loaded by the session path, not by its own campaign mod, so the LOCAL
        /// adapter phase cannot tell us a battle just ended - the host's can.</summary>
        private static CampaignPhase _lastHostPhase = CampaignPhase.None;

        private static uint     _asmRevision;
        private static byte[]?[] _asmChunks = Array.Empty<byte[]?>();
        private static int      _asmReceived;
        private static float    _asmStartedAt;
        private static CampaignPhase _asmPhase;
        private static string   _asmAdapterId = "";
        private static string   _asmModVersion = "";
        private static double   _asmHostClock;
        private static float    _asmHostTimeScale;

        private const int   ChunkBudgetBytes      = 800;   // under the ~1000 B reliable floor
        private const float ReassemblyTimeoutSec  = 10f;

        private static int _applyingDepth;

        // ── Join progress (for JoinProgress) ──────────────────────────────────
        /// <summary>Guest: has any campaign state been applied since connecting.</summary>
        public static bool AppliedAny => _appliedRevision > 0;
        /// <summary>Guest: a multi-piece snapshot is arriving - (received, total), or (0,0).</summary>
        public static (int received, int total) Receiving =>
            _asmChunks.Length > 1 ? (_asmReceived, _asmChunks.Length) : (0, 0);
        /// <summary>Host's campaign phase as last seen by the guest.</summary>
        public static CampaignPhase HostPhase => _lastHostPhase;

        /// <summary>True while CampaignSync is executing a network-originated action.
        /// Adapter interception prefixes must let the call through when this is set,
        /// otherwise the host's execution of a guest command would be intercepted too.</summary>
        public static bool ApplyingFromNetwork => _applyingDepth > 0;

        private sealed class ApplyScope : IDisposable
        {
            public void Dispose() => _applyingDepth--;
        }
        private static readonly ApplyScope _scope = new();
        private static IDisposable Applying() { _applyingDepth++; return _scope; }

        public static ICampaignAdapter Adapter => _adapter;

        /// <summary>Set by the adapter while the HOST's campaign simulation is stepping.
        /// The simulation calls some of the same methods players do (a flight sent home
        /// at bingo fuel); ownership is a rule for players, never for the simulation.
        /// Reset every frame in case a step threw.</summary>
        public static bool SimStepping;

        private static bool IsHost => Plugin.Instance.CfgIsHost.Value;

        /// <summary>Campaign sync is live: enabled, co-op, handshake done, mod present,
        /// and exactly two players on the same side.</summary>
        public static bool IsActive =>
            Plugin.Instance.CfgCampaignSync.Value
            && !Plugin.Instance.CfgPvP.Value
            && NetworkManager.Instance.IsEstablished
            && _adapter.IsAvailable
            && CoopPair(out _, out _);

        private static bool _pairWarned;

        /// <summary>
        /// The campaign layer is built for one host + one guest on the same side. The
        /// battle layer upstream supports up to 8 players and teams; the campaign
        /// layer's Host/Guest seats do not (yet), so anything else keeps it inert and
        /// says why, rather than half-working.
        /// </summary>
        public static bool CoopPair(out byte guestSlot, out string why)
        {
            guestSlot = PlayerRegistry.NoSender;
            int others = 0;
            Team? hostTeam = null, guestTeam = null;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || !p.Connected) continue;
                if (p.Slot == 0) { hostTeam = p.Team; continue; }
                others++;
                guestSlot = p.Slot;
                guestTeam = p.Team;
            }
            if (others != 1)
            {
                why = $"Co-op campaign supports exactly two players for now ({others + 1} connected).";
                return false;
            }
            if (hostTeam != null && guestTeam != null && hostTeam != guestTeam)
            {
                why = "Both players must be on the same side for a co-op campaign.";
                return false;
            }
            why = "";
            return true;
        }

        // ── Adapter resolution ────────────────────────────────────────────────

        /// <summary>Lazy: other BepInEx plugins (the campaign mod) may load after us,
        /// so look on first tick rather than in Awake.</summary>
        private static float _nextResolveAt;
        private static bool _loggedNone;

        private static void ResolveAdapter()
        {
            if (_adapterResolved) return;
            // Mod loaders (R.A.T.S. Keel, Anchor Chain) start campaign mods well after
            // BepInEx plugins - tens of seconds, one per frame - so keep looking until
            // one turns up instead of deciding once at start-up.
            if (Time.unscaledTime < _nextResolveAt) return;
            _nextResolveAt = Time.unscaledTime + 2f;

            ICampaignAdapter[] candidates =
            {
                new DynamicCampaignForkAdapter(),   // Pacific 2027 (first target)
                new BungalowCampaignAdapter(),
            };

            foreach (var c in candidates)
            {
                try
                {
                    if (!c.IsAvailable) continue;
                    _adapterResolved = true;
                    _adapter = c;
                    _adapter.StateChanged += OnLocalStateChanged;
                    Plugin.Log.LogInfo($"[Campaign] Using adapter '{c.AdapterId}' (mod version {c.ModVersion})");
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[Campaign] Adapter probe failed: {ex}");
                }
            }
            if (!_loggedNone)
            {
                _loggedNone = true;
                Plugin.Log.LogInfo("[Campaign] No supported campaign mod loaded yet - will keep checking.");
            }
        }

        // ── Called by adapter interception prefixes (both sides) ─────────────

        /// <summary>
        /// A local player action is about to run on the campaign map. Returns true
        /// when the original code should run. On an active guest it returns false
        /// and forwards the action to the host instead.
        /// </summary>
        public static bool InterceptLocal(CampaignCommand command)
        {
            if (ApplyingFromNetwork) return true;
            if (!IsActive) return true;
            if (IsHost)
            {
                // The host's simulation (or anything off the map) is never refused.
                if (SimStepping || _adapter.Phase != CampaignPhase.Map) return true;
                // Host acting locally: respect the guest's assets, then let it run.
                if (!CampaignOwnership.MayCommand(CampaignSeat.Host, command.AssetKey))
                {
                    Telemetry.Count("campaign.hostRefusedOwnership");
                    SimSyncManager.ReportIssue("That belongs to your partner.",
                        $"{command.AssetKey} is assigned to the other player.", warning: true);
                    return false;
                }
                return true;
            }

            if (!CampaignOwnership.MayCommand(CampaignSeat.Guest, command.AssetKey))
            {
                SimSyncManager.ReportIssue("That belongs to the host.",
                    $"{command.AssetKey} is assigned to the other player.", warning: true);
                return false;
            }

            uint seq = _nextSeq++;
            _pending[seq] = command;
            NetworkManager.Instance.SendToServer(new CampaignCommandMessage { Seq = seq, Command = command });
            Plugin.Log.LogInfo($"[Campaign] -> host #{seq}: {command}");
            Telemetry.Count("campaign.cmdSent");
            return false;
        }

        // ── Host: receive guest command ───────────────────────────────────────

        internal static void HandleCommand(byte fromSlot, CampaignCommandMessage msg)
        {
            if (!IsHost) return;
            // Two-player only (CoopPair): the one guest is the Guest seat.
            var reply = new CampaignCommandResultMessage { Seq = msg.Seq };

            if (!IsActive)
            {
                reply.Reason = "Campaign sync is not active on the host.";
            }
            else if (msg.Command.Id == RequestPostCommand)
            {
                reply.Accepted = TryGrantPost(msg.Command.AssetKey, out reply.Reason);
            }
            else if (msg.Command.Id == ClaimCommand || msg.Command.Id == ReleaseCommand)
            {
                reply.Accepted = TryClaim(CampaignSeat.Guest, msg.Command.AssetKey,
                                          msg.Command.Id == ClaimCommand, out reply.Reason);
            }
            else if (!CampaignOwnership.MayCommand(CampaignSeat.Guest, msg.Command.AssetKey))
            {
                reply.Reason = $"{msg.Command.AssetKey} is assigned to the host.";
            }
            else
            {
                bool ok;
                string reason;
                try
                {
                    using (Applying())
                        ok = _adapter.TryExecute(msg.Command, out reason);
                }
                catch (Exception ex)
                {
                    ok = false;
                    reason = "Host error while executing the order.";
                    Plugin.Log.LogError($"[Campaign] TryExecute threw for {msg.Command}: {ex}");
                }
                reply.Accepted = ok;
                reply.Reason = reason ?? "";
                if (ok)
                {
                    _dirty = true;
                    reply.AppliedInRevision = _revision + 1;
                }
            }

            Plugin.Log.LogInfo($"[Campaign] <- guest #{msg.Seq}: {msg.Command} => {(reply.Accepted ? "OK" : "REFUSED: " + reply.Reason)}");
            Telemetry.Count(reply.Accepted ? "campaign.cmdAccepted" : "campaign.cmdRefused");
            NetworkManager.Instance.BroadcastToClients(reply);
        }

        internal static void HandleResyncRequest(CampaignResyncRequestMessage msg)
        {
            if (!IsHost) return;
            Plugin.Log.LogInfo($"[Campaign] Guest requested resync (has r{msg.HaveRevision}): {msg.Reason}");
            _lastSentHash = 0;  // force a send even if unchanged
            _dirty = true;
            _fullRequested = true;
            _ownershipDirty = true;
        }

        /// <summary>
        /// HOST, Career: hand a free post to the guest. A post is free when no human
        /// holds it. The guest's previous post (if any) goes back to the AI.
        /// </summary>
        private static bool TryGrantPost(string postKey, out string reason)
        {
            if (!_adapter.IsCareer) { reason = "The host's campaign is not in Career mode."; return false; }
            bool exists = false;
            foreach (var p in _adapter.ListPosts()) if (p.key == postKey) { exists = true; break; }
            if (!exists) { reason = $"No post '{postKey}' in this campaign."; return false; }

            var owner = CampaignOwnership.OwnerOf(postKey);
            if (owner == CampaignSeat.Host) { reason = "The host holds that post."; return false; }
            if (owner == CampaignSeat.Guest) { reason = ""; return true; }

            string? previous = CampaignOwnership.HeldBy(CampaignSeat.Guest);
            if (previous != null)
            {
                CampaignOwnership.Assign(previous, CampaignSeat.Locked);
                _adapter.SetPostHeldByHuman(previous, false);
            }
            CampaignOwnership.Assign(postKey, CampaignSeat.Guest);
            _adapter.SetPostHeldByHuman(postKey, true);
            _dirty = true;
            reason = "";
            Plugin.Log.LogInfo($"[Campaign] Career: guest now holds post '{postKey}'" +
                               (previous != null ? $" (released '{previous}')" : ""));
            return true;
        }

        /// <summary>
        /// Claim (or release) a campaign asset outside Career. Everything starts
        /// Shared; a claimed asset only its owner may order. Nobody can claim what
        /// the partner holds - the partner has to release it first.
        /// Both seats go through here: the host's own claims call it directly
        /// (<see cref="ClaimLocal"/>), the guest's arrive as commands.
        /// </summary>
        private static bool TryClaim(CampaignSeat who, string assetKey, bool claim, out string reason)
        {
            if (string.IsNullOrEmpty(assetKey)) { reason = "Nothing selected."; return false; }
            var cur = CampaignOwnership.OwnerOf(assetKey);
            if (claim)
            {
                if (cur == who) { reason = ""; return true; }
                if (cur != CampaignSeat.Shared) { reason = "Your partner commands that. Ask them to release it."; return false; }
                CampaignOwnership.Assign(assetKey, who);
            }
            else
            {
                if (cur != who) { reason = "That isn't yours to release."; return false; }
                CampaignOwnership.Assign(assetKey, CampaignSeat.Shared);
            }
            _dirty = true;
            reason = "";
            Plugin.Log.LogInfo($"[Campaign] {who} {(claim ? "claimed" : "released")} '{assetKey}'.");
            return true;
        }

        /// <summary>Claim/release from the local player (UI). Host: applied at once.
        /// Guest: sent to the host like any other command.</summary>
        public static void ClaimLocal(string assetKey, bool claim)
        {
            if (!IsActive) return;
            if (IsHost)
            {
                if (!TryClaim(CampaignSeat.Host, assetKey, claim, out var why))
                    SimSyncManager.ReportIssue("Can't change command.", why, warning: true);
                return;
            }
            var cmd = new CampaignCommand { Id = claim ? ClaimCommand : ReleaseCommand, AssetKey = assetKey };
            uint seq = _nextSeq++;
            _pending[seq] = cmd;
            NetworkManager.Instance.SendToServer(new CampaignCommandMessage { Seq = seq, Command = cmd });
        }

        /// <summary>
        /// HOST, once per session: in Career, nothing is shared by default - every
        /// post is the AI's (Locked) until a human holds it, and the host's own
        /// officer's post is the host's.
        /// </summary>
        private static void ConfigureCareerOnce()
        {
            if (_careerConfigured || !_adapter.IsCareer) return;
            _careerConfigured = true;
            CampaignOwnership.SetDefault(CampaignSeat.Locked);
            var hostPost = _adapter.LocalPlayerPost;
            if (hostPost != null)
            {
                CampaignOwnership.Assign(hostPost, CampaignSeat.Host);
                _adapter.SetPostHeldByHuman(hostPost, true);
            }
            Plugin.Log.LogInfo($"[Campaign] Career mode: default=Locked, host post='{hostPost ?? "(none)"}', " +
                               $"{_adapter.ListPosts().Count} posts available to the guest.");
        }

        private static void OnLocalStateChanged()
        {
            if (IsHost) _dirty = true;
        }

        // ── Host: snapshot stream ─────────────────────────────────────────────

        private static void HostTick()
        {
            ConfigureCareerOnce();

            if (CampaignOwnershipChangedSinceSend())
            {
                NetworkManager.Instance.BroadcastToClients(CampaignOwnership.ToMessage());
                _ownershipDirty = false;
            }

            TickBattleBridge();

            if (!_dirty && !_fullRequested) return;
            // Continuous clock: while it runs, nearly every frame changes something,
            // so StateChanged is effectively always set and this interval IS the
            // snapshot rate. The guest's presentation-only movement fills the gaps.
            if (Time.unscaledTime - _lastSnapshotAt < Plugin.Instance.CfgCampaignSnapshotIntervalSec.Value) return;
            _dirty = false;
            _lastSnapshotAt = Time.unscaledTime;

            bool full = _fullRequested;
            string state;
            try { state = _adapter.CaptureState(full) ?? ""; }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Campaign] CaptureState threw: {ex}");
                return;
            }

            if (state.Length == 0) return;      // adapter: nothing to send (or not ready)
            if (full) { _fullRequested = false; _lastFullAt = Time.unscaledTime; }
            int hash = StableHash(state);
            if (!full && hash == _lastSentHash) return;  // nothing the guest can see changed
            _lastSentHash = hash;

            SendSnapshot(state, _adapter.Phase);
        }

        private static void SendSnapshot(string state, CampaignPhase phase)
        {
            byte[] packed = Gzip(state);
            int chunkCount = Mathf.Max(1, Mathf.CeilToInt(packed.Length / (float)ChunkBudgetBytes));
            if (chunkCount > ushort.MaxValue)
            {
                Plugin.Log.LogError($"[Campaign] Snapshot too large ({packed.Length} B) - not sent.");
                return;
            }

            uint rev = ++_revision;
            for (int i = 0; i < chunkCount; i++)
            {
                int off = i * ChunkBudgetBytes;
                int len = Mathf.Min(ChunkBudgetBytes, packed.Length - off);
                var part = new byte[len];
                Buffer.BlockCopy(packed, off, part, 0, len);
                NetworkManager.Instance.BroadcastToClients(new CampaignSnapshotChunkMessage
                {
                    Revision   = rev,
                    ChunkIdx   = (ushort)i,
                    ChunkCount = (ushort)chunkCount,
                    Phase      = phase,
                    AdapterId  = _adapter.AdapterId,
                    ModVersion = _adapter.ModVersion,
                    HostClock     = _adapter.Clock,
                    HostTimeScale = _adapter.TimeScale,
                    Payload    = part,
                });
            }
            Plugin.Log.LogInfo($"[Campaign] Snapshot r{rev} sent: {state.Length} ch -> {packed.Length} B in {chunkCount} chunks, phase={phase}");
            Telemetry.Count("campaign.snapshotSent");
        }

        private static bool CampaignOwnershipChangedSinceSend() => _ownershipDirty;

        /// <summary>
        /// When the campaign generates a battle and it finishes loading on the host,
        /// push it to the guest through the normal session path, once per battle.
        /// </summary>
        private static void TickBattleBridge()
        {
            var phase = _adapter.Phase;
            if (phase != _hostLastPhase)
            {
                Plugin.Log.LogInfo($"[Campaign] Host phase {_hostLastPhase} -> {phase}");
                _hostLastPhase = phase;
                _battleSentThisPhase = false;
                _dirty = true;  // guest needs to know the phase changed
                // Back on the map (campaign loaded, or a battle's result absorbed):
                // the guest gets a complete copy, not a delta.
                if (phase == CampaignPhase.Map) _fullRequested = true;
            }

            if (phase != CampaignPhase.Battle || _battleSentThisPhase) return;
            if (!Plugin.Instance.CfgCampaignAutoSendBattles.Value) return;
            if (!SessionManager.MissionIsLive || SessionManager.SceneLoading) return;

            _battleSentThisPhase = true;
            SeedBattleOwnership();
            Plugin.Log.LogInfo("[Campaign] Campaign battle is live - sending it to the guest.");
            SessionManager.CaptureAndSend();
        }

        /// <summary>
        /// HOST, Career battle about to be sent: give the guest the formations that
        /// came from the post they hold.
        ///
        /// Upstream's rule is that the first player on a side - the host, at capture -
        /// takes every unowned unit (FormationOwnership.HostGrantTeamTo). Assigning the
        /// guest's units first means that grant skips them; the host gets their own
        /// post plus the AI posts' units, and splits those with "send formation" as in
        /// any co-op mission. Aircraft launched later inherit their carrier's owner
        /// (HostOnUnitSpawned), so a guest's carrier keeps its air wing.
        /// </summary>
        private static void SeedBattleOwnership()
        {
            if (!OwnershipRules.LockActive) return;
            if (!CoopPair(out byte guestSlot, out _)) return;

            int assigned = 0;
            var units = UnitRegistry.All;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.UniqueID == 0 || u is WeaponBase) continue;
                if (!Teams.IsFriendly(u)) continue;
                if (FormationOwnership.OwnerOf(u.UniqueID) == guestSlot) continue;
                string? key = _adapter.OwnerKeyOfBattleUnit(u);
                if (key == null || CampaignOwnership.OwnerOf(key) != CampaignSeat.Guest) continue;
                // Whole formation: formations are what players command and send.
                FormationOwnership.HostAssignFormationOf(u, guestSlot);
                assigned++;
            }
            Plugin.Log.LogInfo($"[Campaign] Battle: gave the guest the formations of the assets they command ({assigned} anchor unit(s)); " +
                               "Shared and host assets start with the host - send formations to share.");
        }

        // ── Client: receive ───────────────────────────────────────────────────

        internal static void HandleResult(CampaignCommandResultMessage msg)
        {
            _pending.TryGetValue(msg.Seq, out var cmd);
            _pending.Remove(msg.Seq);
            if (msg.Accepted)
            {
                Plugin.Log.LogInfo($"[Campaign] Host accepted #{msg.Seq} ({cmd}) - in r{msg.AppliedInRevision}");
                return;
            }
            Plugin.Log.LogWarning($"[Campaign] Host refused #{msg.Seq} ({cmd}): {msg.Reason}");
            SimSyncManager.ReportIssue("The host refused that order.", msg.Reason, warning: true);
        }

        internal static void HandleOwnership(CampaignOwnershipMessage msg)
        {
            if (IsHost) return;
            CampaignOwnership.ApplyReceived(msg);
        }

        internal static void HandleSnapshotChunk(CampaignSnapshotChunkMessage msg)
        {
            if (IsHost) return;
            if (msg.Revision <= _appliedRevision) return;  // stale

            if (msg.Revision != _asmRevision)
            {
                // Newer revision supersedes whatever was half-built. Updates are
                // deltas, so a lost one means the map may be missing something:
                // ask for a complete copy.
                if (_asmChunks.Length > 0 && _asmReceived < _asmChunks.Length)
                {
                    _resyncRequested = false;
                    RequestResync($"r{_asmRevision} superseded before it completed");
                }
                _asmRevision  = msg.Revision;
                _asmChunks    = new byte[]?[msg.ChunkCount];
                _asmReceived  = 0;
                _asmStartedAt = Time.unscaledTime;
                _asmPhase     = msg.Phase;
            }
            if (msg.ChunkIdx >= _asmChunks.Length || _asmChunks[msg.ChunkIdx] != null) return;

            _asmChunks[msg.ChunkIdx] = msg.Payload;
            _asmReceived++;
            if (msg.ChunkIdx == 0)
            {
                _asmAdapterId     = msg.AdapterId;
                _asmModVersion    = msg.ModVersion;
                _asmHostClock     = msg.HostClock;
                _asmHostTimeScale = msg.HostTimeScale;
            }
            if (_asmReceived < _asmChunks.Length) return;

            // Complete.
            int total = 0;
            foreach (var c in _asmChunks) total += c!.Length;
            var packed = new byte[total];
            int off = 0;
            foreach (var c in _asmChunks) { Buffer.BlockCopy(c!, 0, packed, off, c!.Length); off += c!.Length; }
            uint rev = _asmRevision;
            _asmChunks = Array.Empty<byte[]?>();

            ApplySnapshot(rev, Gunzip(packed), _asmPhase, _asmAdapterId, _asmModVersion,
                          _asmHostClock, _asmHostTimeScale);
        }

        private static void ApplySnapshot(uint rev, string state, CampaignPhase phase, string adapterId, string modVersion,
                                          double hostClock, float hostTimeScale)
        {
            if (adapterId != _adapter.AdapterId)
            {
                SimSyncManager.ReportIssue("Campaign mod mismatch.",
                    $"The host's campaign is '{adapterId}' but this machine has '{_adapter.AdapterId}'. Install the same campaign mod.");
                return;
            }
            if (modVersion != _adapter.ModVersion && !_versionWarned)
            {
                _versionWarned = true;
                SimSyncManager.ReportIssue("Campaign mod versions differ.",
                    $"Host has {modVersion}, you have {_adapter.ModVersion}. Update both to the same version.", warning: true);
            }

            var prevHostPhase = _lastHostPhase;
            try
            {
                using (Applying())
                {
                    _adapter.ApplyState(state, phase, hostClock, hostTimeScale);
                    // Host finished the battle and went back to the map: follow it.
                    if (prevHostPhase == CampaignPhase.Battle
                        && (phase == CampaignPhase.Map || phase == CampaignPhase.Resolving))
                        _adapter.ReturnToMap();
                }
                _appliedRevision = rev;
                _lastHostPhase = phase;
                _resyncRequested = false;
                Plugin.Log.LogInfo($"[Campaign] Applied snapshot r{rev} ({state.Length} ch, phase={phase})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Campaign] ApplyState threw on r{rev}: {ex}");
                RequestResync("apply failed");
            }
        }

        private static void ClientTick()
        {
            if (!_suppressedLocally)
            {
                _adapter.SetSimulationSuppressed(true);
                _suppressedLocally = true;
                Plugin.Log.LogInfo("[Campaign] Guest campaign logic suppressed - host runs the campaign.");
                RequestResync("joined");
            }

            if (_asmChunks.Length > 0 && Time.unscaledTime - _asmStartedAt > ReassemblyTimeoutSec)
            {
                Plugin.Log.LogWarning($"[Campaign] Snapshot r{_asmRevision} incomplete after {ReassemblyTimeoutSec}s ({_asmReceived}/{_asmChunks.Length}).");
                _asmChunks = Array.Empty<byte[]?>();
                RequestResync("reassembly timeout");
            }
        }

        /// <summary>Guest: ask the host for a complete copy (adapter found it can't
        /// apply a delta, or the player pressed the resync key).</summary>
        public static void RequestFull(string reason)
        {
            if (IsHost || !IsActive) return;
            _resyncRequested = false;
            RequestResync(reason);
        }

        private static void RequestResync(string reason)
        {
            if (_resyncRequested) return;
            _resyncRequested = true;
            NetworkManager.Instance.SendToServer(new CampaignResyncRequestMessage
            {
                HaveRevision = _appliedRevision,
                Reason = reason,
            });
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        static CampaignSync()
        {
            CampaignOwnership.Changed += () => _ownershipDirty = true;
        }

        /// <summary>Plugin.Update, every frame.</summary>
        public static void Tick()
        {
            SimStepping = false;
            ResolveAdapter();
            if (!_pairWarned && _adapter.IsAvailable && Plugin.Instance.CfgCampaignSync.Value
                && !Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsEstablished
                && !CoopPair(out _, out var why))
            {
                _pairWarned = true;
                SimSyncManager.ReportIssue("Co-op campaign is off for this session.", why, warning: true);
            }
            if (!IsActive)
            {
                if (_suppressedLocally) Release();
                return;
            }
            if (IsHost) HostTick(); else ClientTick();
            ClaimPanel.EnsureCreated();
            TickHotkeys();
        }

        private static void TickHotkeys()
        {
            if (_adapter.Phase != CampaignPhase.Map) return;
            if (Plugin.Instance.CfgCampaignKeyClaim.Value.IsDown())
            {
                string? key = _adapter.SelectedAssetKey();
                if (key == null)
                {
                    SimSyncManager.ReportIssue("Select a task force or flight first.",
                        "Then press the claim key again to claim or release it.", warning: true);
                    return;
                }
                var owner = CampaignOwnership.OwnerOf(key);
                var me = IsHost ? CampaignSeat.Host : CampaignSeat.Guest;
                ClaimLocal(key, claim: owner != me);
            }
            else if (!IsHost && Plugin.Instance.CfgCampaignKeyResync.Value.IsDown())
            {
                RequestFull("player pressed resync");
            }
        }

        private static void Release()
        {
            _adapter.SetSimulationSuppressed(false);
            _suppressedLocally = false;
            Plugin.Log.LogInfo("[Campaign] Guest campaign logic released.");
        }

        /// <summary>NetworkManager.OnPeerDisconnected.</summary>
        public static void Reset()
        {
            if (_suppressedLocally) Release();
            if (IsHost && !_adapter.IsCareer)
            {
                // The guest left: what they claimed goes back to Shared, so the host
                // can order it.
                int n = CampaignOwnership.ReassignAll(CampaignSeat.Guest, CampaignSeat.Shared);
                if (n > 0) Plugin.Log.LogInfo($"[Campaign] Guest left - {n} claimed asset(s) back to shared.");
            }
            else if (IsHost)
            {
                // The guest left: their post goes back to the campaign's AI so it is
                // not left drifting with nobody in command.
                string? guestPost = CampaignOwnership.HeldBy(CampaignSeat.Guest);
                if (guestPost != null)
                {
                    CampaignOwnership.Assign(guestPost, CampaignSeat.Locked);
                    _adapter.SetPostHeldByHuman(guestPost, false);
                    Plugin.Log.LogInfo($"[Campaign] Guest left - post '{guestPost}' returned to the AI.");
                }
            }
            _dirty = false;
            _ownershipDirty = true;  // re-send to the next guest
            _lastSentHash = 0;
            _lastSnapshotAt = -999f;
            _hostLastPhase = CampaignPhase.None;
            _battleSentThisPhase = false;
            _appliedRevision = 0;
            _lastHostPhase = CampaignPhase.None;
            _pending.Clear();
            _resyncRequested = false;
            _versionWarned = false;
            _pairWarned = false;
            _asmChunks = Array.Empty<byte[]?>();
            _asmRevision = 0;
            // Ownership is host-configured and deliberately survives a reconnect on
            // the host. The guest's mirror is cleared and re-sent on join.
            if (!IsHost) CampaignOwnership.Reset();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>FNV-1a - string.GetHashCode is not stable across runs.</summary>
        private static int StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
                return (int)h;
            }
        }

        private static byte[] Gzip(string s)
        {
            var raw = Encoding.UTF8.GetBytes(s);
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        private static string Gunzip(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var gz = new GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return Encoding.UTF8.GetString(outMs.ToArray());
        }
    }
}
