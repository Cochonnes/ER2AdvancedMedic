using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AdvancedMedic
{
    /// <summary>
    /// The vanilla life channel, used by the soldier's OWNER only.
    ///
    /// Vanilla facts (disassembled 2026-09-27, see DESIGN s15): life travels in exactly one RPC.
    /// `SyncSoldier.TrySyncDamage(bool)` sends `UpdateSoldierLife(life_total, anim)` to Others, with no
    /// ownership check; the receiver OVERWRITES its copy's life, and 0 -> &gt;0 revives, &gt;0 -> 0 downs,
    /// a drop of more than 25 turns bleeding on, a negative value kills. `Soldier.Damage` always ends in
    /// that call - but `Soldier.SetIncapacitated` (life 0 / 25) and a direct `life_total` write never
    /// sync. So after each of those, the owner pushes its own value here, and every other machine
    /// (modded or not) mirrors the down / wake / heal instead of keeping a stale life that its next hit
    /// would push back.
    ///
    /// Guards:
    /// - Owner only (Interop.HasAuthority). A non-owner never pushes a life value from here, so this can
    ///   never send the owner something lower than its own current value (which would turn its bleeding on).
    /// - Never a positive value while the soldier is downed (that would read as a revive everywhere),
    ///   unless the caller says this IS the deliberate revive/wake.
    /// - Never a negative value: a kill goes through KillSynched, which syncs itself.
    /// </summary>
    internal static class NetLife
    {
        /// <summary>Owner: broadcast this soldier's current life through the vanilla RPC. No-op in single
        /// player and on a machine that doesn't own the soldier.</summary>
        public static void SyncOwnLife(Soldier s, string why, bool revive = false)
        {
            if (s == null || !MpSync.Active) return;
            bool log = Plugin.C.DebugLogging.Value;
            if (!Interop.HasAuthority(s))
            {
                if (log) Plugin.L.LogInfo($"[mp] life sync ({why}) skipped: not the owner");
                return;
            }
            int life = ReadLife(s);
            if (life < 0) return;   // dead (or unreadable): the kill path syncs itself
            if (life > 0 && Interop.IsDowned(s) && !revive)
            {
                if (log) Plugin.L.LogWarning($"[mp] life sync ({why}) refused: positive life {life} on a downed soldier");
                return;
            }
            try
            {
                TrySyncRaw(s);
                if (log) Plugin.L.LogInfo($"[mp] owner life sync ({why}): UpdateSoldierLife({life}) -> others");
            }
            catch (Exception e) { Guard.Once("mp life sync", e); }
        }

        private static int ReadLife(Soldier s)
        {
            try { return LifeRaw(s); } catch { return -1; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static int LifeRaw(Soldier s) => s.life_total;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TrySyncRaw(Soldier s)
        {
            var sy = s.GetSyncher();
            if (sy != null) sy.TrySyncDamage(false);
        }

        /// <summary>Third-person syringe clip on the other machines: the vanilla "SyncSyringe" RPC, whose
        /// receiver (SyncSoldier.SyncSyringe, disassembled) only does
        /// PlaySyringeAnimationTPS(ItemsDatabase.GetItemObject(id)) - no life, no revive, no item change.
        /// Only call it with an id the medic really carries: an unknown id makes every receiver log
        /// "Prop ID not found" (and can crash with the asset-streaming mod).</summary>
        public static void SendSyringeTps(Soldier medic, string itemId)
        {
            if (medic == null || string.IsNullOrEmpty(itemId) || !MpSync.Active) return;
            try
            {
                RpcSyncSyringe(medic, itemId);
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] SyncSyringe('{itemId}') -> others (third-person clip)");
            }
            catch (Exception e) { Guard.Once("mp SyncSyringe", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RpcSyncSyringe(Soldier medic, string itemId)
        {
            var sy = medic.GetSyncher();   // SyncSoldier : Syncher : PhotonView - the view the game RPCs on
            if (sy == null) return;
            var args = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
            args[0] = new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(itemId));
            sy.RPC("SyncSyringe", Photon.Pun.RpcTarget.Others, args);
        }
    }
}
