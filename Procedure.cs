using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using Random = UnityEngine.Random;

namespace AdvancedMedic
{
    /// <summary>
    /// A timed treatment: clicking an action hides the overlay, plays a visible first-person
    /// animation on the medic, and when the timer elapses reopens the overlay and applies the
    /// effect (Treatment). All effects are mod-side; the animation is cosmetic.
    ///
    /// Syringe-type tools play the syringe clip, everything else the self-bandage clip (see
    /// PlayHealAnim). Reviving is decided by the vitals wake-up roll, so the vanilla revive is
    /// suppressed during the procedure.
    /// </summary>
    internal static class Procedure
    {
        public static bool Active { get; private set; }
        private static Soldier _patient;
        private static BodyPartType _part;
        private static ActionKind _kind;
        private static float _endTime;

        // The stitch time model: the configured base + per-extra-wound times, cut by 40% overall.
        private const float StitchTimeFactor = 0.6f;

        public static void Start(Soldier patient, BodyPartType part, ActionKind kind)
        {
            if (Active || patient == null) return;
            var medic = Interop.Player();
            if (medic == null) return;

            _patient = patient; _part = part; _kind = kind;
            _bandagePaid = false; _vanillaRevive = false;

            // The bandage the self-heal clip consumes is PRE-PAID inside PlayBandage (timing-independent).
            // The reusable TOOLS (syringe/forceps/scissors) still use a completion snapshot-restore
            // backstop, since whether/when UseSyrynge consumes them is less certain.
            _restoreOn = medic;
            _restoreSnap = SnapshotAnimItems(medic, kind);

            float dur = Mathf.Max(0.5f, DurationFor(kind, patient, part));   // the clip needs a moment
            _endTime = Time.time + dur;   // set before the clip: VerifyAnim's retries are bounded by it

            if (Plugin.C.DebugLogging.Value)
                Plugin.L.LogInfo($"[anim] {kind}{TourniquetNote(patient, part, kind)} on {part}: " +
                                 $"self={Interop.SamePtr(medic, patient)} bandages carried={Items.CountOf(medic, Plugin.C.IdBandage.Value)}");
            _clip = PlayHealAnim(medic, kind, patient);
            if (kind == ActionKind.Bandage && _clip == Clip.Started) _bandagePaid = true;   // the clip spends it
            _clipRetries = ClipRetries;
            _animMedic = medic;
            _animCheckAt = Time.time + ClipRetryEvery;   // then confirm a first-person clip is really playing

            Active = true;

            // While compressions are given the arrest clock runs at half speed and the heart
            // shows a weak 25-35 bpm.
            if (kind == ActionKind.CPR) { var pw = WoundState.Get(patient); if (pw != null) pw.CprUntil = _endTime; }
            MedicUI.Hidden = true;

            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[proc] {kind} on {part} — {dur:0.0}s");
        }

        private static Soldier _animMedic;
        private static float _animCheckAt;

        /// <summary>What we know about this procedure's first-person clip. The bandage clip is CONFIRMED
        /// (Soldier.UseBandages' coroutine locks CanPlaySpecialAnim the moment it really starts); the
        /// syringe clip isn't, so it is checked with FPSGunManager.IsPlayingAnim instead.</summary>
        private enum Clip { Unknown, Started, NotStarted }
        private static Clip _clip;
        private static int _clipRetries;
        private const int ClipRetries = 4;          // bandage-clip retries: covers the ~1-2 s re-equip lock of a previous clip
        private const float ClipRetryEvery = 0.4f;

