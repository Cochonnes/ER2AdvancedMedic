using System;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>Vanilla AI heals (self-bandage, AI-medic syringe revive) and the downed / revive gate
    /// (Soldier.SetIncapacitated).</summary>
    public static partial class Hooks
    {
        // ----- Vanilla AI heal detection: when a NON-player soldier uses bandages/syringe (its own
        // AI self-care or an AI medic), treat it like OUR bandage: every open bleeding wound becomes
        // bandaged (can reopen, still stitchable). Fractures, blood loss and pain stay — an AI patching
        // itself up doesn't un-break a leg. (This used to wipe the whole record, so an AI you'd just
        // shot showed "No wounds" in J a second later while the game still drew the wound.)
        // Player heals go through our UI, so ignore them.
        //
        // TIMING MATTERS: these postfixes fire when the game STARTS its bandage/syringe clip. Its clip
        // needs the patient to stay "bleeding" until it finishes (it aborts otherwise, and the game only
        // takes the bandage out of the inventory at the END). Touching the bleeding flag here killed the
        // AI's animation and left the bandage unused, so we only note the heal now and apply it to our
        // record once the game has finished it (see TickAiHeals).
        private struct PendingHeal { public Soldier S; public float Start; public bool Revive; }
        private static readonly System.Collections.Generic.Dictionary<IntPtr, PendingHeal> _aiHeals =
            new System.Collections.Generic.Dictionary<IntPtr, PendingHeal>();
        private static readonly System.Collections.Generic.List<IntPtr> _aiHealDone = new System.Collections.Generic.List<IntPtr>();

        private const float AiBandageMaxWait = 15f;   // longer than any bandage clip; later = interrupted
        private const float AiReviveDelay = 5f;       // roughly the syringe clip
        private const float AiBandageMinClip = 1f;    // a bleeding flag cleared sooner than this isn't the clip ending

        private static void QueueAiHeal(Soldier s, bool revive)
        {
            var p = Interop.PtrOf(s);
            if (p == IntPtr.Zero || WoundState.Peek(s) == null) return;
            // Never let a later bandage overwrite a pending medic REVIVE: the revive is what stands the
            // patient up, and losing it left a soldier the game had revived still lying downed.
            if (!revive && _aiHeals.TryGetValue(p, out var old) && old.Revive) return;
            _aiHeals[p] = new PendingHeal { S = s, Start = Time.time, Revive = revive };
        }

        /// <summary>An AI medic's vanilla syringe revive has started on this soldier.</summary>
        public static bool RevivePending(Soldier s)
        {
            var p = Interop.PtrOf(s);
            return p != IntPtr.Zero && _aiHeals.TryGetValue(p, out var h) && h.Revive;
        }

        /// <summary>Per-tick: apply AI heals whose game clip has finished.</summary>
        public static void TickAiHeals()
        {
            if (_aiHeals.Count == 0) return;
            float now = Time.time;
            _aiHealDone.Clear();
            foreach (var kv in _aiHeals)
            {
                var h = kv.Value;
                float t = now - h.Start;
                if (Interop.IsDead(h.S)) { _aiHealDone.Add(kv.Key); continue; }
                if (h.Revive)
                {
                    if (t < AiReviveDelay) continue;
                    AiBandaged(h.S, "medic syringe");
                    if (Interop.IsDowned(h.S) && Vitals.OwnsState(h.S))
                    {
                        var pw = WoundState.Peek(h.S);
                        if (pw != null && pw.Arrested) Vitals.CprSuccess(pw);   // restarted heart
                        Vitals.Wake(h.S);
                    }
                    _aiHealDone.Add(kv.Key);
                    continue;
                }
                // Bandage: done once the game has cleared the bleeding flag itself (clip finished).
                bool bleeding = Interop.IsBleeding(h.S, fallback: true);
                if (!bleeding && t > AiBandageMinClip) { AiBandaged(h.S, "self-bandage"); _aiHealDone.Add(kv.Key); }
                else if (t > AiBandageMaxWait)
                {
                    if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo("[vitals] AI bandage interrupted - nothing applied");
                    _aiHealDone.Add(kv.Key);
                }
            }
            foreach (var p in _aiHealDone) _aiHeals.Remove(p);
        }

        public static void ClearAiHeals() { _aiHeals.Clear(); }

        /// <summary>A soldier is gone (SoldierRegistry): forget its pending heal.</summary>
        public static void ForgetAiHeal(IntPtr p) { if (p != IntPtr.Zero) _aiHeals.Remove(p); }

        private static void AiBandaged(Soldier s, string why)
        {
            var w = WoundState.Peek(s);
            if (w == null) return;
            int n = MedicCall.BandageAll(s, w);
            // Only the soldier's authority broadcasts it: on a client this is our local copy of a heal
            // the host sees (and sends) itself, and a client's snapshot carries its own, possibly stale,
            // view of the soldier.
            if (Interop.HasAuthority(s)) MpSync.Send(s);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[vitals] AI {why} finished: bandaged {n} bleeding wound(s), rest kept");
        }

        public static void UseBandages_Post(Soldier __instance)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return;
                if (Interop.IsPlayer(__instance))
                    BandageClipUntil = Time.time + Mathf.Max(0f, Plugin.C.BandagePromptHideSeconds.Value);   // cover the [B] hold-prompt for the clip
                else if (!Interop.IsOtherHuman(__instance))   // another player's heal is theirs to sync
                    QueueAiHeal(__instance, revive: false);   // applied when the game's clip has finished
            }
            catch (Exception e) { Fail("UseBandages", e); }
        }

        public static void UseSyrynge_Post(Soldier __instance, VirtualSyringe __0, Creature __1)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return;
                // Players use our UI, where a syringe never revives by itself; another player's syringe is
                // their own client's treatment. Only an AI medic's syringe is the vanilla revive.
                if (Interop.IsPlayer(__instance) || Interop.IsOtherHuman(__instance)) return;
                var patient = Interop.CastTo<Soldier>(__1) ?? __instance;    // AI medic heals __1 (or self)
                // The vanilla syringe is the game's revive: once its clip has played, bandage the patient
                // in our record and stand them up.
                QueueAiHeal(patient, revive: true);
            }
            catch (Exception e) { Fail("UseSyrynge", e); }
        }

        // ----- Soldier.SetIncapacitated(bool) (prefix): suppress the revive during a non-syringe
        // treatment so the patient never stands up (no stand-then-fall jank). -----
        // Set by the prefix when it lets an AI medic's vanilla revive through, read by the postfix.
        private static IntPtr _vanillaRevivePtr;

        public static bool SetIncapacitated_Pre(Soldier __instance, bool __0)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return true;
                if (__0) return true;   // going down — always allow

                // Only the soldier's AUTHORITY (SP: us; MP: the host for AI, the owner for a player) may
                // ever block a revive. Anywhere else this call is the game mirroring the authority - the
                // host's life update standing a revived AI back up. Blocking it on a client (our procedure
                // on that AI, or a downed stamp) left the client's copy lying in the downed animation
                // while the host ran the AI around shooting, and nothing ever re-synced it.
                if (!Interop.HasAuthority(__instance)) return true;

                var ptr = Interop.PtrOf(__instance);
                if (Vitals.WakingPtr != IntPtr.Zero && Vitals.WakingPtr == ptr)
                    return true;        // our intentional wake — let it through

                // An AI medic's vanilla syringe revive: let the GAME's revive through whole. Blocking only
                // this call (and standing them up ourselves 5 s later) left a window - and, when a
                // self-bandage overwrote the pending revive, no end at all - in which the game had
                // revived the soldier (its AI moving and shooting again) while it still lay downed.
                if (RevivePending(__instance)) { _vanillaRevivePtr = ptr; return true; }

                // The vanilla NETWORKED revive: another machine's syringe (an unmodded client's medic on our
                // AI, a medic on our own MP avatar) arrives as SyncSoldier.UpdateSoldierLife, which writes
                // the new life FIRST and only then calls SetIncapacitated(false) for the 0 -> >0 change
                // (disassembled 2026-09-30). So a revive that finds life already above 0 is that RPC.
                // Blocking it changed nothing but the pose: the life stayed 25, IsInjured (life == 0) read
                // false, and the soldier lay in the downed animation while the game - and the reviving
                // client - had it up. Let it through and bring our state along, as for an AI medic's revive.
                if (Interop.Life(__instance) > 0) { _vanillaRevivePtr = ptr; return true; }

                if (Procedure.ShouldBlockRevive(__instance)) return false;   // block during our procedure

                // Block every other vanilla auto-revive on a downed soldier — the only ways up are our
                // stable-vitals wake-up roll and an AI medic's (vanilla) syringe revive.
                var w = WoundState.Peek(__instance);
                if (w != null && w.DownedAt > 0f && HpLogic.DownedApplies(__instance))
                    return false;
            }
            catch (Exception e) { Fail("SetIncapacitated_Pre", e); }
            return true;
        }

        // ----- Soldier.SetIncapacitated(bool) (postfix): downed stamp / clear, and the bookkeeping for a
        // vanilla revive the prefix let through -----
        public static void SetIncapacitated_Post(Soldier __instance, bool __0)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return;
                DownedLogic.OnIncapacitated(__instance, __0);
                WoundState.Peek(__instance)?.InvalidateMove();   // the Move prefix re-reads the downed flag now
                if (!__0 && _vanillaRevivePtr != IntPtr.Zero && _vanillaRevivePtr == Interop.PtrOf(__instance))
                {
                    _vanillaRevivePtr = IntPtr.Zero;
                    OnVanillaRevive(__instance);
                }
            }
            catch (Exception e) { Fail("SetIncapacitated_Post", e); }
        }

        /// <summary>The game revived a soldier we manage (an AI medic's syringe, or a networked revive from
        /// another machine): bring our state along - a stopped heart counts as restarted, or the arrest
        /// tick would drop them straight back down - and tell the other machines at once.</summary>
        private static void OnVanillaRevive(Soldier s)
        {
            if (Interop.IsDowned(s, fallback: true)) return;   // the call didn't take
            var w = WoundState.Peek(s);
            if (w != null)
            {
                if (w.Arrested) Vitals.CprSuccess(w);
                w.State = MedState.Awake; w.HighPainSince = -1f; w.CollapsedByVitals = false;
            }
            MpSync.Send(s);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo("[vitals] vanilla revive let through");
        }
    }
}
