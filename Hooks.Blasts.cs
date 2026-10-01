using System;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>Damage-classification scopes (bullet / explosion / fire) and the grenade proximity check.</summary>
    public static partial class Hooks
    {
        // ----- Bullet.BulletDamage (prefix/postfix): the bullet scope, a depth counter like the explosion
        // one. BulletScopeInstalled is set by Plugin once BOTH halves patched; without it every unscoped
        // hit keeps the old bullet classification. -----
        internal static bool BulletScopeInstalled;
        private static int _bulletDepth;
        public static void Bullet_Pre() { _bulletDepth++; WoundLogic.InBullet = true; }
        public static void Bullet_Post() { if (--_bulletDepth <= 0) { _bulletDepth = 0; WoundLogic.InBullet = false; } }

        // ----- Explosion scopes: flag blast damage so hits reaching us via the plain Damage path are
        // classified as explosions (→ shrapnel, blast multiplier) or, for a fire-typed blast, as FIRE
        // (burns, ordinary damage rules), not bullets. Depth counters so nested scopes (CreateExplosion →
        // HitSoldierPart → TryDamageWithExplosion) keep the flag set until the OUTERMOST one returns.
        // HitSoldierPart carries no HitType (counts as explosion); CreateExplosion and
        // TryDamageWithExplosion do, and a fire-typed one wins over an untyped scope nested in it. -----
        private static int _explDepth, _fireDepth;

        /// <summary>Counts blasts (outermost Explosion-typed CreateExplosion). The damage path stamps the
        /// local player with it when it already resolved that blast, so the proximity check in
        /// CreateExplosion_Post doesn't roll a second insta-kill or add a second set of wounds.</summary>
        internal static int BlastSeq;
        internal static int PlayerBlastRolledSeq = -1, PlayerBlastWoundSeq = -1;

        private static void SyncBlastFlags()
        {
            WoundLogic.InFire = _fireDepth > 0;
            WoundLogic.InExplosion = _explDepth > 0 && _fireDepth == 0;
        }

        private static void EnterBlast(bool fire)
        {
            if (fire) _fireDepth++;
            else { if (_explDepth == 0 && _fireDepth == 0) BlastSeq++; _explDepth++; }
            SyncBlastFlags();
        }

        private static void LeaveBlast(bool fire)
        {
            if (fire) { if (_fireDepth > 0) _fireDepth--; }
            else if (_explDepth > 0) _explDepth--;
            SyncBlastFlags();
        }

        // HitSoldierPart (untyped)
        public static void Explosion_Pre() { EnterBlast(false); }
        public static void Explosion_Post() { LeaveBlast(false); }
        // Explosion.CreateExplosion(pos, maxDam, pen, radius, responsible, ignore, HitType __6, canDamage)
        public static void CreateExplosionScope_Pre(HitType __6) { EnterBlast(__6 == HitType.Fire); }
        public static void CreateExplosionScope_Post(HitType __6) { LeaveBlast(__6 == HitType.Fire); }
        // BodyPart.TryDamageWithExplosion(maxPen, dam, pos, responsible, HitType __4)
        public static void TryDamageWithExplosion_Pre(HitType __4) { EnterBlast(__4 == HitType.Fire); }
        public static void TryDamageWithExplosion_Post(HitType __4) { LeaveBlast(__4 == HitType.Fire); }

        /// <summary>Close every damage scope. The scopes are only closed by postfixes, and a postfix
        /// doesn't run when the original throws - that latched a flag for the rest of the session (every
        /// later hit read as a blast, or DamageWithAnimation's guard skipped all plain-Damage scaling).
        /// Called once a frame from PlayerController.LateUpdate, where no damage call can be open.</summary>
        internal static void ResetDamageScopes()
        {
            if (_explDepth == 0 && _fireDepth == 0 && _bulletDepth == 0 && !HpLogic.InDamageWithAnimation
                && !WoundLogic.InExplosion && !WoundLogic.InFire && !WoundLogic.InBullet) return;
            _explDepth = 0; _fireDepth = 0; _bulletDepth = 0;
            WoundLogic.InExplosion = false; WoundLogic.InFire = false; WoundLogic.InBullet = false;
            HpLogic.InDamageWithAnimation = false;
        }

        // ----- Corvostudio.Weapons.Explosion.CreateExplosion(pos, maxDam, pen, radius, responsible, ignore,
        // HitType, canDamage) (postfix): grenade lethality by PROXIMITY, for the LOCAL player, in every
        // context. The blast spawns everywhere for its visual, so we read the world position + radius here
        // and act on distance to the local player — the reliable way to make a grenade "right next to me"
        // deadly (on a client the damage is host-authoritative, and even on host/SP the player's own blast
        // damage never reaches Soldier.Damage: logged "[hitpart] PLAYER dam=200 type=Explosion" with no
        // following Soldier.Damage).
        //
        // It is a pure distance test, so it has to respect what distance can't see: a blast that deals no
        // damage (canDamage=false), a player inside a vehicle (the game's vehicle damage model protects -
        // or kills - its crew; an HE shell 4 m from a tank used to give its crew a 90% kill), and solid
        // cover between the blast and the player (walls, terrain, vehicles). -----
        public static void CreateExplosion_Post(Vector3 __0, float __3, Soldier __4, HitType __6, bool __7)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return;
                // Explosions only: a fire-typed blast never rolls the insta-kill (it burns - the damage
                // path gives burns proportional to the damage it deals).
                if (__6 != HitType.Explosion) return;
                if (!__7) return;   // a blast that can't damage anyone

                var player = Interop.Player();
                if (player == null) return;
                // The player's own soldier: this machine's rules in SP / on an unmodded host, the host's on a
                // modded one (vanilla - nothing - until they arrive). See DamageRules.For.
                var r = DamageRules.For(player);
                if (r == null || !r.GrenadeInstaKill) return;

                // The damage path may already have resolved THIS blast for the local player (host/SP,
                // when the blast's damage did reach our hooks): its own kill/down roll, its own wounds.
                // Rolling again here was a second 90% insta-kill and a second set of wounds.
                bool rolled = PlayerBlastRolledSeq == BlastSeq;
                bool wounded = PlayerBlastWoundSeq == BlastSeq;
                if (rolled && wounded) return;

                if (!Interop.TryPos(player, out var ppos)) return;

                float dist = Vector3.Distance(__0, ppos);
                float radius = Mathf.Max(0f, __3);
                float lethalR = r.GrenadeLethalRadius;
                if (dist > Mathf.Max(radius, lethalR)) return;    // out of range entirely

                if (Interop.IsOnVehicle(player)) return;

                BlastExposure(__0, player, ppos, out bool body, out bool head);
                if (!body && !head)
                {
                    if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[grenade] {dist:0.0}m but behind cover -> nothing");
                    return;
                }

                bool lethal = !rolled && dist <= lethalR && body;

                // A blast set off by a soldier ANOTHER machine owns (a remote player's grenade or shell) may
                // also be resolved for our avatar on THAT machine: if it runs the mod, HpLogic there rolls
                // the blast kill and WoundLogic there wounds us (sent as a snapshot). Rolling here as well
                // was a double roll. So wait a moment for that machine's word, and only roll if none came.
                if (Interop.InMpRoom() && __4 != null && !Interop.HasAuthority(__4))
                {
                    _blasts.Add(new PendingBlast
                    {
                        At = Time.time, Player = Interop.PtrOf(player), Dist = dist, Lethal = lethal,
                        Wound = !wounded, Chance = r.GrenadeInstaKillChance,
                    });
                    if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] grenade {dist:0.0}m from a remote thrower -> waiting {BlastDeferSeconds:0.0}s for its machine to resolve it");
                    return;
                }

                ResolveBlast(player, dist, lethal, !wounded, r.GrenadeInstaKillChance);
            }
            catch (Exception e) { Fail("CreateExplosion", e); }
        }

        /// <summary>Point-blank with the body exposed → roll the insta-kill; otherwise (still within the
        /// blast, or only the head showing) leave a wound.</summary>
        private static void ResolveBlast(Soldier player, float dist, bool lethal, bool wound, float chance)
        {
            if (lethal && UnityEngine.Random.value < chance)
            {
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogWarning($"[grenade] {dist:0.0}m -> KILL player");
                Interop.KillAuthoritative(player);
            }
            else if (wound && Plugin.C.MpClientWoundFromFlinch.Value)
            {
                WoundLogic.OnClientExplosionWound(player);
            }
        }

        // Blasts from a remote thrower, waiting for that machine's resolution (see CreateExplosion_Post).
        private struct PendingBlast { public float At; public IntPtr Player; public float Dist; public bool Lethal, Wound; public float Chance; }
        private static readonly System.Collections.Generic.List<PendingBlast> _blasts = new System.Collections.Generic.List<PendingBlast>();
        // Covers the thrower's batched snapshot (MpSync flushes every 0.25 s) plus the network round trip.
        private const float BlastDeferSeconds = 0.8f;

        /// <summary>Per tick: resolve the remote-thrower blasts whose wait is over. Skipped when the
        /// thrower's machine already resolved it (a snapshot brought new wounds for us after the blast), or
        /// we're no longer standing (it killed or downed us, or we respawned into another soldier).</summary>
        public static void TickPendingBlasts()
        {
            if (_blasts.Count == 0) return;
            float now = Time.time;
            for (int i = _blasts.Count - 1; i >= 0; i--)
            {
                var b = _blasts[i];
                if (now - b.At < BlastDeferSeconds) continue;
                _blasts.RemoveAt(i);
                var player = Interop.Player();
                string skip = null;
                if (player == null || Interop.PtrOf(player) != b.Player) skip = "player changed";
                else if (Interop.IsDead(player) || Interop.IsDowned(player)) skip = "already down/dead (resolved remotely)";
                else if (WoundLogic.LastRemotePlayerWoundTime >= b.At) skip = "the thrower's machine resolved it (its wounds arrived)";
                if (Plugin.C.DebugLogging.Value)
                    Plugin.L.LogInfo($"[mp] deferred grenade {b.Dist:0.0}m -> {(skip != null ? "skip: " + skip : "no remote resolution, rolling here")}");
                if (skip == null) ResolveBlast(player, b.Dist, b.Lethal, b.Wound, b.Chance);
            }
        }

        /// <summary>Is there a clear line from the blast to the player's body centre / head height?</summary>
        private static void BlastExposure(Vector3 blast, Soldier player, Vector3 feet, out bool body, out bool head)
        {
            var center = feet + Vector3.up * 1.0f;
            try { center = CenterRaw(player); } catch { }
            var from = blast + Vector3.up * 0.25f;   // off the ground the blast sits on
            body = LineClear(from, center);
            head = LineClear(from, center + Vector3.up * 0.6f);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Vector3 CenterRaw(Soldier s) => s.GetCenterOfUnit();

        /// <summary>No solid, non-soldier collider between two points. Soldiers (the player included) are
        /// not cover; anything else - walls, terrain, vehicles, sandbags - is. Unknown = clear, the old
        /// behaviour.</summary>
        private static bool LineClear(Vector3 from, Vector3 to)
        {
            try
            {
                var d = to - from;
                float len = d.magnitude;
                if (len < 0.3f) return true;
                var hits = Physics.RaycastAll(from, d / len, len, ~0, QueryTriggerInteraction.Ignore);
                if (hits == null) return true;
                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    if (h.distance < 0.3f) continue;   // the grenade / shell itself
                    var col = h.collider;
                    if (col == null) continue;
                    if (col.GetComponentInParent<BodyPart>() != null || col.GetComponentInParent<Soldier>() != null) continue;
                    return false;
                }
                return true;
            }
            catch { return true; }
        }
    }
}