        /// <summary>Some game heal routines silently do nothing (no clip, no error). Shortly after starting,
        /// check the clip: a bandage clip that was refused (special-anim lock, not bleeding, ...) is
        /// retried a few times while the procedure still has time; an unconfirmed (syringe) clip that
        /// isn't playing falls back to the bandage clip. IsPlayingAnim is NOT trusted for a refused
        /// bandage clip: it also reads true for the gun's idle/aim animation, which is how a refused
        /// tourniquet clip used to pass this check and never get its fallback.</summary>
        private static void VerifyAnim()
        {
            if (_animCheckAt <= 0f || Time.time < _animCheckAt) return;
            _animCheckAt = 0f;
            var medic = _animMedic;
            if (medic == null) return;
            bool log = Plugin.C.DebugLogging.Value;

            if (_clip == Clip.Started)
            {
                if (log) Plugin.L.LogInfo($"[anim] {_kind}: bandage clip confirmed running");
                _animMedic = null;
                return;
            }
            if (_clip == Clip.Unknown)
            {
                bool playing = Read(() => GameFpsPlaying(medic), true);   // (an unknown never triggers a second clip over a running one)
                if (log) Plugin.L.LogInfo($"[anim] {_kind}: unconfirmed clip, first-person playing={playing}");
                if (playing) { _animMedic = null; return; }
            }

            // Refused / not playing: (re)try the bandage clip while there's still time to show it.
            if (_clipRetries > 0 && _endTime - Time.time > 1f)
            {
                _clipRetries--;
                bool refund = _kind != ActionKind.Bandage;   // only the real Bandage action pays its bandage
                _clip = Safe("retry PlayBandage", () => PlayBandage(medic, refund)) ? Clip.Started : Clip.NotStarted;
                if (!refund && _clip == Clip.Started) _bandagePaid = true;
                if (log) Plugin.L.LogInfo($"[anim] {_kind}: bandage clip retry -> {_clip} ({_clipRetries} left)");
                _animCheckAt = Time.time + ClipRetryEvery;   // come back to confirm, or retry again
                return;
            }

            _animMedic = null;
            if (log) Plugin.L.LogWarning($"[anim] {_kind}: no clip could be started - playing the bare syringe clip as a last resort");
            try { GamePlaySyringeAnim(medic); } catch (Exception e) { if (log) Plugin.L.LogWarning("[anim] PlaySyringeAnimation threw: " + e.Message); }
        }

        /// <summary>" (apply)" / " (remove)" for a tourniquet log line, empty otherwise.</summary>
        private static string TourniquetNote(Soldier patient, BodyPartType part, ActionKind kind)
        {
            if (kind != ActionKind.Tourniquet) return "";
            var w = WoundState.Peek(patient);
            return w != null && w.HasTourniquet(part) ? " (remove)" : " (apply)";
        }

        public static void Tick()
        {
            if (!Active) return;
            VerifyAnim();
            if (Time.time < _endTime) return;

            Active = false;
            MedicUI.Hidden = false;
            // The treatment lands only if it still can: the patient may have died, been carried off or
            // walked away, or the medic may have been knocked down, during the clip.
            string broken = Interrupted(_patient);
            if (broken == null && _kind == ActionKind.Bandage && !_bandagePaid) broken = PayBandageNow();
            if (broken == null && _kind == ActionKind.Aspirin) broken = AspirinLeft();
            if (broken == null)
            {
                try { Treatment.Apply(_patient, _part, _kind); }
                catch (Exception e) { Guard.Once("proc apply " + _kind, e); }
            }
            else
            {
                MedicUI.SetStatus(broken);
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[proc] {_kind} not applied: {broken}");
            }
            RestoreAnimItems();   // give back whatever the animation clip spent (except a Bandage cost)
            _patient = null;
        }

        // Whether this Bandage action's bandage was spent by its clip. A refused clip (on a vehicle, a
        // special-anim lock that outlasted the retries) spends nothing, and the dressing used to be
        // applied anyway - a free, infinite bandage.
        private static bool _bandagePaid;

        /// <summary>Charge a Bandage action whose clip never played: take one from the medic now, or
        /// refuse the dressing if they have none left. Null = paid.</summary>
        private static string PayBandageNow()
        {
            var medic = Interop.Player();
            string id = Plugin.C.IdBandage.Value;
            if (medic == null || Items.CountOf(medic, id) <= 0) return "No bandage left - nothing applied";
            Items.Consume(medic, id);
            _bandagePaid = true;
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo("[proc] bandage clip never played - charged the bandage directly");
            return null;
        }

