using System;
using System.Collections.Generic;

namespace AdvancedMedic
{
    /// <summary>
    /// Item counting / giving / consuming through the soldier's Inventory (Inventory.FindItemWithID,
    /// the item list and stack counts - the Lua countItems/removeItem calls silently do nothing in this
    /// context), plus a cached Lua_Soldier per soldier for getHealth/getName/healSoldier.
    /// </summary>
    internal static class Items
    {
        private static readonly Dictionary<IntPtr, Lua_Soldier> _lua = new Dictionary<IntPtr, Lua_Soldier>();

        public static Lua_Soldier Lua(Soldier s)
        {
            var p = Interop.PtrOf(s);
            if (p == IntPtr.Zero) return null;
            if (_lua.TryGetValue(p, out var l) && l != null) return l;
            try { l = new Lua_Soldier(s); } catch { l = null; }
            _lua[p] = l;
            return l;
        }

        // The InventoryManager is a component that lives as long as the soldier does, but every
        // Container/Bandages call used to re-resolve it — a native type lookup, plus a
        // GetComponentInChildren walk of the whole body hierarchy whenever the direct get missed.
        // Cached per pointer alongside _lua and dropped by the same Prune/ClearAll (SoldierRegistry).
        private static readonly Dictionary<IntPtr, InventoryManager> _inv = new Dictionary<IntPtr, InventoryManager>();

        // ---- typed bandage / syringe via the InventoryManager component ----
        /// <summary>The soldier's InventoryManager (registers items properly, unlike the raw list). Cached.</summary>
        internal static InventoryManager Inv(Soldier s)
        {
            var p = Interop.PtrOf(s);
            if (p == IntPtr.Zero) return null;
            InventoryManager cached;
            if (_inv.TryGetValue(p, out cached) && cached != null) return cached;

            InventoryManager found = null;
            try { found = s.GetComponent<InventoryManager>(); } catch { }
            if (found == null) { try { found = s.GetComponentInChildren<InventoryManager>(); } catch { } }
            if (found != null) _inv[p] = found;
            return found;
        }

        public static VirtualBandages Bandages(Soldier s)
        {
            var inv = Inv(s); if (inv == null) return null;
            try { return Interop.CastTo<VirtualBandages>(inv.FindItemOfType<VirtualBandages>()); } catch { return null; }
        }

        /// <summary>The carried VirtualBandages matching an id (or the first one).</summary>
        public static VirtualBandages BandagesById(Soldier s, string id)
        {
            var c = Container(s); if (c == null) return null;
            try { return Interop.CastTo<VirtualBandages>(c.FindItemWithID(id)); } catch { return null; }
        }

        /// <summary>The Inventory container (holds the VirtualItem list) for a soldier.</summary>
        public static Inventory Container(Soldier s)
        {
            var inv = Inv(s); if (inv == null) return null;
            try { return inv.inventory; } catch { return null; }
        }

        /// <summary>Consume exactly ONE of a (possibly stacked) item: decrement its stackCount, or
        /// remove the item when it's the last. Removing the whole VirtualItem drops a whole stack
        /// (e.g. all 4 aspirin), which is the bug this avoids.</summary>
        public static void Consume(Soldier s, string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var c = Container(s); if (c == null) return;
            try
            {
                var it = c.FindItemWithID(id);
                if (it == null) return;
                var stk = Interop.CastTo<VirtualItemStackable>(it);
                if (stk != null)
                {
                    int count = stk.stackCount;                 // ProtectedInt → int
                    if (count > 1) { stk.stackCount = new ProtectedInt(count - 1); return; }
                }
                c.RemoveVirtualItem(it);
            }
            catch { }
        }

