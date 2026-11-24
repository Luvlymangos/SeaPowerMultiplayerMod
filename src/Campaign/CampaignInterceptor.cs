using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SeapowerMultiplayer.Campaign
{
    /// <summary>
    /// Table-driven interception of a campaign mod's player-action methods.
    ///
    /// The campaign mod is a separate assembly with no API for us, so each player
    /// action ("move this fleet", "launch this operation") is a method we patch at
    /// runtime. Rather than one hand-written Harmony class per action, an adapter
    /// registers a <see cref="Binding"/> per method:
    ///   - Capture: turn the call's arguments into an adapter-neutral CampaignCommand
    ///     (object references become stable ids the host can look up);
    ///   - Execute: on the host, turn a CampaignCommand back into the real call.
    /// One shared prefix serves every binding and asks CampaignSync whether the
    /// original should run.
    ///
    /// Patches are applied with our own Harmony id and only when the method is
    /// found, so a campaign mod update that renames a method disables that one
    /// action (logged) instead of crashing the game.
    /// </summary>
    public static class CampaignInterceptor
    {
        public sealed class Binding
        {
            public string CommandId = "";
            public MethodBase Target = null!;
            /// <summary>(instance, args) → command. Return null to let this call
            /// through un-synced (e.g. a UI-only overload).</summary>
            public Func<object?, object?[], CampaignCommand?> Capture = null!;
            /// <summary>Host: perform the command. Return false + reason to refuse.</summary>
            public ExecuteFn Execute = null!;
            /// <summary>The guest ALSO runs the call locally (a prediction): for actions
            /// that hand back a new object the guest's UI needs at once (a prepared
            /// flight, a new task force). The host still makes the real one; the next
            /// update replaces the guest's copy (same id - the id counters are synced).</summary>
            public bool Predict;
        }

        public delegate bool ExecuteFn(CampaignCommand command, out string reason);

        private static readonly Dictionary<MethodBase, Binding> _byMethod = new();
        private static readonly Dictionary<string, Binding> _byCommand = new();
        private static Harmony? _harmony;

        private const string HarmonyId = PluginInfo.PLUGIN_GUID + ".campaign";

        /// <summary>Patch one campaign-mod method. Safe to call with a null target
        /// (the lookup failed): it logs and skips.</summary>
        public static bool Bind(Binding b)
        {
            if (b.Target == null)
            {
                Plugin.Log.LogWarning($"[Campaign] '{b.CommandId}': target method not found - action will NOT sync.");
                return false;
            }
            try
            {
                _harmony ??= new Harmony(HarmonyId);
                _harmony.Patch(b.Target, prefix: new HarmonyMethod(
                    typeof(CampaignInterceptor).GetMethod(nameof(SharedPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                _byMethod[b.Target] = b;
                _byCommand[b.CommandId] = b;
                Plugin.Log.LogInfo($"[Campaign] Bound '{b.CommandId}' -> {b.Target.DeclaringType?.FullName}.{b.Target.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Campaign] Failed to patch '{b.CommandId}': {ex}");
                return false;
            }
        }

        /// <summary>Host: dispatch a received command to its binding.</summary>
        public static bool TryExecute(CampaignCommand command, out string reason)
        {
            if (!_byCommand.TryGetValue(command.Id, out var b))
            {
                reason = $"Unknown campaign action '{command.Id}' (campaign mod versions differ?).";
                return false;
            }
            return b.Execute(command, out reason);
        }

        public static int BoundCount => _byCommand.Count;

        public static void UnbindAll()
        {
            _harmony?.UnpatchSelf();
            _byMethod.Clear();
            _byCommand.Clear();
        }

        // Shared by every binding. Harmony injects the original method so one
        // prefix can serve many targets.
        private static bool SharedPrefix(MethodBase __originalMethod, object? __instance, object?[] __args)
        {
            if (CampaignSync.ApplyingFromNetwork || !CampaignSync.IsActive) return true;
            if (!_byMethod.TryGetValue(__originalMethod, out var b)) return true;

            CampaignCommand? cmd;
            try { cmd = b.Capture(__instance, __args); }
            catch (Exception ex)
            {
                // Fail OPEN on the host (its campaign is the real one), CLOSED on the
                // guest (running it locally would fork the two campaigns).
                Plugin.Log.LogError($"[Campaign] Capture failed for '{b.CommandId}': {ex}");
                return Plugin.Instance.CfgIsHost.Value;
            }
            if (cmd == null) return true;
            if (string.IsNullOrEmpty(cmd.Id)) cmd.Id = b.CommandId;

            bool runLocally = CampaignSync.InterceptLocal(cmd);
            return runLocally || (b.Predict && !Plugin.Instance.CfgIsHost.Value);
        }

        // ── Reflection helpers for adapters ───────────────────────────────────

        public static Type? FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, throwOnError: false);
                    if (t != null) return t;
                }
                catch { /* a half-loaded or dynamic assembly: skip it */ }
            }
            return null;
        }

        public static MethodInfo? FindMethod(Type? type, string name, params Type[]? argTypes)
        {
            if (type == null) return null;
            return argTypes == null || argTypes.Length == 0
                ? AccessTools.Method(type, name)
                : AccessTools.Method(type, name, argTypes);
        }
    }
}