        /// <summary>The aspirin is taken from the medic when the procedure COMPLETES (Treatment.DoAspirin).
        /// If the last one went during the clip (given away, used by another action), the dose used to land
        /// anyway with nothing consumed - a free aspirin. Null = there is one to take (or the id is blank:
        /// a free action by config).</summary>
        private static string AspirinLeft()
        {
            string id = Plugin.C.IdAspirin.Value;
            if (string.IsNullOrEmpty(id)) return null;
            var medic = Interop.Player();
            if (medic == null || Items.CountOf(medic, id) <= 0) return "No aspirin left - nothing applied";
            return null;
        }

        /// <summary>Why a finished procedure can no longer take effect, or null if it can.</summary>
        private const float ReachSlack = 1.5f;   // a little room for shuffling during the clip

        private static string Interrupted(Soldier patient)
        {
            var medic = Interop.Player();
            if (medic == null || patient == null) return "Treatment interrupted";
            if (Interop.IsDead(patient)) return "The patient died - treatment interrupted";
            if (Interop.IsDowned(medic)) return "You were knocked down - treatment interrupted";
            if (!Interop.SamePtr(medic, patient))
            {
                float reach = Mathf.Max(1f, Plugin.C.TreatRange.Value) + ReachSlack;
                if (!Interop.WithinRange(patient, medic, reach, unknown: true))
                    return "The patient moved out of reach - treatment interrupted";
            }
            return null;
        }

        // Interrupted (scene change / death): the medic may be a freed pointer, so DON'T touch its
        // inventory — just drop the snapshot.
        public static void Cancel() { Active = false; MedicUI.Hidden = false; _patient = null; _restoreOn = null; _restoreSnap = null; _animMedic = null; _animCheckAt = 0f; _clip = Clip.Unknown; _clipRetries = 0; _bandagePaid = false; _vanillaRevive = false; }


        // ---- item preservation (deferred-consume safe) ----
        private static Soldier _restoreOn;
        private static (string id, int before)[] _restoreSnap;

        private static (string, int)[] SnapshotAnimItems(Soldier medic, ActionKind kind)
        {
            var c = Plugin.C;
            // Reusable tools only — the bandage is pre-paid in PlayBandage, so it's not tracked here.
            return new[]
            {
                (c.IdSyringe.Value,  Items.CountOf(medic, c.IdSyringe.Value)),
                (c.IdForceps.Value,  Items.CountOf(medic, c.IdForceps.Value)),
                (c.IdScissors.Value, Items.CountOf(medic, c.IdScissors.Value)),
            };
        }

        private static void RestoreAnimItems()
        {
            var medic = _restoreOn; var snap = _restoreSnap;
            _restoreOn = null; _restoreSnap = null;
            if (medic == null || snap == null) return;
            foreach (var (id, before) in snap) Items.RestoreTo(medic, id, before);
        }

        /// <summary>Suppress the vanilla revive during any procedure (wake-up is roll-based).</summary>
        public static bool ShouldBlockRevive(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase c)
        {
            // ...except the one revive this procedure started on purpose (PlaySyringe's vanilla revive).
            return Active && !_vanillaRevive && _patient != null && Interop.SamePtr(c, _patient);
        }

        // This procedure deliberately started the game's networked revive (VanillaReviveReason).
        private static bool _vanillaRevive;

