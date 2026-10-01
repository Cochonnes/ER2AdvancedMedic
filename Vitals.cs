using System;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Vitals and medical states, run at a
    /// fixed 4 Hz. The numbers come from <see cref="Physiology"/>; this class moves them forward and
    /// decides what happens to the soldier:
    ///
    ///   Awake → Unconscious (collapse: blood below DownBlood, heavy bleeding, pain spike, overdose)
    ///   → CardiacArrest (fatal vitals: heart failed from blood loss, HR &lt;20 / &gt;220, crashed BP,
    ///      a heart hit) → Dead (arrest clock runs out, or bleed-out while arrested).
    ///   Unconscious → Awake when vitals are STABLE and a periodic wake roll succeeds.
    ///   CardiacArrest → Unconscious on a successful CPR.
    ///
    /// ER2 itself can only show alive / downed / dead, so Unconscious and CardiacArrest both use the
    /// game's downed state; the medical overlay shows which one it is.
    ///
    /// How much of that this machine decides for a soldier is its <see cref="VitalsControl"/>:
    ///   Full     - every rule above (SP: per DownedSurviveMode; MP: AI, on the host).
    ///   BleedOut - an MP human, on their OWN machine only: the blood-loss rules (collapse from blood loss
    ///              or heavy bleeding, the failing heart's arrest, bleed-out / MaxDownedTime death, and the
    ///              wake-up roll out of a collapse of ours). The rest of their downed state stays the
    ///              game's: no vanilla-timer pin, no revive block, no pain pass-out / overdose / heart-hit.
    ///   None     - vitals simulated for the display only.
    /// </summary>
    internal enum VitalsControl { None, BleedOut, Full }

    internal static class Vitals
    {
        // When our code intentionally wakes a soldier, its pointer is set here so the revive-block
        // prefix lets that one SetIncapacitated(false) through.
        public static IntPtr WakingPtr = IntPtr.Zero;

        private const float Step = 0.25f;   // vitals tick interval
        private static float _acc;

        // ---- fatal / critical vitals (the reference model's thresholds) ----
        private const float FatalPulseLow = 20f, FatalPulseHigh = 220f;          // outside → arrest
        private const float CrashSystolic = 50f, CrashDiastolic = 40f, CrashPulse = 40f;   // all three below → arrest
        private const float FatalDiastolic = 190f;                                // diastolic at/above → arrest
        private const float CriticalPulse = 30f;                                  // below: periodic arrest roll
        private const float CriticalCheckSeconds = 5f;
        private const float CriticalArrestBase = 0.4f, CriticalArrestSlope = 0.6f;   // 40% at 30 bpm → 100% at 20

        // ---- heart-rate model ----
        private const float TargetMeanBp = 107f;          // mean BP the heart tries to hold (x blood ratio)
        private const float MinCompensationPulse = 30f;   // floor the compensation formula starts from
        private const float MinCompensationShare = 0.75f; // compensation never targets below 75% of NormalPulse
        private const float PainHrFrom = 0.2f;            // perceived pain (0..1) above which the pulse rises
        private const float PainHrBase = 80f, PainHrSlope = 50f;
        private const float DiastolicSafeCap = 180f;      // EASY: keep the diastolic under the fatal 190
        private const float DrugPulseFloor = 55f;         // EASY: drugs alone don't drag a heart below this
        private const float CprPulseMin = 25f, CprPulseMax = 35f;   // weak pulse shown during compressions
        private const float CprRestartPulse = 30f;        // pulse right after a successful CPR
        private const float HeartFailDecay = 10f;         // failing heart: pulse loses 1/10 per second

        // A forgotten tourniquet hurts, but is capped just below High pain (Pain.TierOf: High >= 67), so it
        // can never pass a soldier out or block waking up on its own.
        private const float TourniquetPainCap = 60f;

        // Blood at which anyone bleeds out, whatever our control over their downed state (0% - clamped).
        private const float EmptyBlood = 0.001f;

        // Kills are deferred to after the loop: Kill_Post prunes WoundState, which would otherwise
        // modify the dictionary we're iterating and abort the rest of the tick.
        private static readonly System.Collections.Generic.List<Soldier> _kills = new System.Collections.Generic.List<Soldier>();
        private static readonly System.Collections.Generic.List<IntPtr> _dead = new System.Collections.Generic.List<IntPtr>();

        /// <summary>Whether this machine makes the state decisions (collapse / wake / arrest / death) for a
        /// soldier: one whose downed state we manage (HpLogic.DownedApplies), and in MP only on that
        /// soldier's authority (the host, for AI), so clients never each roll their own knockouts and
        /// wakes for the same AI and fight over it - or down a copy the host is still running.</summary>
        public static bool OwnsState(Soldier s)
        {
            if (!HpLogic.DownedApplies(s)) return false;
            return Interop.HasAuthority(s);
        }

        /// <summary>How much of a soldier's collapse / wake / death this machine decides (see
        /// <see cref="VitalsControl"/>). Full = OwnsState. BleedOut = the local player in an MP room: a
        /// human's own client owns its avatar, so it - and nobody else, the host included - applies the
        /// blood-loss rules to it, and the outcome goes out through the game's own life RPC / synced kill
        /// (NetLife, KillSynched) plus our snapshot. Before this a client at 0% blood only got a grey
        /// screen. Needs the player in DownedSurviveMode, like the player in single player.</summary>
        public static VitalsControl ControlOf(Soldier s, bool inRoom, bool authority)
        {
            if (!authority || s == null) return VitalsControl.None;
            if (HpLogic.DownedApplies(s, inRoom)) return VitalsControl.Full;
            if (inRoom && Interop.IsPlayer(s) && HpLogic.ConvertApplies(s)) return VitalsControl.BleedOut;
            return VitalsControl.None;
        }

        public static VitalsControl ControlOf(Soldier s) => ControlOf(s, Interop.InMpRoom(), Interop.HasAuthority(s));

        /// <summary>THE rule for who runs a soldier's wound clocks (a dressing reopening, a bruise or splinted
        /// bone healing, infection growing), used by the vitals step and by a received snapshot:
        /// - the soldier's authority always (the host for AI, each player for their own avatar), which
        ///   broadcasts every change;
        /// - anyone else only for AI on an UNMODDED host, where nobody broadcasts clocks, so each client's
        ///   locally inferred AI wounds need their own.
        /// The host used to run its own clocks for its clients' avatars too, and a host medic's treatment
        /// then pushed that drifted state (dressings reopened on the host only) over the owner's.</summary>
        public static bool RunsWoundClocks(Soldier s, bool authority)
        {
            if (authority) return true;
            if (!Interop.InMpRoom()) return true;
            if (Interop.IsMaster() || MpSync.HostSendsWounds) return false;
            return !Interop.IsHuman(s);
        }

        private static void Die(Soldier s) { if (s != null && !_kills.Contains(s)) _kills.Add(s); }

        public static void Wake(Soldier s)
        {
            var w = WoundState.Peek(s);
            if (w != null) { w.DownedAt = 0f; w.CollapsedByVitals = false; w.HighPainSince = -1f; w.State = MedState.Awake; }
            WakingPtr = Interop.PtrOf(s);
            Interop.SetIncapacitated(s, false);
            WakingPtr = IntPtr.Zero;
            MpSync.Send(s);   // propagate the wake to other clients (no-op in SP / while applying remote)
            // SetIncapacitated(false) wrote life 25 LOCALLY only. The owner pushes it through the vanilla life
            // RPC, so every machine (modded or not) stands the soldier up and holds 25, not a stale 0 its
            // next hit would send back. A non-owner (mirroring a snapshot) sends nothing - NetLife checks.
            NetLife.SyncOwnLife(s, "wake", revive: true);
        }

        public static void TickAll()
        {
            _acc += Time.deltaTime;
            if (_acc < Step) return;
            float dt = Mathf.Min(_acc, 1f);
            _acc = 0f;
            float now = Time.time;
            bool inRoom = Interop.InMpRoom();   // frame-cached; read once for the whole pass

            _kills.Clear(); _dead.Clear();
            foreach (var kv in WoundState.All())
            {
                var w = kv.Value;
                var s = w.Owner;
                if (s == null) continue;

                // A corpse can be re-tracked by a damage postfix that runs after Kill_Post pruned it;
                // never simulate (and so never re-knock-out or re-kill) the dead.
                // (Interop.IsDead, not !IsAlive: a DOWNED soldier reads IsAlive=false at life 0.)
                if (Interop.IsDead(s)) { _dead.Add(kv.Key); continue; }

                // The soldier's authority, read ONCE and handed to ControlOf and the tick below (each used
                // to resolve it again: a PhotonView lookup plus a native IsMine per soldier per step).
                bool authority = Interop.HasAuthority(s);

                // What WE decide for this soldier (see VitalsControl). Every tracked soldier's vitals and
                // wound clocks run whatever the answer: a soldier outside DownedSurviveMode (PlayerOnly /
                // Noone) still has dressings that reopen, bruises that heal and blood that drains - it used
                // to be skipped outright in single player - and in an MP room a client runs the numbers
                // (display-only) for the soldiers it doesn't own, so a diagnosis shows real values.
                var ctl = ControlOf(s, inRoom, authority);

                Init(w);
                Tick(s, w, dt, now, ctl, authority);
            }
            // A corpse no Kill hook reported: drop EVERYTHING keyed on it (Lua/inventory caches, view,
            // spawn kit, queued snapshot), not just the wound record - the pointer gets reused.
            foreach (var p in _dead) SoldierRegistry.Forget(p, dead: true);
            // Networked kill in a room (we only get here as the soldier's authority - ControlOf): a plain
            // Kill() killed the local copy only, and every client kept a downed AI that never died. For
            // an MP human's own avatar (BleedOut) this is the game's synced death, which every machine,
            // modded or not, turns into a normal death and respawn.
            foreach (var k in _kills) Interop.KillAuthoritative(k);
            _kills.Clear(); _dead.Clear();
        }

        public static void Init(SoldierWounds w)
        {
            if (w.VitalsInit) return;
            w.VitalsInit = true;
            w.Blood = 1f; w.Pulse = Plugin.C.NormalPulse.Value; w.Resistance = Physiology.DefaultResistance;
            Physiology.BloodPressure(w, out w.BloodPressure, out w.BpLow);
            w.State = MedState.Awake;
        }

        private static void Tick(Soldier s, SoldierWounds w, float dt, float now, VitalsControl ctl, bool authority)
        {
            var c = Plugin.C;
            bool downed = Interop.IsDowned(s);

            TickIv(w, dt);
            // Wound clocks (a bandage reopening, a bruise or splinted bone healing, an unstitched dressing
            // getting infected): see RunsWoundClocks. The authority broadcasts each change; the others take
            // it from its snapshot rather than running divergent clocks.
            if (RunsWoundClocks(s, authority)) TickWoundTimers(s, w, now, dt, authority && MpSync.Active);

            // Medications → heart-rate target shift, pain suppression, resistance.
            Meds.Evaluate(w, now, out float hrAdj, out float painSupp, out float resAdj);
            w.PainSuppress = Mathf.Clamp(painSupp, 0f, 100f);
            w.Resistance = Mathf.Max(1f, Physiology.DefaultResistance + resAdj);

            // The wounds' bleeding, summed ONCE for this step (it walks every wound, and the loss, the
            // heavy-bleeding collapse, the stability test and the downed clock below all need it).
            float bleeding = Physiology.WoundBleeding(w);

            // Blood: bleeding out, and (EASY) a slow self-rebuild while nothing bleeds.
            float lossL = Physiology.BloodLossLitres(w, bleeding);
            w.Blood -= lossL * dt / Physiology.BloodLitres;
            if (lossL <= 0f && !w.Arrested && w.Pulse > 0f && w.IvRemaining <= 0f)
                w.Blood += Mathf.Max(0f, c.BloodRegenPerSec.Value) * dt;
            w.Blood = Mathf.Clamp01(w.Blood);

            UpdateHeartRate(w, hrAdj, InfectionHr(w), dt, now);
            Physiology.BloodPressure(w, out w.BloodPressure, out w.BpLow);

            TourniquetPain(w, now);

            // Out of blood = dead, under EVERY control and every DownedSurviveMode: a soldier outside our
            // downed model (SP PlayerOnly / Noone, an MP human with Noone, host AI outside the mode) used to
            // sit at 0% with a grey screen forever. Only on the soldier's authority, through the normal
            // (synced) kill; nothing else about those modes changes. (Full and BleedOut normally die earlier,
            // in cardiac arrest below ArrestBleedoutBlood; this is their backstop too.)
            if (authority && w.Blood <= EmptyBlood)
            {
                if (c.DebugLogging.Value) Plugin.L.LogInfo($"[vitals] death - out of blood (control={ctl})");
                Die(s);
                return;
            }

            // Everything below OWNS the soldier's downed/death. Not ours at all: show what the game (or the
            // soldier's authority, via its snapshot) says - and keep a received arrest's countdown running
            // for the panel; it used to sit frozen at its first value on every other client.
            if (ctl == VitalsControl.None)
            {
                w.State = w.Arrested ? MedState.CardiacArrest : downed ? MedState.Unconscious : MedState.Awake;
                if (w.Arrested) w.ArrestTimeLeft = Mathf.Max(0f, w.ArrestTimeLeft - dt * (now < w.CprUntil ? 0.5f : 1f));
                w.Overdosed = false;   // the authority acts on an overdose, not this copy
                return;
            }
            bool full = ctl == VitalsControl.Full;

            // Keep our state in step with the game's (vanilla/HpLogic downs, AI-medic heals).
            if (downed && w.State == MedState.Awake) w.State = MedState.Unconscious;
            if (!downed && !w.Arrested && w.State != MedState.Awake) w.State = MedState.Awake;

            // ---- Cardiac arrest: the clock, CPR slowing it, bleed-out ----
            if (w.Arrested)
            {
                w.State = MedState.CardiacArrest;
                if (!downed) { Interop.SetIncapacitated(s, true); w.CollapsedByVitals = true; NetLife.SyncOwnLife(s, "arrest re-down"); }
                w.ArrestTimeLeft -= dt * (now < w.CprUntil ? 0.5f : 1f);   // CPR halves the clock
                if (w.ArrestTimeLeft <= 0f || w.Blood < c.ArrestBleedoutBlood.Value)
                {
                    if (c.DebugLogging.Value) Plugin.L.LogInfo($"[vitals] death in cardiac arrest (time={w.ArrestTimeLeft:0} blood={w.Blood:0.00}){(full ? "" : " - own MP avatar")}");
                    Die(s);
                }
                return;
            }

            // ---- Fatal vitals → cardiac arrest ----
            bool fatal = false, critical = false;
            if (w.Pulse < FatalPulseLow || w.Pulse > FatalPulseHigh) fatal = true;
            else if (w.BloodPressure < CrashSystolic && w.BpLow < CrashDiastolic && w.Pulse < CrashPulse) fatal = true;
            else if (w.BpLow >= FatalDiastolic) fatal = true;
            else if (w.Pulse < CriticalPulse)
            {
                if (now >= w.NextHrCheck)
                {
                    w.NextHrCheck = now + CriticalCheckSeconds;
                    float p = CriticalArrestBase + CriticalArrestSlope * (CriticalPulse - w.Pulse) / (CriticalPulse - FatalPulseLow);
                    if (UnityEngine.Random.value < p) fatal = true;
                    else critical = true;
                }
            }
            if (fatal) { StartArrest(s, w, "fatal vitals"); return; }

            // ---- Critical → collapse (unconscious) ----
            // (An MP human - BleedOut - collapses from the blood-loss causes only; an overdose stays the
            // single-player / AI rule.)
            string why = null;
            if (critical) why = "critical heart rate";
            else if (w.Blood < c.DownBlood.Value) why = "blood loss";
            else if (bleeding > c.KnockOutBleeding.Value) why = "heavy bleeding";
            else if (full && w.Overdosed) why = "overdose";
            w.Overdosed = false;
            if (why != null && !downed) { KnockOut(s, w, why); downed = true; }

            // ---- Sustained HIGH (perceived) pain → pass out ----
            // The knock-out tier: felt pain without what infection adds (Pain.KnockoutTier). Full only.
            var koPain = Pain.KnockoutTier(w);
            if (full && c.EnablePainPassout.Value && koPain == PainTier.High)
            {
                if (w.HighPainSince < 0f) w.HighPainSince = now;
                else if (!downed && now - w.HighPainSince >= c.PainPassoutTime.Value
                         && UnityEngine.Random.value < c.PainPassoutChancePerSec.Value * dt)
                { KnockOut(s, w, "sustained high pain"); downed = true; }
            }
            else w.HighPainSince = -1f;

            if (!downed) return;

            // ---- Spontaneous wake-up ----
            // An MP human only rolls out of a collapse of OUR making: a down from a hit is the game's, and
            // its way out stays the game's (a medic's revive, or its own bleed-out timer).
            float downFor = w.DownedAt > 0f ? now - w.DownedAt : 0f;
            if (now >= w.NextWakeCheck)
            {
                w.NextWakeCheck = now + Mathf.Max(1f, c.WakeCheckInterval.Value);
                if ((full || w.CollapsedByVitals) && Physiology.Stable(w, bleeding) && koPain != PainTier.High)
                {
                    float epi = Meds.Strength(w, MedKind.Epinephrine, now);
                    float chance = c.SpontaneousWakeChance.Value * Mathf.Lerp(1f, Mathf.Max(1f, c.SyringeWakeBoost.Value), epi);
                    if (UnityEngine.Random.value < chance)
                    {
                        if (c.DebugLogging.Value) Plugin.L.LogInfo($"[wake] stable vitals blood={w.Blood:0.00} hr={w.Pulse:0} bp={w.BloodPressure:0}/{w.BpLow:0} -> WAKE");
                        Wake(s);
                        return;
                    }
                }
            }

            // Fallback: a downed soldier nobody treats doesn't lie there forever — after MaxDownedTime if
            // still bleeding, or after 3x that if their vitals never became stable (e.g. BloodRegenPerSec
            // 0 and no IV). A bandaged patient who is recovering is never killed by the clock.
            if ((downFor > c.MaxDownedTime.Value && Physiology.BloodLossLitres(w, bleeding) > 0f)
                || (downFor > 3f * c.MaxDownedTime.Value && !Physiology.Stable(w, bleeding)))
            {
                if (c.DebugLogging.Value) Plugin.L.LogInfo($"[vitals] death - downed and untreated/unstable too long{(full ? "" : " - own MP avatar")}");
                Die(s);
            }
        }

        /// <summary>Heart rate: moves halfway to its target each second.</summary>
        private static void UpdateHeartRate(SoldierWounds w, float hrAdj, float infectionHr, float dt, float now)
        {
            var c = Plugin.C;
            if (w.Arrested)
            {
                w.Pulse = now < w.CprUntil ? UnityEngine.Random.Range(CprPulseMin, CprPulseMax) : 0f;   // chest compressions
                return;
            }

            float target, change;
            if (w.Blood > c.HeartFailBlood.Value)
            {
                target = c.NormalPulse.Value;
                // Blood loss: speed up to hold the blood pressure (target mean BP x blood ratio).
                if (w.Blood < c.CompensateBelowBlood.Value)
                {
                    float mean = (2f / 3f) * w.BloodPressure + (1f / 3f) * w.BpLow;
                    float targetBp = TargetMeanBp * w.Blood;
                    target = Mathf.Max(w.Pulse, MinCompensationPulse) * (targetBp / Mathf.Max(45f, mean));
                    target = Mathf.Max(target, c.NormalPulse.Value * MinCompensationShare);
                }
                float pain = w.PerceivedPain / 100f;
                if (pain > PainHrFrom) target = Mathf.Max(target, PainHrBase + PainHrSlope * pain);
                // EASY: cap every source (blood loss, pain, drugs, infection) below the fatal zone. At full
                // blood the BP model makes diastolic ~= HR, so ~190 bpm would already trip the diastolic rule.
                target = Mathf.Clamp(target + hrAdj + infectionHr, 0f, c.MaxCompensationHr.Value);
                // ...and keep the diastolic (≈ output x resistance x 6.32) under the fatal 190 even when
                // painkillers have raised the resistance.
                float perBeat = Mathf.InverseLerp(0.5f, 1f, w.Blood) * 0.095f / 60f * w.Resistance * 6.3157894f;
                if (perBeat > 0f) target = Mathf.Min(target, DiastolicSafeCap / perBeat);
                // EASY: drugs alone don't drag a healthy heart below DrugPulseFloor.
                if (hrAdj < 0f) target = Mathf.Max(target, Mathf.Min(DrugPulseFloor, c.NormalPulse.Value));
                change = (target - w.Pulse) / 2f;
            }
            else
            {
                // The heart is failing: the pulse sinks toward zero (→ critical, then arrest rolls).
                target = 0f;
                change = -w.Pulse / HeartFailDecay;
            }

            w.Pulse = change < 0f ? Mathf.Max(w.Pulse + dt * change, target) : Mathf.Min(w.Pulse + dt * change, target);
        }

        private static void TickIv(SoldierWounds w, float dt)
        {
            // IV fluids flow in. Multiple attached IVs infuse FASTER (IvSpeedPerExtra per extra bag).
            // Decrement the queued amount by the intended flow even when the pool is already full
            // (excess wasted), so the "transfusing" indicator always clears.
            var c = Plugin.C;
            if (w.IvRemaining <= 0f || w.IvCount <= 0) return;
            float flow = Mathf.Min(c.IvRatePerSec.Value * IvSpeed(w.IvCount) * dt, w.IvRemaining);
            w.IvRemaining -= flow;
            w.Blood = Mathf.Min(1f, w.Blood + flow);
            int pending = Mathf.CeilToInt(w.IvRemaining / Mathf.Max(0.0001f, c.IvAmount.Value));
            if (pending < w.IvCount) w.IvCount = Mathf.Max(0, pending);
            if (w.IvRemaining < 0.0005f) { w.IvRemaining = 0f; w.IvCount = 0; }
        }

        /// <summary>Bandages reopening, self-healing wounds (bruises/burns, splinted fractures) and infection
        /// setting into unstitched dressings. Any change is broadcast once per tick (a healed splinted
        /// fracture used to stay forever on every other machine, which had no clock for it); infection is
        /// broadcast per quantised level step, not per tick.</summary>
        private static void TickWoundTimers(Soldier s, SoldierWounds w, float now, float dt, bool broadcast)
        {
            var c = Plugin.C;
            bool changed = false, levelStep = false;
            bool infect = c.InfectionEnabled.Value;
            float onset = Mathf.Max(0f, c.InfectionOnsetMinutes.Value) * 60f;
            float ramp = Mathf.Max(1f, c.InfectionRampMinutes.Value * 60f);
            foreach (var kv in w.Parts)
                foreach (var wd in kv.Value)
                {
                    if (wd.Treated) continue;
                    if (wd.Bandaged && wd.ReopenAt > 0f && now >= wd.ReopenAt)
                    {
                        wd.Bandaged = false; wd.ReopenAt = 0f;
                        if (wd.Type == WoundType.Bleeding) Interop.SetBleeding(s, true);
                        if (c.DebugLogging.Value) Plugin.L.LogInfo("[vitals] bandage reopened");
                        changed = true;
                    }
                    if (wd.HealAt > 0f && now >= wd.HealAt) { wd.Treated = true; wd.HealAt = 0f; changed = true; }

                    // Infection: a dressed but unstitched bleeding wound older than the onset slowly gets
                    // infected. It only grows while dressed (a reopened wound keeps its level until it is
                    // dressed again) and only stitching - or a medic / tent heal - clears it.
                    if (infect && wd.Bandaged && wd.Type == WoundType.Bleeding && wd.Infection < 1f && now - wd.Born >= onset)
                    {
                        int before = MpSync.InfectionLevel(wd.Infection);
                        wd.Infection = Mathf.Min(1f, wd.Infection + dt / ramp);
                        if (MpSync.InfectionLevel(wd.Infection) != before) levelStep = true;
                    }
                }
            if (changed || levelStep) w.InvalidateMove();   // also expires the pain floor
            if ((changed || levelStep) && broadcast) MpSync.Send(s);
        }

        /// <summary>The strongest infection on the soldier (0..1), x InfectionHeartRate: a fever nudges the
        /// heart-rate target up. Capped with every other source by MaxCompensationHr, so it can't kill.</summary>
        private static float InfectionHr(SoldierWounds w)
        {
            var c = Plugin.C;
            if (!c.InfectionEnabled.Value) return 0f;
            float worst = 0f;
            foreach (var kv in w.Parts)
                foreach (var wd in kv.Value)
                    if (wd.Active && wd.Infection > worst) worst = wd.Infection;
            return worst * Mathf.Max(0f, c.InfectionHeartRate.Value);
        }

        /// <summary>A tourniquet left on past TourniquetPainAfter hurts more and more.</summary>
        private static void TourniquetPain(SoldierWounds w, float now)
        {
            if (w.Tourniquets.Count == 0) return;
            float oldest = float.MaxValue;
            foreach (var kv in w.Tourniquets) oldest = Mathf.Min(oldest, kv.Value);
            float secs = now - oldest - Plugin.C.TourniquetPainAfter.Value;
            if (secs > 0f) Pain.Raise(w, Mathf.Min(secs * Plugin.C.TourniquetPainPerSec.Value, TourniquetPainCap), scaled: false);
        }

        public static void KnockOut(Soldier s, SoldierWounds w, string why)
        {
            w.State = MedState.Unconscious;
            Interop.SetIncapacitated(s, true);
            w.CollapsedByVitals = true;   // after the call: its postfix stamps the down, and this marks it ours
            MpSync.Send(s);
            NetLife.SyncOwnLife(s, "knockout");   // life 0 was written locally only: the owner broadcasts it
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[vitals] collapse ({why}) blood={w.Blood:0.00} hr={w.Pulse:0} bp={w.BloodPressure:0}/{w.BpLow:0}");
        }

        /// <summary>Stable vitals and a decent blood volume — used by the medic/tent heal to decide
        /// whether there's anything left to treat.</summary>
        public static bool VitalsInRange(SoldierWounds w) => Physiology.Stable(w) && w.Blood >= Physiology.Class2;

        // ---- action helpers ----

        /// <summary>CPR success chance scales with blood between class IV and class II.</summary>
        public static float CprSuccessChance(SoldierWounds w)
        {
            var c = Plugin.C;
            return Mathf.Lerp(c.CprChanceMin.Value, c.CprChanceMax.Value, Mathf.InverseLerp(Physiology.Class4, Physiology.Class2, w.Blood));
        }

        /// <summary>A successful CPR restarts the heart; the patient stays unconscious.</summary>
        public static void CprSuccess(SoldierWounds w)
        {
            w.Arrested = false;
            w.ArrestTimeLeft = 0f;   // the idle sweep reads a leftover clock as "still in arrest" forever
            w.Pulse = CprRestartPulse;
            w.State = MedState.Unconscious;
            w.NextHrCheck = Time.time + CriticalCheckSeconds;
            w.NextWakeCheck = Time.time + Mathf.Max(1f, Plugin.C.WakeCheckInterval.Value);
        }

        /// <summary>Clear a soldier's wounds and reset vitals to stable (used when the game's own
        /// AI heals them, a medical tent, etc., so our model matches).</summary>
        public static void StabilizeAndClear(Soldier s)
        {
            var w = WoundState.Peek(s);
            if (w == null) return;
            w.Parts.Clear(); w.Exposed.Clear(); w.Tourniquets.Clear(); w.Meds.Clear();
            w.PainValue = 0f; w.PainSuppress = 0f; w.Pain = PainTier.None; w.AspirinActive = false;
            Pain.Refresh(w);   // wounds are gone: drop the cached floor now, or it would put the pain back
            w.Blood = 1f; w.Pulse = Plugin.C.NormalPulse.Value; w.Resistance = Physiology.DefaultResistance;
            Physiology.BloodPressure(w, out w.BloodPressure, out w.BpLow);
            w.Arrested = false; w.ArrestTimeLeft = 0f; w.CprUntil = 0f; w.Overdosed = false;
            w.HighPainSince = -1f; w.IvRemaining = 0f; w.IvCount = 0; w.CollapsedByVitals = false;
            w.State = MedState.Awake;
            Interop.SetBleeding(s, false);
            // An AI medic's heal / self-bandage stands a downed soldier back up. Only a DOWNED one:
            // SetIncapacitated(false) writes life 25 even on a soldier who is already up (disassembled -
            // its only gate is life >= 0), which knocked a standing player's HP down to 25.
            if (Interop.IsDowned(s)) Wake(s);
            MpSync.Send(s);   // propagate the cleared wound set to other clients (MP only)
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo("[vitals] healed — cleared wounds + stabilised");
        }

        /// <summary>Put a soldier into cardiac arrest: pulse 0, the ArrestTime clock started (+-10%),
        /// and a collapse. Needs CPR before the clock runs out. Only for soldiers whose downed state we
        /// own (OwnsState): the heart hit is not one of the blood-loss rules an MP human gets.</summary>
        public static void TriggerArrest(Soldier s, SoldierWounds w, string why)
        {
            if (w == null || w.Arrested || !OwnsState(s)) return;
            StartArrest(s, w, why);
        }

        /// <summary>The arrest itself, for a caller that already knows the soldier is under our control
        /// (the vitals step: Full, or an MP human's own heart failing from blood loss).</summary>
        private static void StartArrest(Soldier s, SoldierWounds w, string why)
        {
            if (w.Arrested) return;
            Init(w);
            w.Arrested = true;
            w.Pulse = 0f;
            w.State = MedState.CardiacArrest;
            float t = Mathf.Max(1f, Plugin.C.ArrestTime.Value);
            w.ArrestTimeLeft = t + t * UnityEngine.Random.Range(-0.1f, 0.1f);
            Interop.SetIncapacitated(s, true);
            w.CollapsedByVitals = true;
            MpSync.Send(s);
            NetLife.SyncOwnLife(s, "cardiac arrest");   // the collapse's life 0, owner-broadcast
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[vitals] CARDIAC ARREST ({why}) — {w.ArrestTimeLeft:0}s to CPR");
        }

        public static void StartIv(SoldierWounds w)
        {
            // Attach another IV bag: one more dose to infuse, one more toward the speed multiplier.
            var c = Plugin.C;
            if (w.IvCount >= Mathf.Max(1, c.IvMaxCount.Value)) return;   // cap concurrent IVs
            w.IvCount++;
            w.IvRemaining += c.IvAmount.Value;
        }

        /// <summary>Infusion speed multiplier for N attached IVs: 1→1x, 2→1.5x, 3→2x … (config).</summary>
        private static float IvSpeed(int count)
        {
            if (count <= 1) return 1f;
            return 1f + Plugin.C.IvSpeedPerExtra.Value * (count - 1);
        }
    }
}
