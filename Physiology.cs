using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Medical physiology. Every vital is
    /// derived from the others, so the body behaves as one system:
    ///
    ///   cardiac output (L/s) = 0.095 L stroke x HR/60 x (blood 50%→100% mapped to 0→1)
    ///   blood pressure       = output x peripheral resistance  (x9.47 systolic, x6.32 diastolic → 120/80 at rest)
    ///   blood loss (L/s)     = wound bleeding x max(output, 0.05) x (100/resistance) x BleedingCoefficient
    ///
    /// So bleeding slows as the heart weakens, the pulse races to hold pressure as blood drops, and a
    /// drug that slows the heart also slows the bleeding. Blood is kept as a 0..1 fraction of 6 L.
    /// </summary>
    internal static class Physiology
    {
        public const float BloodLitres = 6f;
        private const float StrokeVolume = 0.095f;   // L per beat
        private const float MinCardiacOutput = 0.05f; // floor for the bleed calculation
        private const float BpHighMod = 9.4736842f;   // output x resistance -> systolic
        private const float BpLowMod = 6.3157894f;    // output x resistance -> diastolic
        public const float DefaultResistance = 100f;
        private const float StablePulseMin = 40f;     // a heart slower than this isn't "stable"

        // Hemorrhage class boundaries used by CPR odds and the medic/tent "anything left to treat"
        // test, as fractions of 6 L (class II below 85%, class IV below 60%).
        public const float Class2 = 0.85f, Class4 = 0.60f;

        /// <summary>Cardiac output in litres per second.</summary>
        public static float CardiacOutput(SoldierWounds w)
        {
            float entering = Mathf.InverseLerp(0.5f, 1f, w.Blood);   // clamped 0..1
            return Mathf.Max(0f, entering * StrokeVolume * w.Pulse / 60f);
        }

        public static void BloodPressure(SoldierWounds w, out float sys, out float dia)
        {
            float p = CardiacOutput(w) * w.Resistance;
            sys = p * BpHighMod;
            dia = p * BpLowMod;
        }

        /// <summary>The open wounds' bleeding, capped per region (head 0.9,
        /// chest 1.0, each arm 0.3, each leg 0.5), limb bleeding scaled down while the core bleeds, and
        /// a tourniqueted limb contributing nothing.</summary>
        public static float WoundBleeding(SoldierWounds w)
        {
            float head = 0, body = 0, al = 0, ar = 0, ll = 0, lr = 0;
            foreach (var kv in w.Parts)
            {
                if (w.Tourniquets.ContainsKey(kv.Key)) continue;
                float sum = 0f;
                foreach (var wd in kv.Value) if (wd.Bleeding) sum += wd.BleedRate;
                if (sum <= 0f) continue;
                switch (kv.Key)
                {
                    case BodyPartType.head:  head += sum; break;
                    case BodyPartType.arm_l: al += sum; break;
                    case BodyPartType.arm_r: ar += sum; break;
                    case BodyPartType.leg_l: ll += sum; break;
                    case BodyPartType.leg_r: lr += sum; break;
                    default:                 body += sum; break;
                }
            }
            float core = Mathf.Min(Mathf.Min(head, 0.9f) + Mathf.Min(body, 1f), 1f);
            float limbs = Mathf.Min(Mathf.Min(al, 0.3f) + Mathf.Min(ar, 0.3f) + Mathf.Min(ll, 0.5f) + Mathf.Min(lr, 0.5f), 1f);
            return core + limbs * (1f - core);
        }

        /// <summary>Per-part bleeding (for colouring the body diagram), tourniquet-aware.</summary>
        public static float PartBleeding(SoldierWounds w, BodyPartType part)
        {
            if (w == null || w.Tourniquets.ContainsKey(part) || !w.Parts.TryGetValue(part, out var list)) return 0f;
            float sum = 0f;
            foreach (var wd in list) if (wd.Bleeding) sum += wd.BleedRate;
            return sum;
        }

        /// <summary>Blood lost per second, in litres. <paramref name="bleeding"/> = WoundBleeding(w), summed by
        /// the caller: it walks every wound, and the vitals step sums it once and reuses it.</summary>
        public static float BloodLossLitres(SoldierWounds w, float bleeding)
        {
            if (bleeding <= 0f) return 0f;
            float co = Mathf.Max(CardiacOutput(w), MinCardiacOutput);
            return bleeding * co * (DefaultResistance / Mathf.Max(1f, w.Resistance)) * Mathf.Max(0f, Plugin.C.BleedingCoefficient.Value);
        }

        /// <summary>Stable vitals — what a downed soldier needs before they can wake up. Blood
        /// threshold softened by config (StableBlood); the diastolic check is dropped.</summary>
        public static bool Stable(SoldierWounds w) => Stable(w, WoundBleeding(w));

        /// <summary>Stable, with the wound bleeding already summed.</summary>
        public static bool Stable(SoldierWounds w, float bleeding)
        {
            var c = Plugin.C;
            if (w.Arrested) return false;
            if (w.Blood < c.StableBlood.Value) return false;
            float co = CardiacOutput(w);
            if (BloodLossLitres(w, bleeding) > 0.5f * co / 2f) return false;   // loss above a quarter of the cardiac output
            if (w.BloodPressure < c.StableSystolic.Value) return false;
            if (w.Pulse < StablePulseMin) return false;
            return true;
        }

        // ================================================================================== wounds

        private struct KindInfo
        {
            public float Bleed, Pain; public bool Fracture;
            public KindInfo(float b, float p, bool frac = false) { Bleed = b; Pain = p; Fracture = frac; }
        }

        // Wound classes: bleed, pain, fracture. (Limping is decided per leg in SoldierWounds.Limping:
        // any unstitched wound or fracture, whatever its kind.)
        private static KindInfo Info(WoundKind k)
        {
            switch (k)
            {
                case WoundKind.Abrasion:   return new KindInfo(0.001f, 0.4f);
                case WoundKind.Avulsion:   return new KindInfo(0.1f, 1.0f);
                case WoundKind.Contusion:  return new KindInfo(0f, 0.3f);
                case WoundKind.Crush:      return new KindInfo(0.05f, 0.8f, frac: true);
                case WoundKind.Cut:        return new KindInfo(0.01f, 0.1f);
                case WoundKind.Laceration: return new KindInfo(0.05f, 0.2f);
                case WoundKind.Velocity:   return new KindInfo(0.2f, 0.9f, frac: true);
                case WoundKind.Puncture:   return new KindInfo(0.05f, 0.4f);
                case WoundKind.Burn:       return new KindInfo(0f, 0.7f);
                default:                   return new KindInfo(0.05f, 0.4f);
            }
        }

        private struct Weighted
        {
            public WoundKind Kind; public float[] Pts; public float Size, Pain;
            public Weighted(WoundKind k, float[] pts, float size = 1f, float pain = 1f) { Kind = k; Pts = pts; Size = size; Pain = pain; }
        }

        private class DamageTable
        {
            public float[] Thresholds; public bool Specific; public Weighted[] Kinds;
        }

        // Points are (x, y) pairs flattened, x descending.
        private static readonly DamageTable Bullet = new DamageTable
        {
            Thresholds = new float[] { 20, 10, 4.5f, 2, 3, 1, 0, 1 }, Specific = true,
            Kinds = new[]
            {
                new Weighted(WoundKind.Avulsion,  new float[] { 1, 1, 0.35f, 0 }),
                // Unlike the reference table (armour stops weak rounds → contusion), ER2 has no body
                // armour: any bullet that does real damage breaks the skin and bleeds. Only a graze
                // (< 0.1, ~5 damage) is a contusion.
                new Weighted(WoundKind.Contusion, new float[] { 0.1f, 0, 0.1f, 1 }, size: 3.2f, pain: 2.2f),
                new Weighted(WoundKind.Velocity,  new float[] { 1.5f, 0, 1.5f, 1, 0.1f, 1, 0.1f, 0 }, size: 0.9f),
            }
        };

        private static readonly DamageTable Grenade = new DamageTable
        {
            Thresholds = new float[] { 20, 10, 10, 5, 4, 3, 1.5f, 2, 0.8f, 2, 0.3f, 1, 0, 0 }, Specific = false,
            Kinds = new[]
            {
                new Weighted(WoundKind.Avulsion,  new float[] { 1.5f, 1, 1.1f, 0 }),
                new Weighted(WoundKind.Velocity,  new float[] { 1.5f, 0, 1.1f, 1, 0.7f, 0 }),
                new Weighted(WoundKind.Puncture,  new float[] { 0.9f, 0, 0.7f, 1, 0.35f, 0 }),
                new Weighted(WoundKind.Cut,       new float[] { 0.7f, 0, 0.35f, 1, 0.35f, 0 }),
                new Weighted(WoundKind.Contusion, new float[] { 0.5f, 0, 0.35f, 1 }, size: 2f, pain: 0.9f),
            }
        };

        private static readonly DamageTable Collision = new DamageTable
        {
            Thresholds = new float[] { 8, 4, 1, 1, 0.3f, 1, 0.15f, 0.5f, 0, 0.3f }, Specific = false,
            Kinds = new[]
            {
                new Weighted(WoundKind.Avulsion,   new float[] { 1, 2, 0.5f, 0.5f, 0.5f, 0 }),
                new Weighted(WoundKind.Abrasion,   new float[] { 0.4f, 0, 0.2f, 1, 0, 0 }),
                new Weighted(WoundKind.Contusion,  new float[] { 0.4f, 0, 0.2f, 1 }),
                new Weighted(WoundKind.Crush,      new float[] { 0.4f, 1, 0.2f, 0 }),
                new Weighted(WoundKind.Cut,        new float[] { 0.1f, 1, 0.1f, 0 }),
                new Weighted(WoundKind.Laceration, new float[] { 0, 1 }),
            }
        };

        private static readonly DamageTable Punch = new DamageTable
        {
            Thresholds = new float[] { 0.1f, 1, 0.1f, 0 }, Specific = true,
            Kinds = new[]
            {
                new Weighted(WoundKind.Contusion,  new float[] { 0.35f, 0, 0.35f, 1 }),
                new Weighted(WoundKind.Crush,      new float[] { 0.1f, 1, 0.1f, 0 }),
                new Weighted(WoundKind.Laceration, new float[] { 0, 1 }),
            }
        };

        // Fire: burns only - no bleeding (they are bruise-type wounds that heal by themselves), sized and
        // counted by the damage: one burn for a lick of flame, up to three spread over the body for a
        // heavy burst. Repeated fire ticks grow an existing burn (WoundLogic.GrowBurn) rather than
        // stacking new ones.
        private static readonly DamageTable BurnTable = new DamageTable
        {
            Thresholds = new float[] { 4, 3, 1.5f, 2, 0.5f, 1, 0, 1 }, Specific = false,
            Kinds = new[] { new Weighted(WoundKind.Burn, new float[] { 0, 1 }) }
        };

        /// <summary>Piecewise-linear lookup: points are (x,y) pairs with x descending.</summary>
        private static float Interp(float input, float[] pts)
        {
            int n = pts.Length / 2;
            if (n == 0) return 0f;
            if (n == 1) return pts[1];
            int lower = -1;
            for (int i = 0; i < n; i++) if (pts[i * 2] < input) { lower = i; break; }
            if (lower == 0) return pts[1];
            if (lower == -1) return pts[(n - 1) * 2 + 1];
            float lx = pts[lower * 2], ly = pts[lower * 2 + 1];
            float ux = pts[(lower - 1) * 2], uy = pts[(lower - 1) * 2 + 1];
            if (Mathf.Approximately(ux, lx)) return uy;
            return Mathf.Lerp(ly, uy, Mathf.InverseLerp(lx, ux, input));
        }

        /// <summary>One generated wound, before it is added to a soldier.</summary>
        public struct NewWound
        {
            public BodyPartType Part; public WoundKind Kind; public int Size;
            public float Bleed, Pain, Damage; public bool Fracture;
        }

        private static readonly List<NewWound> _buf = new List<NewWound>();
        private static readonly float[] _weights = new float[8];   // >= the most kinds in any table

        /// <summary>Continuous wound size 0.125..1 over wound-table damage 0.1..2.</summary>
        public static float SizeOf(float dmg) => Mathf.Lerp(0.125f, 1f, Mathf.InverseLerp(0.1f, 2f, dmg));

        /// <summary>Category from a size: 2 - floor(ln size / ln 0.5), clamped 0..2 (0 minor, 1 medium, 2 large).</summary>
        public static int Category(float size) => Mathf.Clamp(2 - Mathf.FloorToInt(Mathf.Log(size) / Mathf.Log(0.5f)), 0, 2);

        /// <summary>The category a wound of this much wound-table damage has.</summary>
        public static int SizeCategory(float dmg) => Category(SizeOf(dmg));

        /// <summary>Wounds for one hit: how many wounds, which kinds, how big, how much
        /// they bleed/hurt, and whether a bone breaks. <paramref name="tableDamage"/> is ER2 damage
        /// converted by WoundDamageScale. Non-specific damage (explosions, crashes) spreads its wounds over
        /// the struck part and its neighbours, since ER2 only reports one part.</summary>
        public static List<NewWound> Generate(float tableDamage, BodyPartType part, HitType hit)
        {
            _buf.Clear();
            var c = Plugin.C;
            DamageTable t;
            switch (hit)
            {
                case HitType.Explosion:        t = Grenade; break;
                case HitType.Fire:             t = BurnTable; break;
                case HitType.VehicleCollision: t = Collision; break;
                case HitType.Melee:            t = Punch; break;
                default:                       t = Bullet; break;
            }

            tableDamage = Mathf.Clamp(tableDamage, 0f, 20f);
            float nf = Interp(tableDamage, t.Thresholds);
            int n = Mathf.CeilToInt(nf - UnityEngine.Random.value);   // random round
            n = Mathf.Min(n, Mathf.Max(1, c.MaxWoundsPerHit.Value));
            if (n < 1) return _buf;

            float perWound = tableDamage / n;

            // Weight the kinds at this per-wound damage.
            float total = 0f;
            var weights = _weights;   // reused: this runs for every hit on every soldier
            int kinds = Mathf.Min(t.Kinds.Length, weights.Length);
            for (int i = 0; i < kinds; i++) { weights[i] = Mathf.Max(0f, Interp(perWound, t.Kinds[i].Pts)); total += weights[i]; }
            if (total <= 0f) return _buf;

            for (int k = 0; k < n; k++)
            {
                float r = UnityEngine.Random.value * total;
                int pick = 0;
                for (int i = 0; i < kinds; i++) { r -= weights[i]; if (r <= 0f) { pick = i; break; } }
                var wt = t.Kinds[pick];
                var info = Info(wt.Kind);

                float dmg = perWound * UnityEngine.Random.Range(0.9f, 1.1f);
                // Size 0.125..1 over damage 0.1..2 (x the kind's size multiplier).
                float size = SizeOf(dmg * wt.Size);
                float pain = size * wt.Pain * info.Pain;
                float bleed = size * info.Bleed;
                int cat = Category(size);

                var wpart = t.Specific ? part : SpreadPart(part);
                bool limb = wpart != BodyPartType.head && wpart != BodyPartType.chest && wpart != BodyPartType.auto_detect;

                _buf.Add(new NewWound
                {
                    Part = wpart, Kind = wt.Kind, Size = cat, Bleed = bleed, Pain = pain, Damage = dmg,
                    Fracture = info.Fracture && limb && dmg > 0.5f && UnityEngine.Random.value < c.FractureChance.Value,
                });
            }
            return _buf;
        }

        // Non-selection-specific damage: half the wounds land on the struck part, the rest anywhere.
        private static readonly BodyPartType[] AllParts =
            { BodyPartType.head, BodyPartType.chest, BodyPartType.arm_l, BodyPartType.arm_r, BodyPartType.leg_l, BodyPartType.leg_r };

        private static BodyPartType SpreadPart(BodyPartType struck)
        {
            if (UnityEngine.Random.value < 0.5f) return struck;
            return AllParts[UnityEngine.Random.Range(0, AllParts.Length)];
        }

        public static string SizeName(int size) => size >= 2 ? "Large" : size == 1 ? "Medium" : "Minor";

        /// <summary>"Medium laceration": size + kind, from a table built once (the wound card asked for it
        /// per wound on every GUI pass, which lower-cased the enum name and concatenated each time).</summary>
        public static string WoundName(int size, WoundKind kind)
        {
            int s = Mathf.Clamp(size, 0, 2), k = (int)kind;
            if (k < 0 || k >= KindCount) return SizeName(s) + " wound";
            return WoundNames[s * KindCount + k];
        }

        private static readonly int KindCount = Enum.GetValues(typeof(WoundKind)).Length;
        private static readonly string[] KindNames = BuildKindNames();
        private static readonly string[] WoundNames = BuildWoundNames();

        private static string[] BuildKindNames()
        {
            var names = new string[KindCount];
            for (int i = 0; i < KindCount; i++)
            {
                var k = (WoundKind)i;
                names[i] = k == WoundKind.Velocity ? "velocity wound"
                         : k == WoundKind.Puncture ? "puncture wound"
                         : k == WoundKind.Generic  ? "wound"
                         : k.ToString().ToLowerInvariant();
            }
            return names;
        }

        private static string[] BuildWoundNames()
        {
            var names = new string[3 * KindCount];
            for (int s = 0; s < 3; s++)
                for (int k = 0; k < KindCount; k++)
                    names[s * KindCount + k] = SizeName(s) + " " + KindNames[k];
            return names;
        }
    }
}
