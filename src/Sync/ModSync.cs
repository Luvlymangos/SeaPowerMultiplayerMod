using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using SeaPower;
using SeapowerMultiplayer.Messages;
using Steamworks;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Fork addition: make a guest's mods match the host's.
    ///
    /// Upstream ModSetCheck only compares a fingerprint and warns. This goes further:
    /// the host sends its enabled mod list (in load order, with each Workshop item's
    /// install timestamp), and the guest's game then
    ///   - subscribes to Workshop mods it doesn't have, and asks Steam to download them;
    ///   - asks Steam to update Workshop mods that are older than the host's;
    ///   - ticks the host's mods and unticks any others in the game's mod list, in the
    ///     host's load order (the same FileManager.SaveDirectories the mod menu uses);
    ///   - says what it changed and that a restart is needed (mods load at start-up).
    /// Mods that aren't on the Workshop can't be fetched; they're named so the host
    /// can hand them over.
    ///
    /// Workshop mods are recognised by their folder, which Steam names after the
    /// item's published-file id.
    /// </summary>
    internal static class ModSync
    {
        private sealed class Entry
        {
            public string Key = "";      // folder name (= Workshop id for Workshop mods)
            public string Name = "";     // display name
            public ulong  WorkshopId;    // 0 = not a Workshop mod
            public uint   Timestamp;     // host's install time of that version (0 = unknown)
        }

        // Host
        private static readonly HashSet<byte> _sentTo = new();
        // Guest
        private static Entry[]? _incoming;
        private static int _received;
        private static readonly HashSet<ulong> _waitingFor = new();
        private static float _waitStarted;

        private static bool Enabled => Plugin.Instance.CfgModSync.Value && NetworkManager.Instance.IsEstablished;
        private static bool IsHost => Plugin.Instance.CfgIsHost.Value;

        // ── Progress (for JoinProgress) ───────────────────────────────────────
        private static int _downloadTotal;
        internal static bool RestartNeeded { get; private set; }
        internal static bool ListComplete => _incoming != null && _received >= _incoming.Length;
        internal static bool Requested => _requested;
        internal static float ListFraction => _incoming == null || _incoming.Length == 0 ? 0f : _received / (float)_incoming.Length;
        internal static int DownloadsLeft => _waitingFor.Count;
        internal static int DownloadsTotal => _downloadTotal;

        // ── Local mod list ────────────────────────────────────────────────────

        private static List<SearchDirectory> SettingsMods()
        {
            var list = new List<SearchDirectory>();
            if (!Singleton<FileManager>.InstanceExists(false)) return list;
            var dirs = Singleton<FileManager>.Instance.GetSearchDirectories();
            if (dirs == null) return list;
            foreach (var d in dirs)
                if (d != null && d.IsSettingsMod && d.DirectoryInfo != null) list.Add(d);
            return list;
        }

        private static ulong WorkshopIdOf(SearchDirectory d) =>
            d.IsSteam && ulong.TryParse(d.DirectoryInfo.Name, out var id) ? id : 0;

        private static uint InstalledAt(ulong id)
        {
            try
            {
                return SteamUGC.GetItemInstallInfo(new PublishedFileId_t(id), out _, out _, 1024u, out uint ts) ? ts : 0;
            }
            catch { return 0; }
        }

        // ── Host: send the list to each guest once ────────────────────────────

        public static void Tick()
        {
            if (!Enabled) { if (_sentTo.Count > 0) _sentTo.Clear(); return; }
            if (IsHost) HostTick(); else GuestTick();
        }

        private static void HostTick() { }   // the guest asks (RequestText) once it is fully connected

        /// <summary>Guest → host report text that means "send me your list".</summary>
        private const string RequestText = "\u0001send-list";
        private static bool _requested;

        private static void SendList(byte slot)
        {
            var mods = SettingsMods().Where(d => d.IsChecked).ToList();
            ushort total = (ushort)mods.Count;
            // One small message per mod: a forty-mod list in one packet would go over
            // the ~1000-byte reliable limit (see ModSetCheck).
            NetworkManager.Instance.SendToSlot(slot, new ModListEntryMessage { Index = 0, Total = total, Key = "" , Header = true });
            for (int i = 0; i < mods.Count; i++)
            {
                var d = mods[i];
                ulong id = WorkshopIdOf(d);
                NetworkManager.Instance.SendToSlot(slot, new ModListEntryMessage
                {
                    Index = (ushort)i, Total = total,
                    Key = d.DirectoryInfo.Name,
                    Name = string.IsNullOrEmpty(d.Name) ? d.DirectoryInfo.Name : d.Name,
                    WorkshopId = id,
                    Timestamp = id != 0 ? InstalledAt(id) : 0,
                });
            }
            Plugin.Log.LogInfo($"[ModSync] Sent my {mods.Count} enabled mods to {PlayerRegistry.DisplayName(slot)}.");
        }

        internal static void HandleReport(byte fromSlot, ModSyncReportMessage msg)
        {
            if (!IsHost) return;
            if (msg.Summary == RequestText)
            {
                Plugin.Log.LogInfo($"[ModSync] {PlayerRegistry.DisplayName(fromSlot)} asked for my mod list.");
                SendList(fromSlot);
                return;
            }
            string who = PlayerRegistry.DisplayName(fromSlot);
            if (msg.Summary.Length == 0) { Plugin.Log.LogInfo($"[ModSync] {who}'s mods match."); return; }
            Plugin.Log.LogInfo($"[ModSync] {who}: {msg.Summary}");
            SimSyncManager.ReportIssue($"{who} is updating mods to match yours.", msg.Summary, warning: true);
        }

        // ── Guest ─────────────────────────────────────────────────────────────

        internal static void HandleEntry(ModListEntryMessage msg)
        {
            if (IsHost || !Plugin.Instance.CfgModSync.Value) return;
            if (msg.Header)
            {
                _incoming = new Entry[msg.Total];
                _received = 0;
                if (msg.Total == 0) Apply(_incoming);
                return;
            }
            if (_incoming == null || msg.Index >= _incoming.Length || _incoming[msg.Index] != null) return;
            _incoming[msg.Index] = new Entry { Key = msg.Key, Name = msg.Name, WorkshopId = msg.WorkshopId, Timestamp = msg.Timestamp };
            if (++_received == _incoming.Length) Apply(_incoming);
        }

        private static void Apply(Entry[] host)
        {
            if (RestartNeeded)
            {
                // Mods were changed earlier in this game session: the list matches now,
                // but they only load at start-up. Say so again instead of "matches".
                Plugin.Log.LogInfo("[ModSync] Mods were changed earlier this session - still waiting for a restart.");
                SimSyncManager.ReportIssue("Restart Sea Power to load the host's mods.",
                    "Your mod list was changed to match the host's, but mods only load when the game starts. Quit, start Sea Power again and rejoin.");
                NetworkManager.Instance.SendToServer(new ModSyncReportMessage { Summary = "changed mods earlier but hasn't restarted yet - their new mods aren't loaded" });
                return;
            }
            Plugin.Log.LogInfo($"[ModSync] Host has {host.Length} mods enabled: " +
                               string.Join(", ", host.Select(e => e.WorkshopId != 0 ? $"{e.Name} [{e.WorkshopId}]" : $"{e.Name} [local:{e.Key}]")));
            var before = SettingsMods();
            Plugin.Log.LogInfo($"[ModSync] I have {before.Count} mods in my list, {before.Count(d => d.IsChecked)} ticked: " +
                               string.Join(", ", before.Select(d => $"{(d.IsChecked ? "+" : "-")}{d.Name} [{d.DirectoryInfo.Name}{(d.IsSteam ? "" : ", local")}]")));
            var notes = new List<string>();
            bool restart = false;

            // 1. Workshop mods the guest lacks or has an older version of.
            foreach (var e in host.Where(e => e.WorkshopId != 0))
            {
                var fid = new PublishedFileId_t(e.WorkshopId);
                var state = (EItemState)SteamUGC.GetItemState(fid);
                bool subscribed = state.HasFlag(EItemState.k_EItemStateSubscribed);
                bool installed  = state.HasFlag(EItemState.k_EItemStateInstalled);
                uint mine = installed ? InstalledAt(e.WorkshopId) : 0;

                if (!subscribed)
                {
                    SteamUGC.SubscribeItem(fid);
                    SteamUGC.DownloadItem(fid, true);
                    _waitingFor.Add(e.WorkshopId);
                    notes.Add($"subscribed to {e.Name}");
                }
                else if (!installed || state.HasFlag(EItemState.k_EItemStateNeedsUpdate)
                         || (e.Timestamp != 0 && mine != 0 && mine < e.Timestamp))
                {
                    SteamUGC.DownloadItem(fid, true);
                    _waitingFor.Add(e.WorkshopId);
                    notes.Add($"updating {e.Name}");
                }
            }

            // 2. Mods that aren't on the Workshop and aren't here: can't be fetched.
            var local = SettingsMods();
            var localKeys = new HashSet<string>(local.Select(d => d.DirectoryInfo.Name), StringComparer.OrdinalIgnoreCase);
            var notOnWorkshop = host.Where(e => e.WorkshopId == 0 && !localKeys.Contains(e.Key)).Select(e => e.Name).ToList();
            if (notOnWorkshop.Count > 0)
                notes.Add("not on the Workshop, get these from the host: " + string.Join(", ", notOnWorkshop));

            // 3. Tick/untick and order what is here now.
            if (MatchChecks(host, out var changed)) restart = true;
            notes.AddRange(changed);

            _downloadTotal = _waitingFor.Count;
            if (_waitingFor.Count > 0)
            {
                _waitStarted = Time.unscaledTime;
                Plugin.Log.LogInfo($"[ModSync] Waiting for Steam to download {_waitingFor.Count} mod(s).");
                SimSyncManager.ReportIssue("Downloading the host's mods…",
                    $"Steam is downloading {_waitingFor.Count} mod(s). You'll be told when to restart.", warning: true);
            }
            else if (restart || notOnWorkshop.Count > 0)
            {
                FinishMessage(notes, notOnWorkshop.Count > 0 && !restart);
            }
            else
            {
                Plugin.Log.LogInfo("[ModSync] My mods already match the host's.");
            }

            NetworkManager.Instance.SendToServer(new ModSyncReportMessage { Summary = string.Join("; ", notes) });
        }

        /// <summary>Tick the host's mods (in the host's order, ahead of the rest),
        /// untick the others. True when anything changed.</summary>
        private static bool MatchChecks(Entry[] host, out List<string> changed)
        {
            changed = new List<string>();
            if (!Singleton<FileManager>.InstanceExists(false)) return false;
            var fm = Singleton<FileManager>.Instance;
            var dirs = fm.GetSearchDirectories();
            if (dirs == null) return false;

            var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < host.Length; i++) wanted[host[i].Key] = i;

            bool any = false;
            foreach (var d in dirs)
            {
                if (d == null || !d.IsSettingsMod || d.DirectoryInfo == null) continue;
                bool shouldBeOn = wanted.ContainsKey(d.DirectoryInfo.Name);
                if (!shouldBeOn && !Plugin.Instance.CfgModSyncDisableExtras.Value) continue;
                if (d.IsChecked != shouldBeOn)
                {
                    d.IsChecked = shouldBeOn;
                    if (d.IsChecked != shouldBeOn)
                        Plugin.Log.LogWarning($"[ModSync] The game refused to {(shouldBeOn ? "tick" : "untick")} {d.Name} [{d.DirectoryInfo.Name}] (enabled={d.IsEnabled}).");
                    changed.Add((shouldBeOn ? "turned on " : "turned off ") + (string.IsNullOrEmpty(d.Name) ? d.DirectoryInfo.Name : d.Name));
                    any = true;
                }
            }

            // Load order: where two mods replace the same file, the later one wins, so
            // put the host's mods in the host's order. Only the host's mods move.
            var mine = dirs.Where(d => d != null && d.IsSettingsMod && d.DirectoryInfo != null && wanted.ContainsKey(d.DirectoryInfo.Name)).ToList();
            var slots = mine.Select(d => dirs.IndexOf(d)).OrderBy(i => i).ToList();
            var ordered = mine.OrderBy(d => wanted[d.DirectoryInfo.Name]).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int from = dirs.IndexOf(ordered[i]);
                if (from != slots[i]) { dirs.Move(from, slots[i]); any = true; }
            }
            if (any && mine.Count > 1 && changed.Count == 0) changed.Add("reordered mods to the host's load order");

            if (any)
            {
                fm.SaveDirectories(dirs);
                Plugin.Log.LogInfo("[ModSync] Mod list changed and saved: " + string.Join("; ", changed));
                // Read it back: if something (the game, Steam, a mod loader) puts a mod
                // back, say so instead of silently looping on restarts.
                var after = fm.GetSearchDirectories();
                var wrong = new List<string>();
                if (after != null)
                    foreach (var d in after)
                    {
                        if (d == null || !d.IsSettingsMod || d.DirectoryInfo == null) continue;
                        bool shouldBeOn = wanted.ContainsKey(d.DirectoryInfo.Name);
                        if (!shouldBeOn && !Plugin.Instance.CfgModSyncDisableExtras.Value) continue;
                        if (d.IsChecked != shouldBeOn) wrong.Add($"{d.Name} [{d.DirectoryInfo.Name}] is {(d.IsChecked ? "on" : "off")}");
                    }
                if (wrong.Count > 0)
                    Plugin.Log.LogWarning("[ModSync] After saving, these still don't match: " + string.Join("; ", wrong));
            }
            else
            {
                Plugin.Log.LogInfo("[ModSync] Mod list already matches the host's ticks and order.");
            }
            return any;
        }

        private static void GuestTick()
        {
            if (!_requested)
            {
                _requested = true;
                Plugin.Log.LogInfo("[ModSync] Connected - asking the host for its mod list.");
                NetworkManager.Instance.SendToServer(new ModSyncReportMessage { Summary = RequestText });
            }
            if (_waitingFor.Count == 0 || _incoming == null) return;
            foreach (var id in _waitingFor.ToList())
            {
                var st = (EItemState)SteamUGC.GetItemState(new PublishedFileId_t(id));
                bool done = st.HasFlag(EItemState.k_EItemStateInstalled)
                            && !st.HasFlag(EItemState.k_EItemStateDownloading)
                            && !st.HasFlag(EItemState.k_EItemStateDownloadPending)
                            && !st.HasFlag(EItemState.k_EItemStateNeedsUpdate);
                if (done) _waitingFor.Remove(id);
            }
            bool timedOut = Time.unscaledTime - _waitStarted > 600f;
            if (_waitingFor.Count > 0 && !timedOut) return;

            // New folders: let the game see them, then tick them.
            if (Singleton<FileManager>.InstanceExists(false)) Singleton<FileManager>.Instance.RefreshSearchDirectories();
            MatchChecks(_incoming, out var changed);
            var notes = new List<string>(changed);
            if (timedOut) notes.Add($"{_waitingFor.Count} download(s) didn't finish in 10 minutes - check Steam's downloads");
            _waitingFor.Clear();
            FinishMessage(notes, false);
        }

        private static void FinishMessage(List<string> notes, bool onlyMissing)
        {
            if (!onlyMissing) RestartNeeded = true;
            string detail = notes.Count > 0 ? string.Join("; ", notes) + ". " : "";
            if (onlyMissing)
                SimSyncManager.ReportIssue("Some of the host's mods aren't on the Workshop.", detail);
            else
                SimSyncManager.ReportIssue("Your mods now match the host's - restart Sea Power.",
                    detail + "Mods only load when the game starts: quit, start Sea Power again and rejoin.");
        }

        public static void Reset()
        {
            _sentTo.Clear();
            _incoming = null;
            _received = 0;
            _requested = false;
            _downloadTotal = 0;
            // RestartNeeded is NOT reset: the mods ticked this session still aren't loaded
            // until the game restarts, however many times the guest reconnects.
            // Downloads already asked of Steam carry on regardless.
            _waitingFor.Clear();
        }
    }
}

