using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Medical-tent detection, shared by the red-cross markers (Marks, up to 100 m) and the X-key tent
    /// heal (MedicCall, a few metres). A medical tent is a data-driven map prop with no typed component,
    /// so it is found by the TentHeal/NameToken1+2 match on nearby object names. The two used to carry
    /// their own copies; MedicCall's did an ALLOCATING OverlapSphere and three ToLowerInvariant per
    /// collider every 0.15 s whenever the player wasn't at full health.
    ///
    /// Tents are static, so everything found is kept for the life of the SCENE, keyed by a ~4 m grid
    /// cell (one tent spawns many colliders), with the bounds of every matching collider merged so a
    /// "near the tent" test measures to the tent's edge, not its pivot. Reset on a scene change
    /// (Effects.Tick's scene branch).
    ///
    /// The scan itself (2026-09-28): a 100 m sphere with no layer mask used to fill its 256-slot buffer
    /// with terrain pieces and soldier body-part colliders, so whether a tent made it into the buffer at
    /// all was luck. Now:
    ///  - the buffer GROWS (doubling up to OverlapCap) whenever a scan fills it, and that scan reruns;
    ///  - soldier body-part layers are masked out (learned from the local player's own BodyParts; never
    ///    layer 0 "Default", and never a layer a tent was found on);
    ///  - a collider already judged "not a tent" is remembered by instance id for the scene, so a rescan
    ///    only reads the names of colliders it hasn't seen before.
    /// </summary>
    internal static class Tents
    {
        internal struct Tent { public Vector3 Mark; public Bounds Box; }

        private static readonly Dictionary<long, Tent> _tents = new Dictionary<long, Tent>();

        private const int OverlapStart = 256, OverlapCap = 4096;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> _overlap;

        // Colliders already judged not to be part of a tent this scene (instance ids), bounded.
        private static readonly HashSet<int> _notTent = new HashSet<int>();
        private const int NotTentCap = 50000;

        // Layer mask: soldier body-part layers learned once per scene, minus Default and any tent layer.
        private static int _soldierLayers, _tentLayers;
        private static bool _layersLearned;

        // Wide scan (markers): only again after moving somewhere new.
        private static float _nextWide;
        private static Vector3 _lastWidePos;
        private static bool _wideDone;
        private const float WideScanSeconds = 5.0f;
        private const float WideRescanMoveSq = 15f * 15f;

        // Near scan (tent heal): a small sphere around the player, a couple of times a second at most.
        private static float _nextNear;
        private const float NearScanSeconds = 0.5f;

        public static void Reset()
        {
            _tents.Clear();
            _nextWide = 0f; _wideDone = false; _nextNear = 0f;
            _notTent.Clear();
            _soldierLayers = 0; _tentLayers = 0; _layersLearned = false;
        }

        /// <summary>Every tent found so far in this scene (range-filter at use). A foreach over it uses
        /// the collection's struct enumerator, so drawing the markers allocates nothing.</summary>
        internal static Dictionary<long, Tent>.ValueCollection All => _tents.Values;

        /// <summary>Wide, throttled scan for the markers: first call, then every 5 s after moving 15 m.</summary>
        public static void ScanWide(Vector3 pos, float range)
        {
            float now = Time.time;
            if (_wideDone && (now < _nextWide || (pos - _lastWidePos).sqrMagnitude <= WideRescanMoveSq)) return;
            _nextWide = now + WideScanSeconds;
            _lastWidePos = pos;
            _wideDone = true;
            Scan(pos, range);
        }

        /// <summary>Is the player within `range` metres of a medical tent's edge? Answers from the cache
        /// and, only when it has nothing close, looks around with a small throttled scan.</summary>
        public static bool Near(Vector3 pos, float range)
        {
            if (NearCached(pos, range)) return true;
            float now = Time.time;
            if (now < _nextNear) return false;
            _nextNear = now + NearScanSeconds;
            Scan(pos, range);
            return NearCached(pos, range);
        }

        private static bool NearCached(Vector3 pos, float range)
        {
            float r2 = range * range;
            foreach (var kv in _tents)
            {
                var b = kv.Value.Box;
                // Squared distance from the point to the box (0 inside it).
                var q = Vector3.Max(b.min, Vector3.Min(pos, b.max));
                if ((q - pos).sqrMagnitude <= r2) return true;
            }
            return false;
        }

        private static void Scan(Vector3 pos, float range)
        {
            try
            {
                if (!HaveTokens()) return;
                LearnSoldierLayers();

                if (_overlap == null)
                    _overlap = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>(OverlapStart);

                int mask = ~(_soldierLayers & ~_tentLayers & ~1);   // never mask Default (layer 0) or a tent layer
                int hits = Physics.OverlapSphereNonAlloc(pos, range, _overlap, mask, QueryTriggerInteraction.Collide);
                // A full buffer means colliders were dropped (maybe the tent): grow it and look again.
                while (hits >= _overlap.Length && _overlap.Length < OverlapCap)
                {
                    _overlap = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>(Mathf.Min(OverlapCap, _overlap.Length * 2));
                    hits = Physics.OverlapSphereNonAlloc(pos, range, _overlap, mask, QueryTriggerInteraction.Collide);
                }
                if (hits <= 0) return;
                if (hits > _overlap.Length) hits = _overlap.Length;
                if (_notTent.Count > NotTentCap) _notTent.Clear();

                for (int i = 0; i < hits; i++)
                {
                    var col = _overlap[i];
                    if (col == null) continue;
                    int id = col.GetInstanceID();
                    if (_notTent.Contains(id)) continue;   // judged on an earlier scan: no name reads
                    bool found = false;
                    var tr = col.transform;
                    for (int d = 0; d < 4 && tr != null; d++)   // collider + a few parents
                    {
                        if (NameMatches(tr.gameObject.name))
                        {
                            AddTent(col, tr.position);
                            found = true;
                            break;
                        }
                        tr = tr.parent;
                    }
                    if (!found) _notTent.Add(id);
                }
            }
            catch (System.Exception e) { Guard.Once("tent scan", e); }
        }

        private static void AddTent(Collider col, Vector3 tp)
        {
            long key = ((long)Mathf.RoundToInt(tp.x / 4f) << 20) ^ Mathf.RoundToInt(tp.z / 4f);
            Bounds box;
            try { box = col.bounds; } catch { box = new Bounds(tp, Vector3.one); }
            if (_tents.TryGetValue(key, out var t)) { t.Box.Encapsulate(box); _tents[key] = t; }
            else _tents[key] = new Tent { Mark = tp + Vector3.up * 2.5f, Box = box };
            try { _tentLayers |= 1 << col.gameObject.layer; } catch { }
        }

        /// <summary>Once per scene: the layers the local player's body-part colliders sit on. Every soldier
        /// shares them, and at 100 m they are most of what fills the overlap buffer.</summary>
        private static void LearnSoldierLayers()
        {
            if (_layersLearned) return;
            var player = Interop.Player();
            if (player == null) return;
            _layersLearned = true;
            try
            {
                var parts = player.GetComponentsInChildren<BodyPart>(true);
                if (parts == null) return;
                for (int i = 0; i < parts.Length; i++)
                {
                    var bp = parts[i];
                    if (bp == null) continue;
                    int layer = bp.gameObject.layer;
                    if (layer > 0 && layer < 32) _soldierLayers |= 1 << layer;
                }
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[tent] masking soldier layers 0x{_soldierLayers:X8} out of the tent scan");
            }
            catch { }
        }

        // Lower-cased tokens, recomputed only when the config strings change. Matched with
        // IndexOf(OrdinalIgnoreCase), so neither the tokens nor the object names are lower-cased per call.
        private static string _tokRaw1, _tokRaw2, _tokLow1, _tokLow2;

        private static bool HaveTokens()
        {
            SyncTokens();
            return _tokLow1 != null || _tokLow2 != null;
        }

        private static void SyncTokens()
        {
            var t1 = Plugin.C.TentToken1.Value; var t2 = Plugin.C.TentToken2.Value;
            bool changed = false;
            if (!ReferenceEquals(t1, _tokRaw1)) { _tokRaw1 = t1; _tokLow1 = string.IsNullOrEmpty(t1) ? null : t1.ToLowerInvariant(); changed = true; }
            if (!ReferenceEquals(t2, _tokRaw2)) { _tokRaw2 = t2; _tokLow2 = string.IsNullOrEmpty(t2) ? null : t2.ToLowerInvariant(); changed = true; }
            if (changed) _notTent.Clear();   // "not a tent" was judged against the old tokens
        }

        /// <summary>Both configured tokens (the non-blank ones) appear in the object name.</summary>
        public static bool NameMatches(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            SyncTokens();
            if (_tokLow1 == null && _tokLow2 == null) return false;
            if (_tokLow1 != null && name.IndexOf(_tokLow1, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (_tokLow2 != null && name.IndexOf(_tokLow2, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }
    }
}