        /// <summary>Stack-aware count of an item id the soldier carries (0 if none; 1 for a
        /// non-stackable present).</summary>
        public static int CountOf(Soldier s, string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            var c = Container(s); if (c == null) return 0;
            try
            {
                // ALL stacks, not just the first. FindItemWithID returns one item, so a soldier carrying
                // several bandage stacks (the spawn multiplier adds whole stacks) read as one stack only
                // — the panel showed "(x2)" while they actually had six.
                var items = c.items;
                if (items == null) return 0;
                int total = 0, n = items.Count;
                for (int i = 0; i < n; i++)
                {
                    var item = items[i];
                    if (item == null) continue;
                    string iid = null; try { iid = item.item_id; } catch { }
                    if (iid != id) continue;
                    var stk = Interop.CastTo<VirtualItemStackable>(item);
                    total += stk != null ? stk.stackCount : 1;
                }
                return total;
            }
            catch { return 0; }
        }

        /// <summary>Give back exactly ONE of an item: increment the existing stack, or add a fresh item
        /// if none is carried. The inverse of Consume — used to restore a unit an animation call spent.
        ///
        /// A fresh item's stack is set to 1 explicitly: VirtualItem.Create only allocates the object and
        /// writes its id, so a stackable (bandages) comes out with stackCount 0. That zero-count "bandage"
        /// is what Soldier.UseBandages then refused (CanUseStackable needs GetStackCount() > 0), so every
        /// animation-only bandage clip (tourniquet above all, the action you reach for when out of
        /// bandages) silently played nothing whenever the medic carried no bandage.</summary>
        public static void GiveOne(Soldier s, string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var c = Container(s); if (c == null) return;
            try
            {
                var it = c.FindItemWithID(id);
                var stk = it != null ? Interop.CastTo<VirtualItemStackable>(it) : null;
                if (stk != null) { stk.stackCount = new ProtectedInt(stk.stackCount + 1); return; }
                var fresh = VirtualItem.Create(id);
                if (fresh == null) return;
                var freshStk = Interop.CastTo<VirtualItemStackable>(fresh);
                if (freshStk != null) freshStk.stackCount = new ProtectedInt(1);
                c.AddVirtualItem(fresh);
            }
            catch { }
        }

        /// <summary>Give back what an animation clip spent: top the soldier's count of <paramref name="id"/>
        /// up to <paramref name="target"/>, one at a time, at most <paramref name="maxGive"/> units (a cap,
        /// so a broken count can never flood the inventory). The procedure's tool restore and the AI medic's
        /// bandage restore share it.</summary>
        public static void RestoreTo(Soldier s, string id, int target, int maxGive = 8)
        {
            if (s == null || string.IsNullOrEmpty(id)) return;
            for (int g = 0; g < maxGive && CountOf(s, id) < target; g++) GiveOne(s, id);
        }

        /// <summary>A stackable item's count as the game reads it (GetStackCount), or -1 if unreadable.
        /// Its own method: a game member.</summary>
        public static int StackOf(VirtualItemStackable it)
        {
            if (it == null) return -1;
            try { return GameStack(it); } catch { return -1; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int GameStack(VirtualItemStackable it) => it.GetStackCount();

        public static void Prune(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;
            _lua.Remove(ptr);
            _inv.Remove(ptr);
        }

        /// <summary>Drop all cached Lua_Soldier wrappers (scene change — pointers are about to free).</summary>
        public static void ClearAll() { _lua.Clear(); _inv.Clear(); _loggedCarried = false; }

        private static bool _loggedCarried;

        /// <summary>Log every carried item's id + type once, to discover real medic item ids.</summary>
        public static void LogCarried(Soldier s)
        {
            if (_loggedCarried || !Plugin.C.DebugLogging.Value) return;
            var inv = Inv(s);
            if (inv == null) { Plugin.L.LogInfo("[inv] no InventoryManager"); return; }
            _loggedCarried = true;
            try
            {
                var container = inv.inventory;
                var items = container != null ? container.items : null;
                if (items == null) { Plugin.L.LogInfo("[inv] inventory.items null"); return; }
                int n = items.Count;
                Plugin.L.LogInfo($"[inv] cur_items: {n} item(s)");
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var it = items[i];
                        if (it == null) continue;
                        string id = null; try { id = it.item_id; } catch { }
                        string ty = null; try { ty = it.GetIl2CppType().Name; } catch { }
                        Plugin.L.LogInfo($"[inv]   id='{id}'  type={ty}");
                    }
                    catch { }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[inv] enum failed: " + e.Message); }
        }
    }
}
