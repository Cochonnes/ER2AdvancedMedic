using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Downed bookkeeping: stamps DownedAt when a soldier we manage goes down (the revive block, the
    /// wake-up roll and the Vitals fallback death all key off it), and - with HP/HoldVanillaDownedTimer
    /// on - holds the vanilla injured-death countdown (Soldier.injureDeathTime) far in the future, so
    /// the vitals model decides when a downed soldier dies instead of the game's short timer. The
    /// value is re-asserted at 4 Hz (it sits ~100000 s out, so a quarter second is nothing) so the
    /// game can't clamp it back.
    ///
    /// injureDeathTime's units (absolute Time.time death-stamp vs. remaining seconds) are unknown
    /// across builds. We OBSERVE the value the game sets after incapacitation and detect the units
    /// before overriding, so we never clobber it with a wrong-units value.
    /// </summary>
    internal static class DownedLogic
    {
        // null = not yet detected; true = injureDeathTime is an absolute Time.time death-stamp.
        private static bool? _absolute;

        /// <summary>Postfix of Soldier.SetIncapacitated(bool): stamp/clear the downed time.</summary>
        public static void OnIncapacitated(Soldier s, bool injure)
        {
            if (s == null) return;
            // Standing up: only an entry we already track has a stamp to clear. (Get here created a
            // tracking entry for every soldier the game ever revived or spawned standing.)
            if (!injure) { var up = WoundState.Peek(s); if (up != null) { up.DownedAt = 0f; up.CollapsedByVitals = false; } return; }

            var w = WoundState.Get(s);   // going down: always tracked (the Move prefix relies on it)
            if (w == null) return;
            // Population excluded (vanilla downed state), or not ours in MP: never stamp a copy another
            // machine runs. An MP human's own avatar (BleedOut) is stamped for its downed clock
            // (MaxDownedTime); the revive block and the timer pin still need DownedApplies, which it isn't.
            if (Vitals.ControlOf(s) == VitalsControl.None) return;

            if (w.DownedAt <= 0f) { w.DownedAt = Time.time; w.NextWakeCheck = Time.time + Mathf.Max(1f, Plugin.C.WakeCheckInterval.Value); }
            // The timer pin happens in TickAll once we've observed the game's countdown.
        }

        /// <summary>For every tracked soldier we stamped as down: clear the stamp once they are up
        /// again, and (config) re-assert the pinned vanilla countdown.
        ///
        /// The loop skips anyone not stamped before touching the game, and the Photon room state -
        /// which cannot change inside a frame - is read ONCE here rather than per soldier.</summary>
        private const float Step = 0.25f;
        private static float _next;

        public static void TickAll()
        {
            float now = Time.time;
            if (now < _next) return;
            _next = now + Step;
            bool inRoom = Interop.InMpRoom();
            bool pin = Plugin.C.HoldVanillaDownedTimer.Value;

            foreach (var kv in WoundState.All())
            {
                var w = kv.Value;
                if (w.DownedAt <= 0f) continue;      // cheapest test first: most tracked soldiers are up
                var s = w.Owner;
                if (s == null) continue;
                TickOne(s, w, now, pin && HpLogic.DownedApplies(s, inRoom) && Interop.HasAuthority(s));
            }
        }

        /// <summary>Clear the stamp of a soldier who is up again, then (pin) detect the units once and
        /// re-assert the held countdown for ONE downed soldier.</summary>
        public static void TickOne(Soldier s, SoldierWounds w, float now, bool pin)
        {
            if (!Interop.IsDowned(s)) { w.DownedAt = 0f; w.CollapsedByVitals = false; return; }   // stood back up / healed
            if (!pin || _deathTimeDead) return;

            if (!TryReadDeathTime(s, out float cur)) return;

            // Detect units from the game's own countdown before touching it.
            if (_absolute == null)
            {
                if (cur > now + 1f) _absolute = true;      // future timestamp ⇒ absolute
                else if (cur > 0.1f) _absolute = false;    // small positive ⇒ remaining seconds
                else
                {
                    // Game hasn't set a countdown yet (or uses a different mechanism). Warn
                    // once if it stays 0 well past going down, then leave it alone this frame.
                    if (Plugin.C.DebugLogging.Value && now - w.DownedAt > 2f)
                        Plugin.L.LogWarning($"[downed] injureDeathTime still 0 after {now - w.DownedAt:0.0}s — it may not drive injured-death on this build.");
                    return;
                }
                if (Plugin.C.DebugLogging.Value)
                    Plugin.L.LogInfo($"[downed] injureDeathTime units = {(_absolute.Value ? "absolute" : "remaining")} (cur={cur:0.0} now={now:0.0})");
            }

            // Pin the vanilla countdown far into the future — the Vitals system now decides
            // death (no pulse / empty blood / fallback max time), not the vanilla timer.
            float value = _absolute.Value ? now + 100000f : 100000f;
            try { WriteDeathTimeRaw(s, value); }
            catch (Exception e) { LatchIfGone(e); }
        }

        // injureDeathTime lives in its own methods (JIT trap): the field vanishing in an update used to
        // fail TickOne as a whole, and with it the stamp clearing for every soldier who stood back up.
        // A missing member latches the pin off for the session; anything else costs only this call.
        private static bool _deathTimeDead;

        private static bool TryReadDeathTime(Soldier s, out float v)
        {
            v = 0f;
            try { v = ReadDeathTimeRaw(s); return true; }
            catch (Exception e) { LatchIfGone(e); return false; }
        }

        private static void LatchIfGone(Exception e)
        {
            if (_deathTimeDead || !(e is MissingMemberException || e is TypeLoadException)) return;
            _deathTimeDead = true;
            Plugin.L.LogError("[downed] Soldier.injureDeathTime unavailable - the vanilla downed timer is no longer held (HP/HoldVanillaDownedTimer): " + e.Message);
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static float ReadDeathTimeRaw(Soldier s) => s.injureDeathTime;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void WriteDeathTimeRaw(Soldier s, float v) => s.injureDeathTime = v;
    }
}
