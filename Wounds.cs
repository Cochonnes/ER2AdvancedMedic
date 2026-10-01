using System;
using System.Collections.Generic;

namespace AdvancedMedic
{
    public enum WoundType { Bleeding, Fracture, Shrapnel, Bruise }
    public enum PainTier { None, Light, Medium, High }

    /// <summary>Wound classes. Drive a wound's bleed rate, pain, and
    /// whether it can break a bone. <see cref="WoundType"/> stays the treatment category.</summary>
    public enum WoundKind { Generic, Abrasion, Avulsion, Contusion, Crush, Cut, Laceration, Velocity, Puncture, Burn }

    /// <summary>A single wound on one body part.</summary>
    public class Wound
    {
        public WoundType Type;
        public WoundKind Kind;    // wound class (display + stats)
        public int Size = 1;      // 0 minor, 1 medium, 2 large (wound category)
        public float BleedRate;   // bleed units (0..~0.2 per wound); only counts while Bleeding
        public float Damage;      // wound-table damage behind it (a burn grows with the fire damage it keeps taking)
        public float Born;        // Time.time it was made here; a snapshot that doesn't know it yet must not erase it
        public bool Treated;      // fully closed (stitched) / healed — off the chart
        public bool Stabilised;   // bandaged but not the right tool (amber state); for a Fracture = SPLINTED
        public bool Bandaged;     // temporarily closed with a bandage (may reopen)
        public float ReopenAt;    // Time.time when a bandaged wound reopens (0 = n/a)
        public float HealAt;      // Time.time a self-healing wound (bruise/burn, splinted fracture) clears (0 = n/a)
        // 0..1: how infected a dressed-but-unstitched wound has become (Vitals.TickWoundTimers, on the
        // soldier's authority; synced as a 4-bit level). Adds pain and a little heart rate; stitching
        // closes the wound and so clears it. Never fatal by itself.
        public float Infection;

        public bool Active => !Treated;                 // still on the chart
        // Actively losing blood: an untreated, un-bandaged bleeding wound.
        public bool Bleeding => Type == WoundType.Bleeding && !Treated && !Bandaged;
    }

    /// <summary>One dose of a medication in the bloodstream .</summary>
    public class MedDose
    {
        public MedKind Kind;
        public float Added;       // Time.time given
        public float Peak;        // seconds until full effect
        public float Duration;    // seconds in the system
        public float Hr;          // heart-rate target adjustment at full effect
        public float Pain;        // pain suppression (0..100 scale) at full effect
        public float Res;         // peripheral-resistance adjustment at full effect
    }

    public enum MedKind { Painkiller, Epinephrine }

    /// <summary>Mod-side medical state of a soldier. ER2 can only SHOW alive / downed / dead, so
    /// Unconscious and CardiacArrest both look "downed" in-game; the overlay tells them apart.</summary>
    public enum MedState { Awake, Unconscious, CardiacArrest }

    /// <summary>All mod-side medical state for one soldier, keyed on native pointer.</summary>
    public class SoldierWounds
    {
        public readonly Dictionary<BodyPartType, List<Wound>> Parts = new Dictionary<BodyPartType, List<Wound>>();

        // Live reference to the soldier, refreshed on Get(), for tick-time effects (bleed drain,
        // re-bleed). Calls are guarded; pruned on Kill.
        public Soldier Owner;

        // Movement cache for the Move prefix (called for every tracked soldier every frame): downed flag
        // and the walk factors, refreshed at MoveCacheHz or when a wound/downed change invalidates it.
        public float MoveCacheAt;         // Time.time the cache goes stale (0 = stale now)
        public bool MoveDowned;
        public float MoveWalk = 1f, MoveSprint = 1f;
        // Every wound-set change calls this, so it also expires the pain floor cache (Pain.WoundFloor).
        public void InvalidateMove() { MoveCacheAt = 0f; PainFloorAt = 0f; }

        // Downed-state fuse: Time.time when the soldier entered the downed state (0 = not downed).
        public float DownedAt;
        // This down is the vitals model's own collapse (blood loss, heavy bleeding, arrest...), not a hit or
        // the game's. An MP human only gets the wake-up roll out of a down like that (Vitals: BleedOut).
        public bool CollapsedByVitals;

        // ---- vitals (see Physiology) ----
        public bool VitalsInit;
        public float Blood = 1f;         // 0..1 blood volume (1 = 6 L)
        public float Pulse = 80f;        // heart rate, bpm (0 = cardiac arrest)
        public float BloodPressure = 120f;   // systolic
        public float BpLow = 80f;            // diastolic
        public float Resistance = 100f;      // peripheral resistance (100 = normal; meds shift it)
        public bool Arrested = false;        // in cardiac arrest (pulse 0; needs CPR before ArrestTimeLeft runs out)
        public float ArrestTimeLeft;         // seconds left in cardiac arrest before death
        public float CprUntil;               // Time.time a CPR attempt is in progress until (halves the arrest clock, HR 25-35)
        public MedState State;               // our state; downed in ER2 = Unconscious or CardiacArrest
        public float IvRemaining = 0f;   // blood fraction still to infuse
        public int IvCount = 0;          // IVs currently attached (scales infusion speed + shown in UI)
        public float NextWakeCheck = 0f;     // Time.time of the next spontaneous wake-up roll
        public float NextHrCheck = 0f;       // Time.time of the next critical-heart-rate arrest roll
        public bool Overdosed;               // too many doses in the system; the vitals tick knocks the soldier out
        public float HighPainSince = -1f;    // Time.time high pain began (-1 = not in high pain); drives the pass-out
        public readonly System.Collections.Generic.List<MedDose> Meds = new System.Collections.Generic.List<MedDose>();

