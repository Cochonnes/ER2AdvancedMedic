using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// World-space red-cross markers over nearby HEALING SOURCES — allied medic soldiers and medical
    /// tents — so a wounded player can find where to get treated. Only friendly (same-faction) medics
    /// and tents within MedicMarkMaxDist (default 100 m) are marked. The source list is rescanned on a
    /// short throttle and cached as plain world POSITIONS (never IL2CPP references held across frames,
    /// so a scene unload can't leave a freed pointer for the OnGUI draw to touch). Our own cross sprite
    /// (not a game asset) sidesteps the texture-mod asset-load crash.
    /// </summary>
    internal static class Marks
    {
        // Medic positions, refreshed on a throttle. Medics walk, so this is time-based.
        private static readonly List<Vector3> _medics = new List<Vector3>();
        private static float _nextMedicScan;

        // Tents come from the shared scene-lifetime cache in Tents (also used by the X-key tent heal).

        // Every soldier on the map, for the medic scan: added by the Soldier.Start hook (OnSpawn), dropped
        // on death (SoldierRegistry.Forget) or once found destroyed, and seeded ONCE per scene with a full
        // FindObjectsOfType (soldiers that were already there, or whose Start we missed). The scan used to
        // run FindObjectsOfType every second the whole time you needed help.
        // Scene: the active scene at spawn; entries of any other scene are skipped without being touched
        // (their soldiers may already be freed) and dropped by the scene flush.
        private struct Known { public Soldier S; public int Scene; }
        private static readonly Dictionary<IntPtr, Known> _known = new Dictionary<IntPtr, Known>();
        private static readonly List<IntPtr> _gone = new List<IntPtr>();
        private static bool _seeded;

        /// <summary>Drop the medic positions. Called from Effects.FlushSession (the tent cache is reset on the
        /// scene change itself, in Effects.Tick).</summary>
        public static void Reset()
        {
            _medics.Clear();
            _nextMedicScan = 0f;
        }

        /// <summary>Soldier.Start: remember the soldier for the medic scan.</summary>
        public static void OnSpawn(Soldier s)
        {
            var p = Interop.PtrOf(s);
            if (p == IntPtr.Zero) return;
            _known[p] = new Known { S = s, Scene = Interop.ActiveSceneHandle() };
        }

        /// <summary>A soldier died (SoldierRegistry): forget it.</summary>
        public static void ForgetSoldier(IntPtr p) { if (p != IntPtr.Zero) _known.Remove(p); }

        /// <summary>Scene flush: drop every soldier, or (keepScene non-zero) every soldier not of that scene -
        /// the new map's soldiers are already spawning when the flush runs. The next scan re-seeds.</summary>
        public static void ClearSoldiers(int keepScene)
        {
            _seeded = false;
            if (keepScene == 0) { _known.Clear(); return; }
            _gone.Clear();
            foreach (var kv in _known) if (kv.Value.Scene != keepScene) _gone.Add(kv.Key);
            foreach (var p in _gone) _known.Remove(p);
            _gone.Clear();
        }

        /// <summary>Once per scene: add every soldier already in it (and re-tag known ones with it).</summary>
        private static void Seed(int scene)
        {
            _seeded = true;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Soldier>();
                if (all == null) return;
                for (int i = 0; i < all.Length; i++)
                {
                    var s = all[i];
                    var p = Interop.PtrOf(s);
                    if (p != IntPtr.Zero) _known[p] = new Known { S = s, Scene = scene };
                }
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[marks] soldier list seeded: {_known.Count}");
            }
            catch (Exception e) { Guard.Once("marks seed", e); }
        }

        public static void Draw()
        {
            var c = Plugin.C;
            if (!c.EnableMedicMark.Value) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            var player = Interop.Player();
            if (player == null) { _medics.Clear(); return; }

            // Only guide you to help when you actually need it — hide the crosses while healthy.
            if (!PlayerNeedsHelp(player, c)) { _medics.Clear(); return; }

            var cam = PainFX.Cam();
            if (cam == null) return;

            Vector3 ppos;
            try { ppos = player.transform.position; } catch { return; }

            float range = Mathf.Max(1f, c.MedicMarkMaxDist.Value);
            float rangeSq = range * range;
            float now = Time.time;

            // Medics: a full scene sweep, so kept to 1 Hz. They are a navigation hint at up to 100 m,
            // not a crosshair — a second-old position is indistinguishable at that distance.
            if (now >= _nextMedicScan)
            {
                _nextMedicScan = now + MedicScanSeconds;
                RescanMedics(player, ppos, rangeSq);
            }

            // Tents: only worth looking for again once you have MOVED somewhere new (Tents throttles
            // on time AND distance travelled). Standing still costs nothing at all.
            Tents.ScanWide(ppos, range);

            float size = Mathf.Max(4f, c.MedicMarkSize.Value);
            for (int i = 0; i < _medics.Count; i++)
                DrawAt(cam, _medics[i], size);

            // The tent cache spans the whole scene, so range-filter it here at draw time.
            foreach (var t in Tents.All)
            {
                if ((t.Mark - ppos).sqrMagnitude > rangeSq) continue;
                DrawAt(cam, t.Mark, size * 1.5f);
            }
        }

        // Medics move, tents do not: medics are rescanned on a clock, tents only after moving.
        private const float MedicScanSeconds = 1.0f;

        private static void DrawAt(Camera cam, Vector3 world, float size)
        {
            Vector3 sp;
            try { sp = cam.WorldToScreenPoint(world); } catch { return; }
            if (sp.z <= 0f) return;   // behind the camera
            DrawCross(sp.x, Screen.height - sp.y, size);
        }

        /// <summary>Refresh the allied-medic positions within range of the player.</summary>
        private static void RescanMedics(Soldier player, Vector3 ppos, float rangeSq)
        {
            _medics.Clear();
            var playerPtr = Interop.PlayerPtr();
            int scene = Interop.TickScene;
            if (!_seeded) Seed(scene);

            _gone.Clear();
            try
            {
                // The player's faction is a marshalled string, so it is read ONCE here rather than
                // per candidate inside IsAlliedMedic.
                string pf = null; try { pf = player.faction; } catch { }
                if (string.IsNullOrEmpty(pf)) return;

                foreach (var kv in _known)
                {
                    if (kv.Value.Scene != scene) continue;   // another scene's soldier: never touched
                    var s = kv.Value.S;
                    if (s == null) { _gone.Add(kv.Key); continue; }   // destroyed without a death hook
                    if (kv.Key == playerPtr) continue;

                    // DISTANCE FIRST. This used to run the full medic test (IsAlive + IsMedic +
                    // hasSyringe + a marshalled faction string — four native calls and an
                    // allocation) on every soldier on the map before finding out they were 600 m
                    // away. One transform read rejects almost all of them for a fraction of that.
                    Vector3 hp;
                    try { hp = s.transform.position; } catch { continue; }
                    if ((hp - ppos).sqrMagnitude > rangeSq) continue;

                    if (!IsAlliedMedic(s, pf)) continue;
                    _medics.Add(hp + Vector3.up * 2.0f);
                }
            }
            catch { }
            // Pruned after the loop (removing inside it would abort the enumeration).
            foreach (var p in _gone) _known.Remove(p);
            _gone.Clear();
        }

        /// <summary>True only when the LOCAL player is HURT enough to want a medic — exactly these states:
        /// needs bandaging / bleeding (an active bleeding wound, or the vanilla bleeding flag), has shrapnel,
        /// STRONG (High) pain, or blood below the threshold (default &lt;70%). Blood at/above 70% and a merely
        /// non-full HP bar do NOT count. While none apply, the markers stay hidden.</summary>
        private static bool PlayerNeedsHelp(Soldier player, Cfg c)
        {
            if (Interop.IsBleeding(player)) return true;   // bleeding

            var w = WoundState.Peek(player);
            if (w == null) return false;

            // Needs bandaging / bleeding / has shrapnel — any active wound of those types.
            foreach (var kv in w.Parts)
            {
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    var wd = list[i];
                    if (!wd.Active) continue;
                    if (wd.Type == WoundType.Bleeding || wd.Type == WoundType.Shrapnel) return true;
                }
            }

            if (w.Pain == PainTier.High) return true;              // strong pain only

            // Needs blood — below the threshold (default 70%). At/above it, no marker.
            if (w.VitalsInit && w.Blood < c.MedicMarkBloodThreshold.Value) return true;

            return false;
        }

        private static bool IsAlliedMedic(Soldier s, string playerFaction)
        {
            try
            {
                if (Interop.IsDead(s) || Interop.IsDowned(s)) return false;   // a downed medic heals nobody
                if (!Interop.IsMedic(s)) return false;

                string sf = null; try { sf = s.faction; } catch { }
                if (string.IsNullOrEmpty(playerFaction) || string.IsNullOrEmpty(sf)) return false;
                if (sf == playerFaction) return true;
                try { return ResourcesManager.IsSameFaction(playerFaction, sf); } catch { return false; }
            }
            catch { return false; }
        }

        private static void DrawCross(float cx, float cy, float size)
        {
            float bar = Mathf.Max(2f, size * 0.30f);
            var red  = new Color(0.90f, 0.16f, 0.16f, 0.95f);
            var edge = new Color(0f, 0f, 0f, 0.55f);
            float h = size * 0.5f, b = bar * 0.5f;

            // dark backing, then the red cross on top
            Gui.Rect(new Rect(cx - h - 1, cy - b - 1, size + 2, bar + 2), edge);
            Gui.Rect(new Rect(cx - b - 1, cy - h - 1, bar + 2, size + 2), edge);
            Gui.Rect(new Rect(cx - h, cy - b, size, bar), red);
            Gui.Rect(new Rect(cx - b, cy - h, bar, size), red);
        }
    }
}
