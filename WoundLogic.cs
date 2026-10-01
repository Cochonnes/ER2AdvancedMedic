using System;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Turns damage events into mod-side wounds using the wound tables (Physiology.Generate): the
    /// damage type picks the wound kinds (bullet → velocity wound / avulsion / contusion, explosion →
    /// several wounds spread over the body, collision → bruises/crush, fire → burns), the damage sets
    /// their size, and the size sets how much each wound bleeds and hurts. Also drives pain, fractures,
    /// the pain knock-out and heart-hit arrest, and the vanilla bleeding flag.
    /// </summary>
    internal static class WoundLogic
    {
        // Set true while an explosion is applying damage (Explosion.HitSoldierPart), so damage that
        // reaches us via the plain Damage path is still classified as an explosion, not a bullet.
        public static bool InExplosion;

        // Set true while a FIRE-typed blast is applying damage (CreateExplosion / TryDamageWithExplosion
        // with HitType.Fire): burns and the ordinary damage rules, never the blast multiplier.
        public static bool InFire;

        // Set true while a bullet is applying damage (Bullet.BulletDamage), so a plain-Damage hit is
        // only read as a bullet wound when a bullet really delivered it (see Hooks.Damage_Post).
        public static bool InBullet;

        // Time.time of the last wound our hooks recorded for the LOCAL player — the HP watchdog uses
        // it to avoid double-counting a hit it already saw through a hook.
        public static float LastPlayerWoundTime;

        // Time.time a snapshot from ANOTHER machine brought new wounds for the local player: the hit was
        // resolved (rolled and wounded) on the shooter's machine. Read by the deferred grenade check.
        public static float LastRemotePlayerWoundTime = -1f;

        // Nominal wound-table damage for hits we only infer (no damage number reached us).
        private const float InferredBulletDamage = 1.2f;
        private const float InferredBlastDamage = 1.5f;

        /// <summary>Create a wound on the player from an unexplained HP drop (a hit that bypassed our
        /// damage hooks — e.g. host-authoritative MP-client damage). Location is unknown, so a
        /// weighted-random part.</summary>
        public static void InferPlayerWound(Soldier s)
        {
            if (s == null) return;
            var w = WoundState.Get(s);
            if (w == null) return;
            var part = RandomHitPart();
            ApplyHit(s, w, InferredBulletDamage, part, HitType.Projectile, true, false);
            LastPlayerWoundTime = Time.time;
            MpSync.Send(s);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[hit] PLAYER inferred {part} wound from HP drop (hit bypassed our hooks)");
        }

        /// <summary>Give the local player an EXPLOSION wound from a nearby blast detected by world position
        /// (MP client — host-authoritative damage never reaches OnDamage).</summary>
        public static void OnClientExplosionWound(Soldier s)
        {
            if (s == null) return;
            var w = WoundState.Get(s);
            if (w == null) return;
            var part = RandomHitPart();
            ApplyHit(s, w, InferredBlastDamage, part, HitType.Explosion, true, false);
            LastPlayerWoundTime = Time.time;
            MpSync.Send(s);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[hit] PLAYER nearby-explosion {part} wound (MP client)");
        }

        /// <summary>A wound on a soldier from a hit-flinch seen on an MP client (random part, real type, nominal
        /// damage — the host keeps the real number). Local only: not broadcast, since the host is the
        /// authority for that soldier.</summary>
        public static void OnClientFlinch(Soldier s, BodyPartType part, HitType hit)
        {
            if (s == null || Interop.IsDead(s)) return;
            var w = WoundState.Get(s);
            if (w == null) return;
            // The flinch's body part isn't real for AI on a client (every hit reports chest), so place the
            // wound with the same weighted randomizer the inferred player wounds use (Infer*Weight).
            part = RandomHitPart();
            ApplyHit(s, w, hit == HitType.Explosion ? InferredBlastDamage : InferredBulletDamage, part, hit, false, false);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] client-side {part} {hit} wound from flinch (host sends no wound data)");
        }

        /// <summary>Pick a body part for an inferred wound by the configured weights (chest / any limb
        /// / head). The weights are relative — they don't need to sum to anything.</summary>
        private static BodyPartType RandomHitPart()
        {
            var c = Plugin.C;
            float chest = Mathf.Max(0f, c.InferChestWeight.Value);
            float limb  = Mathf.Max(0f, c.InferLimbWeight.Value);
            float head  = Mathf.Max(0f, c.InferHeadWeight.Value);
            float total = chest + limb + head;
            if (total <= 0f) return BodyPartType.chest;

            float r = UnityEngine.Random.value * total;
            if (r < chest) return BodyPartType.chest;
            if (r < chest + limb)
                switch (UnityEngine.Random.Range(0, 4))   // one of the four limbs
                {
                    case 0:  return BodyPartType.arm_l;
                    case 1:  return BodyPartType.arm_r;
                    case 2:  return BodyPartType.leg_l;
                    default: return BodyPartType.leg_r;
                }
            return BodyPartType.head;
        }

        public static void OnDamage(Soldier s, float dam, BodyPartType part, HitType hit, bool physical = true)
        {
            if (s == null) return;
            bool isPlayer = Interop.IsPlayer(s);

            if (Plugin.C.DebugLogging.Value)
                Plugin.L.LogInfo($"[hit] {(isPlayer ? "PLAYER" : "ai")} part={part} type={hit} dam={dam:0.0} phys={physical} life={SafeLife(s)}");

            // Stamp BEFORE the small-hit early return: a hook saw this HP loss, so the HP watchdog must
            // not turn it into an inferred bullet wound a moment later.
            if (isPlayer) LastPlayerWoundTime = Time.time;

            if (dam < Plugin.C.MinWoundDamage.Value) return;
            // The killing hit's postfix runs after Kill_Post pruned the soldier: don't re-track a corpse.
            // (IsDead, not !IsAlive: a soldier just DOWNED sits at life 0 and must still get the wound.)
            if (Interop.IsDead(s)) return;
            if (part == BodyPartType.auto_detect) part = BodyPartType.chest;   // fallback bucket

            var w = WoundState.Get(s);
            if (w == null) return;

            ApplyHit(s, w, dam * Mathf.Max(0f, Plugin.C.WoundDamageScale.Value), part, hit, isPlayer, physical);

            // This blast's wounds are on the local player: the proximity check adds none of its own.
            // (Outside any blast the stamp is stale by the next one, which bumps BlastSeq first.)
            if (isPlayer && hit == HitType.Explosion) Hooks.PlayerBlastWoundSeq = Hooks.BlastSeq;

            // Broadcast the new wound set so other clients see the same wounds (MP only).
            MpSync.Send(s);
        }

        /// <summary>Wounds for one hit, applied to a soldier.</summary>
        private static void ApplyHit(Soldier s, SoldierWounds w, float tableDamage, BodyPartType part, HitType hit, bool isPlayer, bool physical)
        {
            var c = Plugin.C;
            float now = Time.time;
            bool madeBleed = false, heartHit = false, fractured = false, shrapnel = false;
            float hitPain = 0f;
            int worstSize = -1;   // largest BLEEDING wound this hit made (0 minor .. 2 large); -1 = none

            var made = Physiology.Generate(tableDamage, part, hit);
            foreach (var nw in made)
            {
                bool bleeds = nw.Bleed >= 0.005f;   // abrasions / contusions / burns barely bleed: bruise
                // A burn on a part that already carries one GROWS it instead of stacking a new wound:
                // fire damages in many small ticks, and each tick used to add a wound of its own.
                if (nw.Kind == WoundKind.Burn && GrowBurn(w, nw, now, c))
                {
                    hitPain += nw.Pain;
                    continue;
                }
                w.Of(nw.Part).Add(new Wound
                {
                    Type = bleeds ? WoundType.Bleeding : WoundType.Bruise,
                    Kind = nw.Kind, Size = nw.Size, BleedRate = bleeds ? nw.Bleed : 0f, Damage = nw.Damage,
                    HealAt = bleeds ? 0f : now + Mathf.Max(1f, c.BruiseHealTime.Value),
                    Born = now,
                });
                madeBleed |= bleeds;
                hitPain += nw.Pain;
                if (bleeds && nw.Size > worstSize) worstSize = nw.Size;

                if (nw.Fracture && w.FractureState(nw.Part) == 0)
                {
                    w.Of(nw.Part).Add(new Wound { Type = WoundType.Fracture, Kind = nw.Kind, Size = 2, Damage = nw.Damage, Born = now });
                    fractured = true;
                    if (c.DebugLogging.Value) Plugin.L.LogInfo($"[hit] FRACTURE {nw.Part}");
                }

                // Heart shot: a heavy chest wound has a small chance to stop the heart.
                // Only for real located hits (physical): the plain-Damage path lumps everything into chest.
                if (physical && c.ArrestOnHitEnabled.Value && nw.Part == BodyPartType.chest
                    && nw.Damage >= c.OrganDamageThreshold.Value && UnityEngine.Random.value < c.HeartHitChance.Value)
                    heartHit = true;
            }

            // Explosions can also embed shrapnel (needs scissors, then forceps).
            if (hit == HitType.Explosion && made.Count > 0 && UnityEngine.Random.value < c.ExplosionShrapnelChance.Value)
            {
                w.Of(part).Add(new Wound { Type = WoundType.Shrapnel, Kind = WoundKind.Puncture, Born = now });
                shrapnel = true;
            }

            if (c.DebugLogging.Value)
                Plugin.L.LogInfo($"[hit] table dmg={tableDamage:0.00} -> {made.Count} wound(s), pain {hitPain * 100f:0}, bleeding now {Physiology.WoundBleeding(w):0.000}");
            w.InvalidateMove();   // a new leg wound / fracture slows the walk now, not at the next refresh

            // Drive the vanilla bleeding state so the game's own indicators react.
            if (madeBleed) TrySetBleeding(s, true);
            // Local-player feedback. Each is guarded on its own: they reach game UI types, and one of
            // those going missing in an update must not take the pain/knock-out logic below with it.
            if (isPlayer && madeBleed) { try { BloodScreen.OnBleedingHit(s); } catch { } }
            if (isPlayer && made.Count > 0) { try { HitPeek.OnHit(); } catch { } }

            // The hit's own spike (max, not sum), then the floor from EVERY wound now carried - so the
            // second, third, fourth wound each add pain instead of just re-raising to one hit's level.
            Pain.Raise(w, hitPain * 100f);
            Pain.Refresh(w);

            // Per-limb immediate physical effects (knockdown roll). Skipped for route-less hits. Guarded at
            // the call: a failure there (a pose member gone) must not take the rest of the hit - and the
            // caller's MP snapshot - with it.
            if (physical) { try { LimbEffects.OnHit(s, part); } catch (Exception e) { Guard.Once("knockdown", e); } }

            // Only where we own the soldier's downed state: TriggerArrest refuses anyone else (an MP human's
            // fate is vanilla), and returning here anyway used to skip the pain-knockout roll and play the
            // Heavy hurt sound for a heart hit that never happened.
            if (heartHit && Vitals.OwnsState(s))
            {
                Vitals.TriggerArrest(s, w, "heart hit");
                if (isPlayer) PlayHurt(HitSounds.Tier.Heavy);
                return;
            }

            // A single very painful hit can knock the soldier out.
            float scaledPain = hitPain * 100f * Mathf.Max(0f, c.PainCoefficient.Value);
            if (scaledPain >= c.PainKnockoutThreshold.Value && Vitals.OwnsState(s)
                && UnityEngine.Random.value < c.PainKnockoutChance.Value)
            {
                if (!Interop.IsDowned(s)) Vitals.KnockOut(s, w, "pain spike");
            }

            if (isPlayer && made.Count > 0)
                PlayHurt(HurtTier(s, worstSize, fractured, shrapnel));
        }

        /// <summary>Which hurt sound a hit on the local player earns, from what the hit did:
        ///   Heavy  = a large bleeding wound, a broken bone, or the player is down after it (the
        ///            heart-hit arrest plays Heavy directly);
        ///   Medium = a medium bleeding wound, or embedded shrapnel;
        ///   Light  = only minor wounds / bruises.</summary>
        private static HitSounds.Tier HurtTier(Soldier s, int worstSize, bool fractured, bool shrapnel)
        {
            if (worstSize >= 2 || fractured || Interop.IsDowned(s)) return HitSounds.Tier.Heavy;
            if (worstSize == 1 || shrapnel) return HitSounds.Tier.Medium;
            return HitSounds.Tier.Light;
        }

        private static void PlayHurt(HitSounds.Tier tier)
        {
            try { HitSounds.Play(tier); } catch { }
        }

        /// <summary>Fold a new burn into an active burn already on that part: the damage adds up, the size
        /// (and so the pain it carries) follows the total, and the heal clock restarts. False if the part
        /// has no active burn yet.</summary>
        private static bool GrowBurn(SoldierWounds w, Physiology.NewWound nw, float now, Cfg c)
        {
            if (!w.Parts.TryGetValue(nw.Part, out var list)) return false;
            foreach (var wd in list)
            {
                if (!wd.Active || wd.Kind != WoundKind.Burn) continue;
                wd.Damage += nw.Damage;
                wd.Size = Mathf.Max(wd.Size, Physiology.SizeCategory(wd.Damage));
                wd.HealAt = now + Mathf.Max(1f, c.BruiseHealTime.Value) * (1f + 0.5f * wd.Size);   // bigger burns take longer
                return true;
            }
            return false;
        }

        private static void TrySetBleeding(Soldier s, bool on)
        {
            Interop.SetBleeding(s, on);
            if (on) { try { BleedFxRaw(s); } catch { } }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void BleedFxRaw(Soldier s) => s.ApplyBleedingEffect();

        private static string SafeLife(Soldier s)
        {
            int life = Interop.Life(s);
            return life >= 0 ? life.ToString() : "?";
        }
    }
}
