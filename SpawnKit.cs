using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Multiplies every soldier's bandages when they spawn — medics by MedicBandageMultiplier (3x),
    /// everyone else by BandageMultiplier (2x) — and gives a full aspirin stack to anyone carrying none.
    /// Hooked from Soldier.Start; the actual top-up is deferred a moment so the inventory is populated first.
    /// </summary>
    internal static class SpawnKit
    {
        private static readonly HashSet<IntPtr> _done = new HashSet<IntPtr>();
        private static readonly List<Pending> _pending = new List<Pending>();
        // Scene: the active scene when the soldier spawned. A new map's soldiers run Start BEFORE the tick
        // notices the scene change, and the flush that follows used to drop their kits with the old map's.
        private struct Pending { public Soldier S; public float Due, Deadline; public int Scene; }

        // How long to keep waiting for a soldier's inventory to turn up before giving up on it. Joining
        // an MP match fills the inventory from the network AFTER the soldier spawns, so the old single
        // attempt at +0.5 s often found nothing to multiply and the medic kept the default bandages.
        private const float FirstTry = 0.5f, RetryEvery = 0.5f, GiveUpAfter = 15f;

        public static void OnSpawn(Soldier s)
        {
            if (!Plugin.C.DoubleMedsOnSpawn.Value || s == null) return;
            var p = Interop.PtrOf(s);
            if (p == IntPtr.Zero || _done.Contains(p)) return;
            float now = Time.time;
            _pending.Add(new Pending { S = s, Due = now + FirstTry, Deadline = now + GiveUpAfter, Scene = Interop.ActiveSceneHandle() });
        }

        /// <summary>Drop queued/soldier state on a scene flush (pending refs point at soldiers the scene
        /// unload is about to free — Tick would otherwise touch freed pointers). keepScene (non-zero): the
        /// kits queued by soldiers of THAT scene - the new map, already spawning - stay queued.</summary>
        public static void Clear(int keepScene = 0)
        {
            _done.Clear();
            if (keepScene == 0) { _pending.Clear(); return; }
            // Decided on the tag alone: the dropped entries are never touched.
            _pending.RemoveAll(x => x.Scene != keepScene);
        }

        /// <summary>A soldier died: forget them. IL2CPP reuses pointers, so a stale "done" entry would
        /// deny the kit to whoever spawns next at the same address.</summary>
        public static void Prune(IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            _done.Remove(p);
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (Interop.PtrOf(_pending[i].S) == p) _pending.RemoveAt(i);
        }

        public static void Tick()
        {
            if (_pending.Count == 0) return;
            float now = Time.time;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now < _pending[i].Due) continue;
                var s = _pending[i].S;
                var p = Interop.PtrOf(s);
                if (p == IntPtr.Zero) { _pending.RemoveAt(i); continue; }

                // Nothing in the inventory yet? Come back shortly — up to GiveUpAfter, then run anyway so
                // a soldier who genuinely carries no bandages still gets their aspirin.
                bool empty = Items.CountOf(s, Plugin.C.IdBandage.Value) <= 0;
                if (empty && now < _pending[i].Deadline)
                {
                    var again = _pending[i];
                    again.Due = now + RetryEvery;
                    _pending[i] = again;
                    continue;
                }

                _pending.RemoveAt(i);
                if (!_done.Add(p)) continue;

                // Only the soldier's authority tops up its kit (SP: us; MP: the host for AI, each player
                // for their own avatar). Every machine sees every Soldier.Start, and each one adding its
                // own stacks multiplied the kit once per machine in the room. Asked at kit time, not at
                // Start, so the PhotonView's ownership has had time to arrive.
                if (!Interop.HasAuthority(s)) continue;

                if (Plugin.C.DebugLogging.Value && empty)
                    Plugin.L.LogInfo("[spawnkit] gave up waiting for an inventory after " + GiveUpAfter + "s");
                // Per soldier: one failing kit must not abort the loop for the soldiers still queued.
                try { Double(s); } catch (Exception e) { Guard.Once("spawnkit", e); }
            }
        }

        private static void Double(Soldier s)
        {
            var im = Items.Inv(s);
            var inv = Items.Container(s);
            if (im == null || inv == null) return;
            // The class flags can still be unset this early, but the kit is already there, so a
            // carried syringe counts too.
            bool medic = Interop.IsMedic(s, checkInventory: true);
            int mult = medic ? Plugin.C.MedicBandageMultiplier.Value : Plugin.C.BandageMultiplier.Value;
            if (Plugin.C.DebugLogging.Value)
                Plugin.L.LogInfo($"[spawnkit] {(medic ? "MEDIC" : "soldier")} (isMedic={Interop.IsClassMedic(s)} hasSyringe={Interop.HasSyringeFlag(s)} carriesSyringe={Items.CountOf(s, Plugin.C.IdSyringe.Value)}) -> bandages x{mult}");
            // Each step guarded HERE, at its call (JIT trap): MultiplyItem / EnsureStack reach inventory
            // and item-database members, and one of those vanishing fails that whole method when it is
            // compiled - a try inside it can't catch that. One step failing leaves the other working.
            try { MultiplyItem(im, inv, Plugin.C.IdBandage.Value, mult, medic); } catch (Exception e) { Guard.Once("spawnkit multiply", e); }
            try { EnsureStack(im, inv, Plugin.C.IdAspirin.Value, Plugin.C.AspirinFullStack.Value); } catch (Exception e) { Guard.Once("spawnkit ensure", e); }
        }

        /// <summary>If the soldier carries NONE of an item, add a single full stack of it (leaving
        /// soldiers who already have some untouched).</summary>
        private static void EnsureStack(InventoryManager im, Inventory inv, string id, int fullCount)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                var items = inv.items;
                if (items != null)
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i]; string iid = null; try { iid = it != null ? it.item_id : null; } catch { }
                        if (iid == id) return;   // already carries some → leave it
                    }

                ItemObject prefab = null;
                try { prefab = ItemsDatabase.GetItemObject(id); } catch { }
                if (prefab == null) return;

                var added = im.AddItemToInventory(prefab);
                if (added != null)
                {
                    try { SetStack(added, fullCount); } catch { }
                    if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[spawnkit] gave a full {id} stack ({fullCount}) — had none");
                }
            }
            catch (Exception e) { Guard.Once("spawnkit ensure", e); }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SetStack(VirtualItem added, int fullCount)
        {
            var stk = Interop.CastTo<VirtualItemStackable>(added);
            if (stk != null) stk.stackCount = new ProtectedInt(Mathf.Max(1, fullCount));
        }

        private static void MultiplyItem(InventoryManager im, Inventory inv, string id, int mult, bool medic)
        {
            if (string.IsNullOrEmpty(id) || mult <= 1) return;
            try
            {
                // AddItemToInventory adds a whole STACK, so a multiplier of N means adding (N-1) stacks
                // per existing stack (not per item — that tripled it).
                int stacks = 0;
                var items = inv.items;
                if (items != null)
                {
                    int n = items.Count;
                    for (int i = 0; i < n; i++)
                    {
                        var it = items[i];
                        string iid = null; try { iid = it != null ? it.item_id : null; } catch { }
                        if (iid == id) stacks++;
                    }
                }
                if (stacks <= 0) return;

                ItemObject prefab = null;
                try { prefab = ItemsDatabase.GetItemObject(id); } catch { }
                if (prefab == null) return;

                int want = stacks * (mult - 1);
                int added = 0;
                for (int i = 0; i < want; i++)
                    if (im.AddItemToInventory(prefab) != null) added++;

                if (added > 0 && Plugin.C.DebugLogging.Value)
                    Plugin.L.LogInfo($"[spawnkit] {(medic ? "medic" : "soldier")} {id} x{mult}: {stacks} stack(s) +{added}");
            }
            catch (Exception e) { Guard.Once("spawnkit multiply", e); }
        }
    }
}