        // Tourniquets: body part -> Time.time applied. A tourniqueted limb doesn't bleed.
        public readonly Dictionary<BodyPartType, float> Tourniquets = new Dictionary<BodyPartType, float>();

        // ---- diagnosis snapshot (shown until re-diagnosed) ----
        public bool HasDiagnosis;
        public float DxBlood, DxPulse, DxBp, DxBpLow;
        public float DxTime;   // Time.time of that diagnosis — keeps the idle sweep from dropping it
        public PainTier DxPain;

        // Pain: PainValue is the REAL pain 0..100 (fades slowly); painkillers add a SUPPRESSION
        // that masks it while in the system. Everything that reacts to pain (tier, screen flash,
        // heart rate, pass-out) uses the PERCEIVED value, so pain comes back as a painkiller wears off.
        public PainTier Pain = PainTier.None;   // tier of the PERCEIVED pain
        public float PainValue;            // 0..100 real pain
        public float PainSuppress;         // 0..100 masked by medication right now
        public bool AspirinActive;         // a painkiller is speeding the real decline
        public float AspirinClearAt;
        // Wound floor: the pain the CURRENT wounds cause (sum by size x part, less when dressed). Real
        // pain never decays below it. Derived from Parts (so the idle sweep's ActiveCount covers it),
        // cached at 4 Hz and expired by InvalidateMove / Pain.Refresh.
        public float WoundPain;
        // The same floor WITHOUT infection pain: infection hurts (it is in WoundPain) but never knocks
        // anyone out or keeps them down (Pain.KnockoutTier).
        public float WoundPainNoInfection;
        public float PainFloorAt;          // Time.time the cached WoundPain goes stale
        public PainTier PainLogged;        // last tier the verbose [pain] line reported (log only)
        public float PerceivedPain => Math.Max(0f, PainValue - PainSuppress);

        // Knockdown pose lock (Time.time until which the knockdown pose is held)
        public float PoseLockUntil;
        public SoldierPose PoseLockPose;

        // Parts whose wound has been exposed with scissors (lets the forceps reach embedded shrapnel).
        public readonly HashSet<BodyPartType> Exposed = new HashSet<BodyPartType>();

        public List<Wound> Of(BodyPartType part)
        {
            if (!Parts.TryGetValue(part, out var list)) { list = new List<Wound>(); Parts[part] = list; }
            return list;
        }

        public int ActiveCount()
        {
            int n = 0;
            foreach (var kv in Parts) foreach (var w in kv.Value) if (w.Active) n++;
            return n;
        }

        // ---- per-part counts. One copy, used by the overlay, the hit peek, the procedure timer and the
        // treatments (each used to carry its own slightly different loop). ----

        /// <summary>Active (untreated) wounds on a part.</summary>
        public int CountActive(BodyPartType part)
        {
            if (!Parts.TryGetValue(part, out var list)) return 0;
            int n = 0; foreach (var x in list) if (x.Active) n++; return n;
        }

        /// <summary>Active wounds of one type on a part; includeBandaged:false skips dressed ones.</summary>
        public int CountActive(BodyPartType part, WoundType type, bool includeBandaged = true)
        {
            if (!Parts.TryGetValue(part, out var list)) return 0;
            int n = 0;
            foreach (var x in list) if (x.Active && x.Type == type && (includeBandaged || !x.Bandaged)) n++;
            return n;
        }

        /// <summary>Active, bandaged (dressed but not stitched) wounds on a part.</summary>
        public int CountBandaged(BodyPartType part)
        {
            if (!Parts.TryGetValue(part, out var list)) return 0;
            int n = 0; foreach (var x in list) if (x.Active && x.Bandaged) n++; return n;
        }

        /// <summary>Any bleeding wound not yet bandaged, tourniquet or not (what a bandage-all treats).</summary>
        public bool HasOpenBleed()
        {
            foreach (var kv in Parts)
                foreach (var x in kv.Value)
                    if (x.Active && x.Type == WoundType.Bleeding && !x.Bandaged) return true;
            return false;
        }

        /// <summary>Drop wounds that are fully closed/healed. Nothing reads them once Treated (every
        /// consumer checks Active), so they only made the per-part lists grow for the whole life.</summary>
        public void PruneTreated()
        {
            foreach (var kv in Parts) kv.Value.RemoveAll(IsTreated);
        }
        private static readonly Predicate<Wound> IsTreated = x => x.Treated;

        public bool AnyBleeding()
        {
            foreach (var kv in Parts)
            {
                if (Tourniquets.ContainsKey(kv.Key)) continue;   // a tourniquet stops the limb bleeding
                foreach (var w in kv.Value) if (w.Bleeding) return true;
            }
            return false;
        }

