using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Pain. PainValue (0..100) is the REAL pain, and it has two sources:
    ///
    ///   * the WOUND FLOOR (<see cref="SoldierWounds.WoundPain"/>): recomputed from the wounds the soldier
    ///     carries right now - the SUM of every active wound's pain (by size, x body-part weight, less
    ///     once bandaged / splinted), x PainCoefficient, capped at WoundPainMax. Real pain never decays
    ///     below it, so open wounds keep hurting and four chest wounds hurt more than one.
    ///   * the HIT SPIKE: a hit raises pain to at least that hit's own pain (<see cref="Raise"/>, max not
    ///     sum), and a tourniquet left on too long raises it too. Anything above the floor fades.
    ///
    /// The part above the floor fades at max(PainDeclinePerSec, excess x PainReliefPerSec): linearly for
    /// a small excess, faster for a big one - so treating wounds (which lowers the floor) brings relief
    /// in about a minute instead of the old ~8 min crawl.
    ///
    /// Aspirin adds a SUPPRESSION through the medication model (Meds), which masks the pain while it's
    /// in the system and lets it come back as it wears off. The tier and every effect read the PERCEIVED
    /// pain (real minus suppression). Aspirin also still speeds the decline for a while (v2), which only
    /// ever reaches the floor: it masks wound pain, it doesn't cure it.
    ///
    /// Before 2026-09-27 there was no floor: pain was the single worst hit, fading to 0 in minutes
    /// however many wounds were still open ("4 chest wounds but close to no pain").
    ///
    /// An infected dressing adds Infection/PainAdded x its infection level to its wound's share. That
    /// share is FELT pain only: the knock-out rules read <see cref="KnockoutTier"/>, which leaves it out.
    /// </summary>
    internal static class Pain
    {
        // Band tops the old tier model used; the screen flash still interpolates against them.
        public const float LightVal = 33f, MedVal = 66f, HighVal = 85f;
        private const float HighAt = 67f, MedAt = 34f, LightAt = 1f;

        // How often each soldier's wound floor is recomputed (wound changes also invalidate it at once).
        // Just under the tick interval, so every tick recomputes it.
        private const float FloorSeconds = 0.2f;

        // The pain pass shares the vitals cadence: it does no game reads, but walking every tracked soldier
        // every frame for a value that changes by ~0.05 per frame was wasted work. Raise/Refresh still
        // update the tier at once when a hit or a treatment lands.
        private const float TickSeconds = 0.25f;
        private static float _acc;

        public static PainTier TierOf(float v) =>
            v >= HighAt ? PainTier.High : v >= MedAt ? PainTier.Medium : v >= LightAt ? PainTier.Light : PainTier.None;

        /// <summary>Raise pain to at least <paramref name="value"/> (0..100). Wound pain is scaled by
        /// PainCoefficient; pass scaled:false for pain that's already in final units.</summary>
        public static void Raise(SoldierWounds w, float value, bool scaled = true)
        {
            if (w == null || value <= 0f) return;
            if (scaled) value *= Mathf.Max(0f, Plugin.C.PainCoefficient.Value);
            w.PainValue = Mathf.Clamp(Mathf.Max(w.PainValue, value), 0f, 100f);
            w.Pain = TierOf(w.PerceivedPain);
        }

        /// <summary>Recompute the wound floor NOW and lift the real pain to it. Call after anything that
        /// changes the wound set where a stale floor would be wrong for even a frame - above all after
        /// clearing pain (a stale floor would put it straight back).</summary>
        public static void Refresh(SoldierWounds w)
        {
            if (w == null) return;
            w.WoundPain = WoundFloor(w, out w.WoundPainNoInfection);
            w.PainFloorAt = Time.time + FloorSeconds;
            if (w.PainValue < w.WoundPain) w.PainValue = w.WoundPain;
            w.Pain = TierOf(w.PerceivedPain);
        }

        /// <summary>Aspirin: a painkiller dose (masks pain, slightly slows the heart) plus the v2
        /// faster real decline for AspirinMin..Max seconds.</summary>
        public static void ApplyAspirin(SoldierWounds w)
        {
            if (w == null) return;
            var c = Plugin.C;
            Meds.Give(w, MedKind.Painkiller);
            if (w.PainValue > 0f)
            {
                w.AspirinActive = true;
                w.AspirinClearAt = Time.time + Random.Range(c.AspirinMin.Value, c.AspirinMax.Value);
            }
        }

        /// <summary>Hold real pain at the wound floor, fade what's above it (faster while aspirin is
        /// active), and keep the perceived tier in sync, for every tracked soldier.</summary>
        public static void TickAll()
        {
            _acc += Time.deltaTime;
            if (_acc < TickSeconds) return;
            float dt = Mathf.Min(_acc, 1f);
            _acc = 0f;
            var c = Plugin.C;
            float now = Time.time;
            float decline = Mathf.Max(0f, c.PainDeclinePerSec.Value);
            float relief = Mathf.Max(0f, c.PainReliefPerSec.Value);

            foreach (var kv in WoundState.All())
            {
                var w = kv.Value;
                if (now >= w.PainFloorAt)
                {
                    w.WoundPain = WoundFloor(w, out w.WoundPainNoInfection);
                    w.PainFloorAt = now + FloorSeconds;
                }

                if (w.PainValue <= 0f && w.WoundPain <= 0f)
                {
                    w.PainValue = 0f; w.Pain = PainTier.None; w.AspirinActive = false;
                    if (w.PainLogged != PainTier.None) LogTier(w);
                    continue;
                }

                float excess = w.PainValue - w.WoundPain;
                if (excess > 0f)
                {
                    // Untreated pain only crawls down (PainDeclinePerSec: High takes ~5 min to drop below
                    // High). The proportional relief is the painkiller's: without aspirin it made High pain
                    // Medium within a minute, which is far too fast.
                    float rate = decline;
                    if (w.AspirinActive)
                    {
                        if (now >= w.AspirinClearAt) w.AspirinActive = false;             // aspirin wore off
                        else rate = Mathf.Max(decline, excess * relief)
                                    * Mathf.Max(1f, c.AspirinDeclineMult.Value);          // faster while active
                    }
                    w.PainValue = Mathf.Max(w.WoundPain, w.PainValue - rate * dt);
                }
                else
                {
                    w.PainValue = w.WoundPain;   // open wounds keep hurting: never below the floor
                    if (w.AspirinActive && now >= w.AspirinClearAt) w.AspirinActive = false;
                }

                w.Pain = TierOf(w.PerceivedPain);
                if (w.Pain != w.PainLogged) LogTier(w);
            }
        }

        // ------------------------------------------------------------------ wound floor

        /// <summary>The pain the soldier's current wounds cause (0..WoundPainMax): the sum of each active
        /// wound's <see cref="WoundContribution"/>, x PainCoefficient. Pure data - no game calls.
        /// <paramref name="noInfection"/> is the same floor without any infection pain (what the knock-out
        /// and wake-up checks read, see <see cref="KnockoutTier"/>).</summary>
        public static float WoundFloor(SoldierWounds w, out float noInfection)
        {
            noInfection = 0f;
            var c = Plugin.C;
            float max = Mathf.Clamp(c.WoundPainMax.Value, 0f, 100f);
            if (max <= 0f || w.Parts.Count == 0) return 0f;
            float sum = 0f, inf = 0f;
            foreach (var kv in w.Parts)
            {
                var list = kv.Value;
                if (list.Count == 0) continue;
                float weight = PartWeight(kv.Key, c);
                for (int i = 0; i < list.Count; i++)
                {
                    var wd = list[i];
                    if (!wd.Active) continue;
                    sum += WoundContribution(wd, c) * weight;
                    inf += InfectionPain(wd, c) * weight;
                }
            }
            float coef = Mathf.Max(0f, c.PainCoefficient.Value);
            noInfection = Mathf.Min(max, Mathf.Max(0f, sum - inf) * coef);
            return Mathf.Min(max, sum * coef);
        }

        /// <summary>The perceived pain tier the KNOCK-OUT rules read (sustained High pain passes you out and
        /// keeps you down): the felt tier minus what infection adds. Infection hurts, and still shows in
        /// the flash / suppression / heart rate, but it never knocks anyone out (Infection/Enabled's
        /// promise) - three bandaged, fully infected chest wounds used to pass a soldier out and, since
        /// dressings never heal and infection only grows, keep them down for good.
        ///
        /// Pain above the wound floor is the fading hit spike, which is infection-free; at the floor, the
        /// floor without infection is used. (While the floor holds, the old spike's exact position under it
        /// isn't tracked, so this can read slightly low - it never reads high.)</summary>
        public static PainTier KnockoutTier(SoldierWounds w)
        {
            if (w == null) return PainTier.None;
            float real = w.PainValue > w.WoundPain + 0.01f ? w.PainValue : Mathf.Min(w.PainValue, w.WoundPainNoInfection);
            return TierOf(Mathf.Max(0f, real - w.PainSuppress));
        }

        /// <summary>One active wound's pain before the part weight and PainCoefficient: its size
        /// (Minor/Medium/Large) or the fracture value, reduced once dressed, plus its infection.</summary>
        public static float WoundContribution(Wound wd, Cfg c)
        {
            float p;
            if (wd.Type == WoundType.Fracture)
            {
                p = c.WoundPainFracture.Value;
                if (wd.Stabilised) p *= Mathf.Clamp01(c.SplintedPainFactor.Value);   // splinted
            }
            else
            {
                p = wd.Size >= 2 ? c.WoundPainLarge.Value : wd.Size == 1 ? c.WoundPainMedium.Value : c.WoundPainMinor.Value;
                // Non-bleeding bruises/abrasions hurt less; burns (also bleed-free) hurt fully.
                if (wd.Type == WoundType.Bruise && wd.Kind != WoundKind.Burn) p *= Mathf.Clamp01(c.BruisePainFactor.Value);
                if (wd.Bandaged) p *= Mathf.Clamp01(c.BandagedPainFactor.Value);
            }
            return Mathf.Max(0f, p) + InfectionPain(wd, c);
        }

        /// <summary>The extra pain an infected wound carries (Infection/PainAdded at full infection).</summary>
        public static float InfectionPain(Wound wd, Cfg c)
        {
            if (wd.Infection <= 0f || !c.InfectionEnabled.Value) return 0f;
            return wd.Infection * Mathf.Max(0f, c.InfectionPain.Value);
        }

        public static float PartWeight(BodyPartType part, Cfg c)
        {
            switch (part)
            {
                case BodyPartType.head:  return Mathf.Max(0f, c.PainWeightHead.Value);
                case BodyPartType.arm_l:
                case BodyPartType.arm_r: return Mathf.Max(0f, c.PainWeightArm.Value);
                case BodyPartType.leg_l:
                case BodyPartType.leg_r: return Mathf.Max(0f, c.PainWeightLeg.Value);
                default:                 return Mathf.Max(0f, c.PainWeightTorso.Value);   // chest / auto_detect
            }
        }

        // ------------------------------------------------------------------ logging

        /// <summary>One verbose "[pain]" line when a soldier's perceived tier changes, naming the wounds
        /// behind the floor. Built only while logging is on.</summary>
        private static void LogTier(SoldierWounds w)
        {
            var from = w.PainLogged;
            w.PainLogged = w.Pain;
            if (!LogGate.Verbose) return;
            Plugin.L.LogInfo($"[pain] {Who(w.Owner)} {from} -> {w.Pain}: real {w.PainValue:0} (wounds {w.WoundPain:0}), masked {w.PainSuppress:0}, felt {w.PerceivedPain:0} | {Describe(w)}");
        }

        private static string Describe(SoldierWounds w)
        {
            var c = Plugin.C;
            float coef = Mathf.Max(0f, c.PainCoefficient.Value);
            var sb = new StringBuilder();
            foreach (var kv in w.Parts)
            {
                float weight = PartWeight(kv.Key, c);
                foreach (var wd in kv.Value)
                {
                    if (!wd.Active) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(kv.Key).Append(' ');
                    if (wd.Type == WoundType.Fracture) sb.Append(wd.Stabilised ? "splinted fracture" : "fracture");
                    else
                    {
                        sb.Append(Physiology.WoundName(wd.Size, wd.Kind));
                        if (wd.Bandaged) sb.Append(" (bandaged)");
                        if (wd.Infection > 0f) sb.Append(" (infected ").Append((wd.Infection * 100f).ToString("0")).Append("%)");
                    }
                    sb.Append('=').Append((WoundContribution(wd, c) * weight * coef).ToString("0"));
                }
            }
            return sb.Length > 0 ? sb.ToString() : "no wounds";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Who(Soldier s)
        {
            try { return s != null && Interop.IsPlayer(s) ? "PLAYER" : "ai"; } catch { return "?"; }
        }
    }
}
