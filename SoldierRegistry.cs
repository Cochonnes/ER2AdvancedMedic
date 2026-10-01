using System;

namespace AdvancedMedic
{
    /// <summary>
    /// The one owner of every per-soldier cache the mod keys on a native pointer. IL2CPP reuses
    /// pointers, so a leftover entry would attach to whoever spawns next at that address (a stale wound
    /// record, Lua wrapper or InventoryManager, no spawn kit, a pending heal or snapshot for a stranger).
    ///
    /// There used to be two hand-kept prune lists (the death hooks and the idle sweep) that had drifted
    /// apart. A new per-soldier cache gets added HERE, and both paths pick it up.
    /// </summary>
    internal static class SoldierRegistry
    {
        /// <summary>Drop everything keyed on this soldier's pointer.
        ///
        /// dead: the soldier is gone (a kill hook, or the vitals tick found a corpse). A LIVE soldier the
        /// idle sweep drops keeps its spawn-kit bookkeeping: the kit is a once-per-life thing keyed on the
        /// soldier, and dropping a still-pending top-up would cancel it.</summary>
        public static void Forget(IntPtr p, bool dead)
        {
            if (p == IntPtr.Zero) return;
            WoundState.Prune(p);
            Items.Prune(p);           // cached Lua_Soldier + InventoryManager
            Interop.PruneView(p);     // PhotonView + the human/AI verdict
            MpSync.Prune(p);          // an unsent snapshot
            Hooks.ForgetAiHeal(p);    // a pending AI bandage / revive
            MedicCall.Forget(p);      // a pending medic-bandage restore
            if (dead) { SpawnKit.Prune(p); Marks.ForgetSoldier(p); }   // (the marker list is every LIVE soldier)
        }

        /// <summary>Drop every per-soldier entry at once. Only on a real scene change / mission teardown:
        /// the soldiers are about to be (or have been) freed with the scene.
        ///
        /// keepScene (non-zero, a scene change): what the NEW scene's soldiers queued from their Start hook
        /// before the tick noticed the change - spawn kits, the marker list - is kept; everything else goes.</summary>
        public static void ClearAll(int keepScene = 0)
        {
            WoundState.Clear();
            Items.ClearAll();
            SpawnKit.Clear(keepScene);
            Marks.ClearSoldiers(keepScene);
            Interop.ClearViews();
            Hooks.ClearAiHeals();
            MpSync.ClearPending();
            MedicCall.ClearRestore();
        }
    }
}