namespace SeapowerMultiplayer.Messages
{
    /// <summary>Host → guest: one enabled mod (or, with Header, the count that follows).</summary>
    public class ModListEntryMessage : INetMessage
    {
        public bool   Header;
        public ushort Index;
        public ushort Total;
        public string Key = "";
        public string Name = "";
        public ulong  WorkshopId;
        public uint   Timestamp;

        public MessageType Type => MessageType.ModListEntry;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Header);
            w.Put(Index);
            w.Put(Total);
            w.Put(Key ?? "");
            w.Put(Name ?? "");
            w.Put(WorkshopId);
            w.Put(Timestamp);
        }

        public static ModListEntryMessage Deserialize(NetDataReader r) => new()
        {
            Header = r.GetBool(),
            Index = r.GetUShort(),
            Total = r.GetUShort(),
            Key = r.GetString(),
            Name = r.GetString(),
            WorkshopId = r.GetULong(),
            Timestamp = r.GetUInt(),
        };
    }

    /// <summary>Guest → host: what the guest's game changed to match (empty = already matched).</summary>
    public class ModSyncReportMessage : INetMessage
    {
        public string Summary = "";
        public MessageType Type => MessageType.ModSyncReport;
        public void Serialize(NetDataWriter w)
        {
            // Keep it under the reliable-packet limit.
            w.Put(Summary.Length > 700 ? Summary.Substring(0, 700) + "…" : Summary);
        }
        public static ModSyncReportMessage Deserialize(NetDataReader r) => new() { Summary = r.GetString() };
    }
}
