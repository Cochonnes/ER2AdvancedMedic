using System;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Immediate per-limb physical reactions to a hit: leg/chest knockdown rolls (Phase 7).
    /// The persistent effects (arm intensity floor, walk slow) are computed each frame in
    /// Effects from the active wound set; this handles only the one-shot reactions at hit time.
    /// </summary>
    internal static class LimbEffects
    {
        public static void OnHit(Soldier s, BodyPartType part)
        {
            var c = Plugin.C;
            if (!c.EnableHitKnockdown.Value) return;   // crouch-only by default (a prone snap spun the head)

            // Only on a soldier this machine owns: a pose forced on someone else's soldier (the host on a
            // client's avatar, a client on a host-run AI) fights the owner's own pose and snaps back.
            if (!Interop.HasAuthority(s)) return;

            var w = WoundState.Peek(s);
            if (w == null) return;

            // Only knock down a soldier who is currently STANDING. If they're already kneeling
            // (Crouch) or lying down (Prone) there's nothing to drop — and forcing a crouch on a
            // prone soldier would pop them UP, the opposite of a knockdown.
            // (Pose/SetPose go through Interop: game members, see its JIT-trap note.)
            if (Interop.TryGetPose(s, out var pose) && pose != SoldierPose.Idle) return;

            // Leg or chest hit: one roll for the reaction pose. Prone (rarer, more severe) takes
            // priority, then crouch. Single random so the two chances don't stack past 100%.
            switch (part)
            {
                case BodyPartType.leg_l:
                case BodyPartType.leg_r:
                case BodyPartType.chest:
                    float r = UnityEngine.Random.value;
                    if (r < c.HitProneChance.Value)
                        Knockdown(s, w, SoldierPose.Prone);
                    else if (r < c.HitProneChance.Value + c.HitCrouchChance.Value)
                        Knockdown(s, w, SoldierPose.Crouch);
                    break;
            }
        }

        /// <summary>
        /// Brief knockdown: force the pose and hold it for KnockdownLock seconds so the input
        /// loop doesn't immediately pop the soldier back up. After the lock the player stands on
        /// their own (Phase 7 enforces the lock in the pose/move hooks).
        /// </summary>
        private static void Knockdown(Soldier s, SoldierWounds w, SoldierPose pose)
        {
            Interop.SetPose(s, pose);
            w.PoseLockPose = pose;
            w.PoseLockUntil = Time.time + Plugin.C.KnockdownLock.Value;
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[knockdown] pose={pose}");
        }
    }
}