        /// <summary>
        /// Plays a visible first-person animation for EVERY action: the syringe clip for the
        /// syringe-type tools (syringe / splinter forceps / scissors), the self-bandage clip for the
        /// bandage and for the custom actions that have no clip of their own (aspirin / stitch / IV /
        /// diagnose / CPR). Each action's real item cost is charged in Treatment; whatever a clip
        /// spends on its own is pre-paid (bandage) or restored at completion (tools).
        /// </summary>
        private static Clip PlayHealAnim(Soldier medic, ActionKind kind, Soldier patient)
        {
            var c = Plugin.C;
            // PlaySyringe goes through Safe too: it reaches several game members, and one vanishing in an
            // update fails the WHOLE method at JIT time - which, called bare, threw out of Start.
            switch (kind)
            {
                case ActionKind.Syringe:  if (Safe("PlaySyringe", () => PlaySyringe(medic, patient, c.IdSyringe.Value, kind)))  return Clip.Unknown; break;
                case ActionKind.Forceps:  if (Safe("PlaySyringe", () => PlaySyringe(medic, patient, c.IdForceps.Value, kind)))  return Clip.Unknown; break;
                case ActionKind.Scissors: if (Safe("PlaySyringe", () => PlaySyringe(medic, patient, c.IdScissors.Value, kind))) return Clip.Unknown; break;
                // Bandage plays the self-heal clip and spends the bandage (its cost).
                case ActionKind.Bandage:
                    return Safe("PlayBandage", () => PlayBandage(medic, refund: false)) ? Clip.Started : Clip.NotStarted;
                // Tourniquet (apply AND remove) / Aspirin / Stitch / IV / Diagnose / CPR / Splint have no clip
                // of their own → self-heal clip, animation-only (the bandage it spends is pre-paid).
                default:
                    return Safe("PlayBandage", () => PlayBandage(medic, refund: true)) ? Clip.Started : Clip.NotStarted;
            }

            // The syringe clip failed outright: the self-heal clip (VerifyAnim retries it / falls back further).
            return Safe("PlayBandage", () => PlayBandage(medic, refund: true)) ? Clip.Started : Clip.NotStarted;
        }

        private static bool Safe(string what, Func<bool> f)
        {
            try { return f(); }
            catch (Exception e)
            {
                // A heal clip entry point throwing is a real failure (a game member gone): report it once.
                Guard.Once("anim " + what, e);
                return false;
            }
        }

