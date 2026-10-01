using System;
using UnityEngine;
using Random = UnityEngine.Random;

namespace AdvancedMedic
{
    /// <summary>
    /// Applies a treatment's effect to the patient's mod-side wound/vitals model, and consumes the
    /// matching item from the actor (the player-medic). The animation is handled by Procedure; all
    /// effects live here.
    ///
    /// Rules: Bandage temp-closes the worst-bleeding wound on the part (may reopen). Scissors expose a
    /// limb; Forceps then remove shrapnel (chance to re-bleed). Stitch fully closes bandaged wounds.
    /// Aspirin = painkiller (masks pain, slightly slows the heart). Syringe = epinephrine
    /// (speeds the heart; doubles the downed wake-up roll, never wakes by itself). CPR restarts
    /// the heart by chance. Tourniquet stops a limb bleeding (hurts if left on, and the limb carries
    /// part of a bleeding limb's debuff - Effects). Splint sets a broken limb. Blood IV queues a slow
    /// transfusion. Diagnose freezes a vitals snapshot.
    /// </summary>
    internal static class Treatment
    {
        private static Soldier Actor => Interop.Player();

        // The small cut pulling shrapnel can leave: a minor laceration's bleed rate (a light, steady ooze).
        private const float ForcepsCutBleedRate = 0.02f;

        public static void Apply(Soldier patient, BodyPartType part, ActionKind kind)
        {
            if (patient == null) return;
            var w = WoundState.Get(patient);
            if (w == null) return;

            bool restarted = false;
            switch (kind)
            {
                case ActionKind.Diagnose: DoDiagnose(w); break;
                case ActionKind.CPR:      restarted = DoCpr(w); break;
                case ActionKind.Stitch:   DoStitch(patient, w, part); break;
                case ActionKind.BloodIV:  Vitals.StartIv(w); break;
                case ActionKind.Bandage:  DoBandage(patient, w, part); break;
                case ActionKind.Forceps:  DoForceps(patient, w, part); break;
                case ActionKind.Scissors: DoScissors(w, part); break;
                case ActionKind.Syringe:  DoSyringe(patient, w); break;
                case ActionKind.Aspirin:  DoAspirin(w); break;
                case ActionKind.Tourniquet: DoTourniquet(w, part); break;
                case ActionKind.Splint:   DoSplint(w, part); break;
            }

            w.InvalidateMove();   // a bandaged leg / splint / tourniquet changes the walk at once

            // Drugs and fluids live in the vitals model, which the snapshot doesn't carry: on a soldier this
            // machine doesn't own, its authority applies them to ITS record too - the one its wake-up roll
            // reads (that is how a client medic's syringe helps a downed AI up on a modded host).
            switch (kind)
            {
                case ActionKind.Syringe: MpSync.SendTreat(patient, MpSync.TreatKind.Epinephrine); break;
                case ActionKind.Aspirin: MpSync.SendTreat(patient, MpSync.TreatKind.Aspirin); break;
                case ActionKind.BloodIV: MpSync.SendTreat(patient, MpSync.TreatKind.BloodIV); break;
            }

            // Propagate the post-treatment wound set to other clients (MP only). A restarted heart is
            // flagged, so it ends the arrest even on the soldier's authority.
            MpSync.Send(patient, cprRestart: restarted, immediate: true);
        }

        /// <summary>Close a wound for good (stitched / removed / set): off the chart, no dressing state.
        /// Shared by the stitch and forceps actions and the medic's stage-2 heal.</summary>
        public static void Close(Wound wd)
        {
            wd.Treated = true; wd.Bandaged = false; wd.Stabilised = false; wd.ReopenAt = 0f; wd.HealAt = 0f;
        }

        // ---- vitals actions ----
        private static void DoDiagnose(SoldierWounds w)
        {
            w.DxBlood = w.Blood; w.DxPulse = w.Pulse; w.DxBp = w.BloodPressure; w.DxBpLow = w.BpLow; w.DxPain = w.Pain;
            w.HasDiagnosis = true;
            w.DxTime = Time.time;
        }

        private static bool DoCpr(SoldierWounds w)
        {
            // Compressions only matter in cardiac arrest; success chance scales with blood.
            w.CprUntil = 0f;
            if (!w.Arrested) return false;
            float chance = Vitals.CprSuccessChance(w);
            bool ok = Random.value < chance;
            if (ok) Vitals.CprSuccess(w);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[cpr] {(ok ? "SUCCESS" : "failed")} (chance {chance:0.00}, blood {w.Blood:0.00})");
            MedicUI.SetStatus(ok ? "Pulse restored - patient still unconscious" : "No pulse yet - keep trying");
            return ok;
        }