        /// <summary>Is a specific part actively losing blood (untreated, un-bandaged, no tourniquet)?</summary>
        public bool IsBleeding(BodyPartType part)
        {
            if (Tourniquets.ContainsKey(part)) return false;
            if (!Parts.TryGetValue(part, out var list)) return false;
            foreach (var w in list) if (w.Bleeding) return true;
            return false;
        }

        public bool LegBleeding() => IsBleeding(BodyPartType.leg_l) || IsBleeding(BodyPartType.leg_r);
        public bool ArmBleeding() => IsBleeding(BodyPartType.arm_l) || IsBleeding(BodyPartType.arm_r);
        public bool ChestHeadBleeding() => IsBleeding(BodyPartType.chest) || IsBleeding(BodyPartType.head);

        /// <summary>0 = no fracture on the part, 1 = broken, -1 = broken but splinted.</summary>
        public int FractureState(BodyPartType part)
        {
            if (!Parts.TryGetValue(part, out var list)) return 0;
            int st = 0;
            foreach (var w in list)
                if (w.Active && w.Type == WoundType.Fracture) { if (!w.Stabilised) return 1; st = -1; }
            return st;
        }

        /// <summary>A leg that still carries an UNSTITCHED wound (open or bandaged bleed, or a fracture):
        /// the soldier limps on it.</summary>
        public bool Limping(BodyPartType leg)
        {
            if (!Parts.TryGetValue(leg, out var list)) return false;
            foreach (var w in list)
                if (w.Active && (w.Type == WoundType.Bleeding || w.Type == WoundType.Fracture)) return true;
            return false;
        }

        public bool HasTourniquet(BodyPartType part) => Tourniquets.ContainsKey(part);
        public bool LegTourniquet() => HasTourniquet(BodyPartType.leg_l) || HasTourniquet(BodyPartType.leg_r);
        public bool ArmTourniquet() => HasTourniquet(BodyPartType.arm_l) || HasTourniquet(BodyPartType.arm_r);
    }

    /// <summary>Body-part helpers shared by the overlay, the hit peek and the damage rules.</summary>
    public static class Body
    {
        public static bool IsLimb(BodyPartType p) =>
            p == BodyPartType.arm_l || p == BodyPartType.arm_r || p == BodyPartType.leg_l || p == BodyPartType.leg_r;

        /// <summary>The body-diagram mask texture for each part (base name; "_s" selected, "_t" tourniquet).</summary>
        public static readonly (BodyPartType part, string mask)[] Masks =
        {
            (BodyPartType.head, "head"), (BodyPartType.chest, "torso"),
            (BodyPartType.arm_l, "arm_left"), (BodyPartType.arm_r, "arm_right"),
            (BodyPartType.leg_l, "leg_left"), (BodyPartType.leg_r, "leg_right"),
        };

        public static string Mask(BodyPartType p)
        {
            foreach (var (part, mask) in Masks) if (part == p) return mask;
            return "torso";
        }

        /// <summary>The tourniquet-band mask names ("_t"), parallel to <see cref="Masks"/>, built once.</summary>
        public static readonly string[] TourniquetMasks = BuildTq();

        private static string[] BuildTq()
        {
            var t = new string[Masks.Length];
            for (int i = 0; i < Masks.Length; i++) t[i] = Masks[i].mask + "_t";
            return t;
        }
    }

    /// <summary>Global wound store. Keyed on IL2CPP pointer; pruned on death.</summary>
    public static class WoundState
    {
        private static readonly Dictionary<IntPtr, SoldierWounds> _map = new Dictionary<IntPtr, SoldierWounds>();

        public static int TrackedCount => _map.Count;

        public static SoldierWounds Get(Soldier s)
        {
            var p = Interop.PtrOf(s);
            if (p == IntPtr.Zero) return null;
            if (!_map.TryGetValue(p, out var w)) { w = new SoldierWounds(); _map[p] = w; }
            w.Owner = s;
            return w;
        }

        public static SoldierWounds Peek(Soldier s) => Peek(Interop.PtrOf(s));

        /// <summary>Peek by a pointer the caller already has (the Move prefix reads it once per call).</summary>
        public static SoldierWounds Peek(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            _map.TryGetValue(p, out var w);
            return w;
        }

        /// <summary>Drop one soldier's record. Call SoldierRegistry.Forget instead, which drops every other
        /// per-soldier cache with it.</summary>
        public static void Prune(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero) _map.Remove(ptr);
        }

        /// <summary>Drop ALL tracked soldiers. Called on a scene change so the per-frame ticks
        /// never iterate pointers freed by the scene unload (that would native-crash).</summary>
        public static void Clear() => _map.Clear();

        /// <summary>The live map, for the tick passes. Returned as the concrete Dictionary so a
        /// foreach uses its struct enumerator: through IEnumerable it boxed one per pass, several
        /// passes a frame. Callers must not add/remove while iterating (defer, as Vitals does).</summary>
        public static Dictionary<IntPtr, SoldierWounds> All() => _map;
    }
}