        /// <summary>
        /// Play the syringe FP clip for a syringe-type tool. This used to fall straight through to the
        /// bandage clip whenever the carried item didn't cast to VirtualSyringe — silently, so every tool
        /// looked like a bandage. Now it tries, in order: the carried item → a throwaway VirtualSyringe
        /// built from the id (not in any inventory, so nothing real is spent) → the bare
        /// PlaySyringeAnimation clip. Only a total failure returns false (→ bandage clip). Each step is
        /// logged under DebugLogging so a miss says exactly which step broke. Any carried tool the call
        /// consumes is restored at procedure completion (SnapshotAnimItems).
        ///
        /// NEVER UseSyrynge "for the animation": UseSyrynge IS the vanilla networked revive (UseSyryngeCR,
        /// disassembled: SetIncapacitated(false) = life 25, SetBleeding(false), SurrenderSynched on an
        /// enemy, then patient.TrySyncDamage sends the 25 to the owner). Forceps and scissors used to revive
        /// any downed soldier this way, and on a modded host the host blocked it while the medic's copy
        /// stood up (a permanent desync). Only the Syringe action may revive with it, and only where the
        /// vanilla revive IS the right path (VanillaReviveReason); everything else is the bare clip.
        /// </summary>
        private static bool PlaySyringe(Soldier medic, Soldier patient, string id, ActionKind kind)
        {
            bool log = Plugin.C.DebugLogging.Value;

            VirtualItem carried = null;
            try { var inv = Items.Container(medic); if (inv != null) carried = inv.FindItemWithID(id); } catch { }
            var vs = Interop.CastTo<VirtualSyringe>(carried);
            string how = "carried";

            if (vs == null)
            {
                try { vs = Interop.CastTo<VirtualSyringe>(GameCreateItem(id)); } catch { vs = null; }
                how = "throwaway";
            }

            if (log)
            {
                string ty = null; try { ty = carried?.GetIl2CppType().Name; } catch { }
                string anim = null; try { anim = vs != null ? GameSyringeAnimName(vs) : null; } catch { }
                Plugin.L.LogInfo($"[anim] '{id}': carried={(carried == null ? "none" : ty ?? "?")} " +
                                 $"syringe={(vs == null ? "no" : how)} animName='{anim}' self={Interop.SamePtr(medic, patient)}");
            }

            // The deliberate vanilla revive: the Syringe action only, on a downed soldier someone else owns,
            // where no machine running our wake-up roll will decide it (see VanillaReviveReason).
            bool self = Interop.SamePtr(medic, patient);
            bool patientDown = !self && Interop.IsDowned(patient);
            string revive = kind == ActionKind.Syringe && patientDown ? VanillaReviveReason(patient) : null;
            if (log && kind == ActionKind.Syringe && patientDown)
                Plugin.L.LogInfo($"[mp] syringe on a downed patient -> {(revive != null ? "VANILLA networked revive (" + revive + ")" : "bare clip; " + OwnReviveNote(patient))}");
            if (vs != null && revive != null)
            {
                try { _vanillaRevive = true; GameUseSyrynge(medic, vs, patient); return true; }   // also sends the vanilla SyncSyringe (TPS clip)
                catch (Exception e) { _vanillaRevive = false; if (log) Plugin.L.LogWarning($"[anim] UseSyrynge('{id}') threw: {e.Message}"); }
            }

            try
            {
                float len = GamePlaySyringeAnim(medic);
                if (log) Plugin.L.LogInfo($"[anim] '{id}': bare PlaySyringeAnimation len={len:0.00}");
                // The others see the third-person clip, as vanilla's UseSyryngeCR shows it. Only with a
                // carried item: its id is then known to be real (receivers look it up in the item DB).
                if (carried != null) NetLife.SendSyringeTps(medic, id);
                return true;
            }
            catch (Exception e)
            {
                if (log) Plugin.L.LogWarning($"[anim] PlaySyringeAnimation threw: {e.Message} — using the bandage clip");
                return false;
            }
        }

        /// <summary>Why the Syringe action on this DOWNED patient should use the vanilla networked revive, or
        /// null when our own owner-authoritative path decides instead:
        /// - single player / a soldier we own: null - our wake-up roll (Vitals, boosted by the dose) on this
        ///   machine stands them up, and Vitals.Wake broadcasts it (MpSync + the vanilla life RPC). (If our
        ///   downed model doesn't manage the soldier at all, the game's revive, as before.)
        /// - another PLAYER's avatar (any host): vanilla - an MP human's own machine only runs the blood-loss
        ///   rules (VitalsControl.BleedOut), and it takes the game's revive (the life RPC) as a revive; it
        ///   is what a vanilla medic does.
        /// - an AI on a MODDED host: null - the epinephrine goes to the host (MpSync.SendTreat), whose
        ///   wake roll decides; a vanilla revive there was blocked by the host and desynced our copy.
        /// - an AI on an UNMODDED host: vanilla - the host has no wake roll, only the game's revive.</summary>
        private static string VanillaReviveReason(Soldier patient)
        {
            if (!Interop.InMpRoom() || Interop.HasAuthority(patient))
                // Ours: our wake roll - unless our downed model doesn't manage this soldier at all
                // (DownedSurviveMode), where nothing else would ever stand them up.
                return Vitals.OwnsState(patient) ? null : "our downed model doesn't manage this soldier";
            if (Interop.IsOtherHuman(patient)) return "downed player: a medic's revive of a human is the game's in MP";
            if (MpSync.HostSendsWounds) return null;
            return "unmodded host: the game's revive is the only one";
        }

        private static string OwnReviveNote(Soldier patient)
        {
            if (!Interop.InMpRoom() || Interop.HasAuthority(patient)) return "our wake-up roll decides here";
            return "epinephrine sent to the host, whose wake-up roll decides";
        }

