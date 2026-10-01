using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Medications. A dose
    /// ramps in, holds, then fades over its time in the system:
    ///
    ///   effect = min(1, (t / timeToPeak)^2) x (timeInSystem - t) / timeInSystem
    ///
    /// Each dose adds a heart-rate target shift, a pain suppression, and a peripheral-resistance shift
    /// scaled by that effect. ER2 only has two medical drugs, modelled as:
    /// aspirin = a painkiller (masks pain, slightly slows the heart), syringe = epinephrine
    /// (speeds the heart, boosts waking). Too many doses in the system at once is an overdose.
    /// </summary>
    internal static class Meds
    {
        // The HR effect depends on the CURRENT heart rate band: below LowPulse, LowPulse..HighPulse, above.
        private const float LowPulse = 55f, HighPulse = 110f;
        // Painkiller: HR shift from PainkillerHrMin to the band's value, plus a small resistance rise.
        private const float PainkillerHrMin = -5f, PainkillerHrLow = -10f, PainkillerHrNormal = -15f, PainkillerHrHigh = -17f;
        private const float PainkillerResistance = 5f;
        // Epinephrine: HR shift from EpiHrMin to the band's value; reaches full effect after EpiPeakSeconds.
        private const float EpiHrMin = 10f, EpiHrLow = 20f, EpiHrNormal = 50f, EpiHrHigh = 40f;
        private const float EpiPeakSeconds = 10f;

        public static void Give(SoldierWounds w, MedKind kind)
        {
            var c = Plugin.C;
            float hr = w.Pulse;
            int band = hr < LowPulse ? 0 : hr <= HighPulse ? 1 : 2;
            var d = new MedDose { Kind = kind, Added = Time.time };
            switch (kind)
            {
                case MedKind.Painkiller:
                    d.Peak = c.AspirinPeak.Value; d.Duration = c.AspirinDuration.Value;
                    d.Pain = c.AspirinPainReduce.Value;
                    d.Hr = Rand(PainkillerHrMin, band == 0 ? PainkillerHrLow : band == 1 ? PainkillerHrNormal : PainkillerHrHigh);
                    d.Res = PainkillerResistance;
                    break;
                case MedKind.Epinephrine:
                    d.Peak = EpiPeakSeconds; d.Duration = c.EpiDuration.Value;
                    d.Hr = Rand(EpiHrMin, band == 0 ? EpiHrLow : band == 1 ? EpiHrNormal : EpiHrHigh);
                    break;
            }
            w.Meds.Add(d);

            int limit = kind == MedKind.Painkiller ? c.AspirinMaxDose.Value : c.SyringeMaxDose.Value;
            int dev = kind == MedKind.Painkiller ? 1 : 2;   // overdose limit deviation
            if (Count(w, kind) > limit + Random.Range(-dev, dev + 1)) w.Overdosed = true;
        }

        private static float Rand(float a, float b) => a + Random.value * (b - a);

        public static int Count(SoldierWounds w, MedKind kind)
        {
            int n = 0;
            foreach (var d in w.Meds) if (d.Kind == kind) n++;
            return n;
        }

        /// <summary>Sum the active doses' adjustments and drop expired ones.</summary>
        public static void Evaluate(SoldierWounds w, float now, out float hrAdj, out float painSupp, out float resAdj)
        {
            hrAdj = 0f; painSupp = 0f; resAdj = 0f;
            for (int i = w.Meds.Count - 1; i >= 0; i--)
            {
                var d = w.Meds[i];
                float t = now - d.Added;
                if (t >= d.Duration || d.Duration <= 0f) { w.Meds.RemoveAt(i); continue; }
                float ramp = d.Peak > 0f ? Mathf.Min(1f, (t / d.Peak) * (t / d.Peak)) : 1f;
                float e = ramp * (d.Duration - t) / d.Duration;
                hrAdj += d.Hr * e;
                painSupp += d.Pain * e;
                resAdj += d.Res * e;
            }
        }

        /// <summary>Current strength (0..1) of the strongest dose of a kind — drives the epinephrine
        /// wake-up boost.</summary>
        public static float Strength(SoldierWounds w, MedKind kind, float now)
        {
            float best = 0f;
            foreach (var d in w.Meds)
            {
                if (d.Kind != kind) continue;
                float t = now - d.Added;
                if (t >= d.Duration) continue;
                float ramp = d.Peak > 0f ? Mathf.Min(1f, (t / d.Peak) * (t / d.Peak)) : 1f;
                best = Mathf.Max(best, ramp * (d.Duration - t) / d.Duration);
            }
            return best;
        }
    }
}