        private static void DoSyringe(Soldier patient, SoldierWounds w)
        {
            // Epinephrine: raises the heart rate over ~2 min and boosts the wake-up roll (SyringeWakeBoost).
            // It never wakes anyone by itself: getting up is ONLY the stable-vitals roll in Vitals.
            Vitals.Init(w);
            Meds.Give(w, MedKind.Epinephrine);
            if (Interop.IsDowned(patient))
                MedicUI.SetStatus(w.Arrested ? "No pulse - needs CPR first"
                                : Physiology.Stable(w) ? "Vitals stable - should come round soon"
                                : "Vitals not stable yet - stop the bleeding / give blood");
            // Not consumed — syringe/forceps/scissors are reusable tools.
        }

        private static void DoAspirin(SoldierWounds w)
        {
            // Painkiller: masks pain while in the system (and speeds the real decline a while).
            Vitals.Init(w);
            Pain.ApplyAspirin(w);
            Items.Consume(Actor, Plugin.C.IdAspirin.Value);   // aspirin is a consumed pill
        }

        private static void DoTourniquet(SoldierWounds w, BodyPartType part)
        {
            // Toggle. A tourniquet stops the limb bleeding completely but hurts if left on.
            if (w.Tourniquets.ContainsKey(part)) w.Tourniquets.Remove(part);
            else w.Tourniquets[part] = Time.time;
            if (w.Owner != null) { Interop.SetBleeding(w.Owner, w.AnyBleeding()); }
        }

        private static void DoSplint(SoldierWounds w, BodyPartType part)
        {
            float heal = Plugin.C.FractureHealTime.Value;
            foreach (var wd in w.Of(part))
                if (wd.Active && wd.Type == WoundType.Fracture && !wd.Stabilised)
                {
                    wd.Stabilised = true;                               // splinted
                    wd.HealAt = heal > 0f ? Time.time + heal : 0f;      // EASY: knits by itself later
                }
        }

        // ---- wound actions ----
        private static void DoBandage(Soldier patient, SoldierWounds w, BodyPartType part)
        {
            // One bandage dresses ONE wound, the worst-bleeding one on the part first
            // findMostEffectiveWounds). A second bleeding wound on the same part needs its own bandage.
            Wound best = null;
            foreach (var wd in w.Of(part))
                if (wd.Active && wd.Type == WoundType.Bleeding && !wd.Bandaged && (best == null || wd.BleedRate > best.BleedRate))
                    best = wd;
            if (best != null)
            {
                best.Bandaged = true;
                best.ReopenAt = Time.time + Random.Range(Plugin.C.BandageReopenMin.Value, Plugin.C.BandageReopenMax.Value);
            }
            if (!w.AnyBleeding()) { Interop.SetBleeding(patient, false); }
            // Note: the bandage was consumed by the animation (PlayHealAnim → UseBandages, no refund
            // on the Bandage action).
        }

        private static void DoScissors(SoldierWounds w, BodyPartType part)
        {
            w.Exposed.Add(part);   // bandage scissors are a reusable tool — not consumed
        }

        private static void DoForceps(Soldier patient, SoldierWounds w, BodyPartType part)
        {
            if (w.Exposed.Contains(part) && w.CountActive(part, WoundType.Shrapnel) > 0)
            {
                foreach (var wd in w.Of(part))
                    if (wd.Active && wd.Type == WoundType.Shrapnel) Close(wd);

                if (Random.value < Plugin.C.ShrapnelRemovalBleedChance.Value)
                {
                    w.Of(part).Add(new Wound { Type = WoundType.Bleeding, Kind = WoundKind.Laceration, Size = 0, BleedRate = ForcepsCutBleedRate, Born = Time.time });
                    Interop.SetBleeding(patient, true);
                }
            }
            // Splinter forceps are a reusable tool — not consumed.
        }

        private static void DoStitch(Soldier patient, SoldierWounds w, BodyPartType part)
        {
            // Stitching permanently closes wounds that have already been BANDAGED. It does NOT close a
            // raw (un-bandaged) bleeding wound — bandage it first — nor shrapnel, which needs forceps.
            foreach (var wd in w.Of(part))
                if (wd.Active && wd.Bandaged) Close(wd);
            if (!w.AnyBleeding()) { Interop.SetBleeding(patient, false); }
        }
    }
}