        /// <summary>Play the self-heal (bandage) FP clip. For an animation-only use (refund:true — tourniquet, aspirin,
        /// stitch, IV, CPR, diagnose) the bandage the clip will consume is PRE-PAID here (GiveOne before
        /// UseBandages), so the deferred consume just claws it back to net zero — timing-independent, unlike
        /// a post-restore that races the clip. Only the real Bandage action (refund:false) pays its bandage
        /// as its cost.
        ///
        /// Returns whether the clip really STARTED. Soldier.UseBandages returns void and silently does
        /// nothing unless every one of its gates passes (read off the native code): CanPlaySpecialAnim
        /// (alive, no special-anim lock running, no other blocking state), CanUseStackable (the item is in
        /// the medic's inventory AND its GetStackCount() > 0), isBleeding, alive. The same gates are
        /// checked here first so a refusal is logged with its reason; the start itself is confirmed by the
        /// coroutine taking the special-anim lock (CanPlaySpecialAnim flips to false). A refused clip
        /// takes its pre-payment back, so no bandage is ever gained or lost by an animation.</summary>
        internal static bool PlayBandage(Soldier medic, bool refund)
        {
            bool log = Plugin.C.DebugLogging.Value;
            string id = Plugin.C.IdBandage.Value;
            int carried = log ? Items.CountOf(medic, id) : 0;
            if (refund) Items.GiveOne(medic, id);   // pre-pay the deferred consume (net zero); works even with none carried
            var vb = Items.BandagesById(medic, id) ?? Items.Bandages(medic);
            if (vb == null)
            {
                if (log) Plugin.L.LogWarning($"[anim] bandage clip: no '{id}' item to play it with (carried={carried}, refund={refund})");
                return false;   // nothing was found under the id, so there is no pre-payment to take back
            }
            // UseBandages refuses a soldier that isn't bleeding. Remember the flag: a refused clip must not
            // leave the medic "bleeding" in the game's eyes.
            bool wasBleeding = Read(() => GameIsBleeding(medic), false);
            try { GameSetBleeding(medic, true); } catch { }

            string blocked = BandageClipBlocker(medic, vb);
            if (log)
                Plugin.L.LogInfo($"[anim] bandage clip: carried={carried} -> {Items.CountOf(medic, id)} (refund={refund}) " +
                                 $"stack={Items.StackOf(vb)} gate={blocked ?? "ok"}");
            if (blocked != null)
            {
                if (refund) Items.Consume(medic, id);   // take the pre-payment back: nothing will spend it
                if (!wasBleeding) { try { GameSetBleeding(medic, false); } catch { } }
                return false;
            }

            GameUseBandages(medic, vb);
            bool started = SpecialAnimLocked(medic);
            if (log) Plugin.L.LogInfo($"[anim] bandage clip: UseBandages called -> started={started}");
            if (!started)
            {
                if (refund) Items.Consume(medic, id);
                if (!wasBleeding) { try { GameSetBleeding(medic, false); } catch { } }
            }
            return started;
        }

        /// <summary>Why Soldier.UseBandages would refuse this call right now, or null if it will play.
        /// Mirrors its native gates (see PlayBandage); the vehicle check is ours: on a vehicle the game
        /// heals without any clip, which would read as "not started" and double-refund.</summary>
        private static string BandageClipBlocker(Soldier medic, VirtualBandages vb)
        {
            // An unreadable gate (renamed member) reads as "passes": the call is still tried and its
            // start is still confirmed afterwards, so a broken read only costs the reason text.
            if (!Read(() => GameIsAlive(medic), true))               return "medic not alive (downed?)";
            if (Interop.IsOnVehicle(medic))                          return "medic is on a vehicle (the game heals there without a clip)";
            if (!Read(() => GameCanPlaySpecialAnim(medic), true))    return "CanPlaySpecialAnim=false (another clip / weapon switch still locking)";
            if (!Read(() => GameCanUseStackable(medic, vb), true))   return $"CanUseStackable=false (item not in the inventory or stack={Items.StackOf(vb)})";
            if (!Read(() => GameIsBleeding(medic), true))            return "isBleeding=false after SetBleeding(true)";
            return null;
        }

        /// <summary>UseBandages' coroutine takes the special-anim lock synchronously when it starts the
        /// first-person clip, so right after the call "can't play a special anim" means "it started".
        /// Unreadable: assume started, so a pre-payment is never taken back twice.</summary>
        private static bool SpecialAnimLocked(Soldier medic) => !Read(() => GameCanPlaySpecialAnim(medic), false);

        /// <summary>Runs a game read inside a try. The read lives in its own method (below), so a member
        /// a game update removed fails when THAT method is JIT-compiled — inside this try — instead of
        /// JIT-killing PlayBandage as a whole (see the game-update playbook).</summary>
        private static bool Read(Func<bool> f, bool fallback) { try { return f(); } catch { return fallback; } }

        // Every game member this class touches lives in one of these, NoInlining so the JIT can't fold
        // it back into a caller: a member removed by an update then fails only here, inside the
        // caller's try, instead of refusing the whole caller (the game-update playbook's JIT trap).
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool GameIsAlive(Soldier s)            => s.IsAlive;
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool GameCanPlaySpecialAnim(Soldier s) => s.CanPlaySpecialAnim();
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool GameIsBleeding(Soldier s)         => s.isBleeding;
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool GameCanUseStackable(Soldier s, VirtualItemStackable it) => s.CanUseStackable(it);
        [MethodImpl(MethodImplOptions.NoInlining)] private static void GameSetBleeding(Soldier s, bool on) => s.SetBleeding(on);
        [MethodImpl(MethodImplOptions.NoInlining)] private static void GameUseBandages(Soldier s, VirtualBandages vb) => s.UseBandages(vb);
        [MethodImpl(MethodImplOptions.NoInlining)] private static void GameUseSyrynge(Soldier s, VirtualSyringe vs, Soldier patient) => s.UseSyrynge(vs, patient, true);
        [MethodImpl(MethodImplOptions.NoInlining)] private static float GamePlaySyringeAnim(Soldier s)   => s.PlaySyringeAnimation();
        [MethodImpl(MethodImplOptions.NoInlining)] private static string GameSyringeAnimName(VirtualSyringe vs) => vs.GetSyringeAnimName();
        [MethodImpl(MethodImplOptions.NoInlining)] private static VirtualItem GameCreateItem(string id)  => VirtualItem.Create(id);
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool GameFpsPlaying(Soldier medic)
        {
            var fps = medic.fpsGun;
            return fps == null || fps.IsPlayingAnim();
        }

        private static float DurationFor(ActionKind kind, Soldier patient, BodyPartType part)
        {
            var c = Plugin.C;
            switch (kind)
            {
                case ActionKind.Diagnose: return Random.Range(c.DiagnoseTimeMin.Value, c.DiagnoseTimeMax.Value);
                case ActionKind.CPR:      return Random.Range(c.CprTimeMin.Value, c.CprTimeMax.Value);
                case ActionKind.Stitch:   return StitchDuration(patient, part);
                default:                  return c.ProcedureTime.Value;   // bandage/forceps/scissors/syringe/aspirin/IV
            }
        }

        private static float StitchDuration(Soldier patient, BodyPartType part)
        {
            var c = Plugin.C;
            // The per-wound increase applies only to MULTIPLE bandaged wounds on the SAME limb being
            // stitched — not wounds spread across different limbs. Overall duration cut by 40%.
            var w = WoundState.Peek(patient);
            int bandaged = Mathf.Max(1, w != null ? w.CountBandaged(part) : 0);
            return (c.StitchTimeBase.Value + c.StitchTimePerExtra.Value * (bandaged - 1)) * StitchTimeFactor;
        }
    }
}
