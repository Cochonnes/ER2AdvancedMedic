using System;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Harmony patch bodies. IL2CPP positional params are named __0, __1, … Keep every body
    /// wrapped in try/catch so a game-side change never takes the whole frame down; a real failure is
    /// reported once through <see cref="Guard"/> (always printed, even with DebugLogging off).
    ///
    /// Split by concern: this file (frame tick, damage, movement, input blocks, deaths, the MP flinch,
    /// the GUI pass), Hooks.Blasts.cs (damage scopes + grenade proximity), Hooks.AiHeals.cs (vanilla AI
    /// heals and the downed/revive gate), Hooks.Prompt.cs (the bandage hold-prompt).
    /// </summary>
    public static partial class Hooks
    {
        private static void Fail(string hook, Exception e) => Guard.Once("hook " + hook, e);

        // ----- PlayerController.LateUpdate (postfix): capture PC, run our tick -----
        public static void PC_LateUpdate_Post(PlayerController __instance)
        {
            try
            {
                Interop.PC = __instance;
                // No damage call is ever in flight at this point in the frame, so any scope still open
                // was left by an original that threw past its postfix (see ResetDamageScopes).
                ResetDamageScopes();
                if (!Plugin.C.Enabled.Value)
                {
                    // Disabling with the panel up would latch Open=true forever: the look/fire
                    // blocks below key off it, and the tick that would close it no longer runs.
                    // Release once, then stay out of the way.
                    if (MedicUI.Open) { try { MedicUI.Close(); Effects.ReleaseOverlay(); } catch { } }
                    return;
                }
                Effects.Tick();
            }
            catch (Exception e) { Fail("LateUpdate", e); }
        }

        /// <summary>The local player is gone (respawn screen): drop the player's own pending bits here.</summary>
        internal static void ResetPlayerLocal()
        {
            BandageClipUntil = 0f;
            _blasts.Clear();   // grenade checks queued for the old body
        }

        // ----- Soldier.DamageWithAnimation(float dam, BodyPartType, HitType) -----
        // Prefix scales+caps the damage (anti-oneshot) and latches a guard so the inner Damage
        // call isn't scaled again. Postfix records the wound (the per-limb sensor) and clears it.
        public static void DamageWithAnimation_Pre(Soldier __instance, ref float __0, BodyPartType __1, HitType __2)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return;
                HpLogic.InDamageWithAnimation = true;
                // This path carries the real HitType, so classify the explosion directly (reliable) —
                // the InExplosion flag can miss if the game applies the damage outside HitSoldierPart.
                // Fire is NOT an explosion: it takes the ordinary damage rules (anti-oneshot scale, no
                // blast multiplier, no insta-kill roll) and leaves burns (WoundLogic/Physiology).
                HpLogic.Process(__instance, ref __0, __1, __2 == HitType.Explosion);
            }
            catch (Exception e) { Fail("DamageWithAnimation_Pre", e); }
        }

        public static void DamageWithAnimation_Post(Soldier __instance, float __0, BodyPartType __1, HitType __2)
        {
            try { if (Plugin.C.Enabled.Value) WoundLogic.OnDamage(__instance, __0, __1, __2); }
            catch (Exception e) { Fail("DamageWithAnimation_Post", e); }
            finally { HpLogic.InDamageWithAnimation = false; }
        }

        // ----- Soldier.MoveFPS / Move (prefix): scale the move vector by the soldier's walk
        // factor. GetMoveSpeed() is never called by the game, so the move vector is the reliable
        // lever (proven by ImmersiveEffects). Applies to any wounded soldier (player + AI). -----
        private static int _playerMoveFrame = -1;
        private const float MoveCacheSeconds = 0.25f;

        public static void MoveScale_Pre(Soldier __instance, ref Vector3 __0, SoldierPose __1, bool __2, ref float __3)
        {
            try
            {
                if (!Plugin.C.Enabled.Value || __instance == null) return;

                // The game calls this for EVERY soldier, EVERY frame, so the order of the checks
                // below is the whole cost of this hook on a busy map.
                var ptr = Interop.PtrOf(__instance);

                // Freeze the local player's movement while the overlay is open. A pointer compare
                // against the frame-cached player pointer, not a fresh
                // PlayerController.ControlledCharacter read per soldier per frame.
                if (MedicUI.Open && ptr != IntPtr.Zero && ptr == Interop.PlayerPtr()) { __0 = Vector3.zero; return; }

                // Untracked soldier: we have recorded no wound, so there is no walk penalty to apply
                // and no downed state of ours to hold. Bail here — this is the overwhelming
                // majority of the calls, and everything past this point costs native interop.
                //
                // Safe because anything we down IS tracked: SetIncapacitated's postfix runs
                // WoundState.Get before it looks at the flag, so a downed soldier always has an entry.
                var w = WoundState.Peek(ptr);   // the pointer read above, not a second PtrOf
                if (w == null) return;

                // Downed flag + walk factors, cached on the soldier at 4 Hz (a native IsInjured read and
                // two wound scans per soldier per Move call were the cost here). Anything that changes
                // them at once - a hit, a treatment, going down or up - invalidates the cache.
                float now = Time.time;
                if (now >= w.MoveCacheAt)
                {
                    w.MoveCacheAt = now + MoveCacheSeconds;
                    w.MoveDowned = Interop.IsDowned(__instance);
                    w.MoveWalk = Effects.ComputeWalkFactor(w, false);
                    w.MoveSprint = Effects.ComputeWalkFactor(w, true);
                }

                // A downed/incapacitated soldier can't move. The vanilla movement lock doesn't hold on
                // the trim-to-just-lethal downed state we create (esp. in MP), so you could run around
                // in the downed animation — zero the move vector here to stop that.
                if (w.MoveDowned) { __0 = Vector3.zero; return; }

                // MoveFPS may call Move internally: scale the player once per frame, like ImmersiveEffects,
                // so the factor never applies twice (0.96 must not become 0.92).
                bool isPlayer = ptr != IntPtr.Zero && ptr == Interop.PlayerPtr();
                if (isPlayer)
                {
                    if (_playerMoveFrame == Time.frameCount) return;
                    _playerMoveFrame = Time.frameCount;
                }

                float f = __2 ? w.MoveSprint : w.MoveWalk;   // __2 = running/sprinting
                // Scale DELTA TIME, not the move VECTOR. The vector still drives the game's walk->run
                // animation blend (which keys off its magnitude), so a slowed sprint stays a sprint —
                // just slower — instead of collapsing into a walk that ends up slower than an
                // un-debuffed walk. Same lever ImmersiveEffects uses; both prefixes compound cleanly.
                if (f < 0.999f && __3 > 0f) __3 *= f;
            }
            catch (Exception e) { Fail("MoveScale", e); }
        }

        // ----- BodyPart/Interagible.GetInteractions(InventoryManager) (postfix): strip vanilla heal -----
        public static void GetInteractions_Post(Il2CppSystem.Collections.Generic.List<Interaction> __result)
        {
            try { if (Plugin.C.Enabled.Value) Targeting.StripVanillaHeal(__result); }
            catch (Exception e) { Fail("GetInteractions", e); }
        }

        // ----- PlayerController.GetCameraRotationInput (prefix): freeze look while overlay open -----
        public static bool BlockLook_Pre(ref Vector2 __result)
        {
            try
            {
                if (MedicUI.Open) { __result = Vector2.zero; return false; }
            }
            catch (Exception e) { Fail("BlockLook", e); }
            return true;
        }

        // ----- GenericGun.Shoot(Creature user, …) (prefix): block player fire while overlay open -----
        public static bool BlockFire_Pre(Creature __0)
        {
            try
            {
                if (MedicUI.Open && Interop.IsPlayer(__0)) return false;   // skip the shot
            }
            catch (Exception e) { Fail("BlockFire", e); }
            return true;
        }

        // ----- Soldier.SwitchTo(int) (prefix): hold the LOCAL player's weapon switch while the panel is open. -----
        // Keyed on MedicUI.Open itself - there is no separate flag to get stuck: the panel closes on toggle, on
        // the player's death / the respawn screen, on a scene flush, when downed, and when the mod is switched
        // off, and a dead player's soldier is no longer the local player anyway. Everyone else passes through.
        public static bool BlockSwitch_Pre(Soldier __instance)
        {
            try
            {
                if (!MedicUI.Open || !Plugin.C.Enabled.Value || !Plugin.C.BlockWeaponSwitchWhilePanelOpen.Value) return true;
                if (Interop.IsPlayer(__instance)) return false;   // pointer compare against the frame-cached player
            }
            catch (Exception e) { Fail("BlockSwitch", e); }
            return true;
        }

        // ----- GenericGun.Fire / FakeFire(Creature user, …)->bool (prefix): the real fire entry.
        // Return false + __result=false to fully cancel the fire while the overlay is open. -----
        public static bool BlockFireEntry_Pre(Creature __0, ref bool __result)
        {
            try
            {
                if (MedicUI.Open && Interop.IsPlayer(__0)) { __result = false; return false; }
            }
            catch (Exception e) { Fail("BlockFireEntry", e); }
            return true;
        }

        // ----- TurretGun.Shoot(Creature user, …) (prefix): backstop — cancel the actual vehicle shot for
        // the local operator while the overlay is open. -----
        public static bool BlockTurretShoot_Pre(Creature __0)
        {
            try
            {
                if (MedicUI.Open && Interop.IsPlayer(__0)) return false;
            }
            catch (Exception e) { Fail("BlockTurretShoot", e); }
            return true;
        }

        // ----- Soldier.Damage(float dam) (prefix): anti-oneshot damage scaling -----
        // Skipped when a DamageWithAnimation call is in flight (already scaled upstream).
        public static void Damage_Pre(Soldier __instance, ref float __0)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return;
                if (HpLogic.InDamageWithAnimation) return;
                HpLogic.Process(__instance, ref __0, BodyPartType.auto_detect, WoundLogic.InExplosion);
            }
            catch (Exception e) { Fail("Damage_Pre", e); }
        }

        // ----- Creature/Soldier.Damage (postfix): record a wound for hits that DON'T go through the
        // per-limb DamageWithAnimation path (a lot of hits — incl. MP-networked damage — route here),
        // so "hit but never bleeding" stops. No body part on this path → the chest bucket; the knockdown
        // roll is skipped (physical:false). Guarded against the DamageWithAnimation re-entry, which
        // records its own wound with the real part.
        //
        // The hit TYPE comes from the scope flags: a blast (InExplosion) or a bullet (InBullet, set around
        // Bullet.BulletDamage). Anything else unscoped - falls, vehicle bumps, scripted damage - used to
        // become a bleeding BULLET wound in the chest; in single player it now takes the blunt-trauma
        // table (bruises; crush/avulsion only when heavy). In an MP room an unscoped hit stays a bullet
        // wound: networked damage can arrive outside any local scope there, and "hit but never
        // bleeding" is the worse failure. Both logged (DebugLogging) so the real sources can be seen. -----
        public static void Damage_Post(Soldier __instance, float __0)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return;
                if (HpLogic.InDamageWithAnimation) return;
                HitType hit;
                if (WoundLogic.InFire) hit = HitType.Fire;
                else if (WoundLogic.InExplosion) hit = HitType.Explosion;
                else if (WoundLogic.InBullet || !BulletScopeInstalled) hit = HitType.Projectile;
                else
                {
                    bool room = Interop.InMpRoom();
                    hit = room ? HitType.Projectile : HitType.VehicleCollision;
                    if (Plugin.C.DebugLogging.Value && _unscopedLogged < 20)
                    {
                        _unscopedLogged++;
                        Plugin.L.LogInfo($"[hit] unscoped plain Damage ({(Interop.IsPlayer(__instance) ? "PLAYER" : "ai")} dam={__0:0.0}) -> {(room ? "bullet (MP room)" : "blunt")}");
                    }
                }
                WoundLogic.OnDamage(__instance, __0, BodyPartType.auto_detect, hit, physical: false);
            }
            catch (Exception e) { Fail("Damage_Post", e); }
        }

        private static int _unscopedLogged;

        // ----- Soldier.Start() (postfix): queue the spawn medical-kit doubling -----
        public static void Start_Post(Soldier __instance)
        {
            try
            {
                // The medic markers' soldier list, whatever Enabled says (so it is complete if the mod is
                // switched back on mid-mission): one dictionary write per spawn.
                Marks.OnSpawn(__instance);
                if (Plugin.C.Enabled.Value) SpawnKit.OnSpawn(__instance);
            }
            catch (Exception e) { Fail("Start", e); }
        }

        // ----- Creature.Kill() (postfix): prune wound state for the dead soldier -----
        public static void Kill_Post(Creature __instance)
        {
            try
            {
                if (Plugin.C.DebugLogging.Value && Interop.IsPlayer(__instance))
                    Plugin.L.LogWarning("[death] PLAYER died via Creature.Kill");
                PruneDead(__instance);
            }
            catch (Exception e) { Fail("Kill", e); }
        }

        // ----- Creature.KillSynched() (postfix): a SEPARATE kill path (MP-synced) we weren't pruning.
        // Also tells us if the player dies through here rather than Kill(). -----
        public static void KillSynched_Post(Creature __instance)
        {
            try
            {
                if (Plugin.C.DebugLogging.Value && Interop.IsPlayer(__instance))
                    Plugin.L.LogWarning("[death] PLAYER died via KillSynched");
                PruneDead(__instance);
            }
            catch (Exception e) { Fail("KillSynched", e); }
        }

        // ----- Soldier.Kill() / Soldier.KillSynched() (postfix): Soldier declares its OWN overrides of
        // both, so the Creature patches above only see a soldier's death if the native override happens
        // to call the base. These leaf patches (the same shape as the long-stable Soldier.Damage one)
        // make the prune certain; pruning twice is harmless. -----
        public static void SoldierKill_Post(Soldier __instance)
        {
            try { PruneDead(__instance); } catch (Exception e) { Fail("SoldierKill", e); }
        }

        /// <summary>Everything we key on a soldier's pointer goes with them (SoldierRegistry): IL2CPP reuses
        /// pointers, so a leftover entry would attach to whoever spawns next at that address.</summary>
        private static void PruneDead(Creature c) { SoldierRegistry.Forget(Interop.PtrOf(c), dead: true); }

        // ----- BodyPart.HitPart(dam, penetration, point, faction, HitType) (prefix, LOG ONLY):
        // diagnostic to confirm whether the player's hits arrive here and whether they then reach our
        // Damage/DamageWithAnimation hooks (i.e. whether the anti-oneshot + wound sensor even run).
        // Only installed with DebugLogging on at load. -----
        public static void HitPart_Pre(BodyPart __instance, float __0, HitType __4)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return;
                if (!Plugin.C.DebugLogging.Value || __instance == null) return;
                Soldier s = null;
                try { s = Interop.CastTo<Soldier>(__instance.GetUnit()); } catch { }
                bool isPlayer = s != null && Interop.IsPlayer(s);
                Plugin.L.LogInfo($"[hitpart] {(isPlayer ? "PLAYER" : (s == null ? "?" : "ai"))} dam={__0:0.0} type={__4}");
            }
            catch (Exception e) { Fail("HitPart", e); }
        }

        // ----- Soldier.PlayDamageAnimation(BodyPartType, HitType) (postfix): MP-client headshot lethality.
        // As a non-host client the local player's damage is host-authoritative — our Damage/DamageWithAnimation
        // hooks don't fire, so the anti-oneshot never runs and a headshot often isn't lethal. This flinch
        // trigger DOES fire on the client with the real body part, so a head hit here (that our own damage
        // path did NOT just handle) rolls HeadLethal to kill via the synced kill - on an UNMODDED host only
        // (a modded host's machine already rolled it). SP/host: our DamageWithAnimation ran first. -----
        public static void PlayDamageAnimation_Post(Soldier __instance, BodyPartType __0, HitType __1)
        {
            try
            {
                if (!Plugin.C.Enabled.Value) return;
                // ONLY as a non-host client, where the local player's damage is host-authoritative and our
                // Damage hooks never fired. On SP/host the normal path already handled this hit — and this
                // flinch may be nested inside that DamageWithAnimation, so bail on that too.
                if (!Interop.MpNonHost()) return;
                if (HpLogic.InDamageWithAnimation) return;
                if (!Interop.IsPlayer(__instance))
                {
                    // An AI hit seen on a client. If the host runs the mod its snapshot is the authority
                    // (it saw the real damage). If it doesn't, nothing would ever wound this soldier here,
                    // so build a local wound from the flinch's real part + type (not broadcast).
                    if (Plugin.C.MpClientWoundFromFlinch.Value && !MpSync.HostSendsWounds)
                        WoundLogic.OnClientFlinch(__instance, __0, __1);
                    return;
                }

                // A MODDED host: the machine that computed this hit applied the host's rules to it
                // (DamageRules: HpLogic's head kill/down/wound roll, and WoundLogic's wound, sent to us in a
                // snapshot). Rolling HeadLethal again here made a headshot ~98% lethal, and a flinch wound on
                // top duplicated the synced one. The HP watchdog (deferred while the host sends wounds) is the
                // net for a hit whose shooter doesn't run the mod.
                if (MpSync.HostSendsWounds)
                {
                    if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] flinch {__0}/{__1}: modded host resolves hits -> no local roll, no flinch wound");
                    return;
                }

                // Unmodded host (fallback): nobody applied our rules to this hit. Reconstruct the WOUND from
                // the real part + type the flinch carries — this is why a client "gets hit but never
                // wounded" (grenades included). Nominal damage (we don't get the host's number), above
                // MinWoundDamage so it always registers.
                if (Plugin.C.MpClientWoundFromFlinch.Value)
                    WoundLogic.OnDamage(__instance, 25f, __0, __1, physical: true);

                // Headshot lethality (unmodded host): a head hit rolls HeadLethal to kill - the only roll.
                if (Plugin.C.MpClientHeadshotKill.Value && __0 == BodyPartType.head
                    && UnityEngine.Random.value < Plugin.C.HeadLethal.Value)
                {
                    if (Plugin.C.DebugLogging.Value) Plugin.L.LogWarning("[mp] client headshot (unmodded host fallback) -> KillSynched");
                    Interop.KillAuthoritative(__instance);
                }
            }
            catch (Exception e) { Fail("PlayDamageAnimation", e); }
        }

        // ----- PlayerController.OnGUI (postfix): draw overlay + pain flash + debug -----
        // Every draw subsystem on its own guard: they used to share one try, so a single exception in,
        // say, the medic markers blanked the medic panel too - while the cursor stayed freed and the
        // camera frozen. A failing panel now closes itself instead of leaving the player stuck.
        public static void PC_OnGUI_Post(PlayerController __instance)
        {
            try
            {
                Interop.PC = __instance;
                if (!Plugin.C.Enabled.Value || Event.current == null) return;
            }
            catch (Exception e) { Fail("OnGUI", e); return; }

            // Not repaint-gated here: MedicUI needs every event so its MouseDown hit-tests work.
            // Texture fills inside Gui.Rect/Tex self-gate to Repaint.
            try { PainFX.DrawDesaturation(); } catch (Exception e) { Fail("OnGUI Desaturation", e); }
            try { PainFX.DrawUnconscious(); } catch (Exception e) { Fail("OnGUI Unconscious", e); }
            try { BloodScreen.DrawOverlay(); } catch (Exception e) { Fail("OnGUI BloodScreen", e); }   // held blood splash (Persistent)
            try { PainFX.DrawFlash(); } catch (Exception e) { Fail("OnGUI PainFlash", e); }
            try { Marks.Draw(); } catch (Exception e) { Fail("OnGUI Marks", e); }   // medic marks over healing sources

            // The overlay/debug panels read cached soldier references; skip them with no live
            // player (menu / mid scene-unload) so a freed pointer is never touched in the GUI
            // pass — the tick's scene guard clears them, but OnGUI can fire first that frame.
            bool player;
            try { player = Interop.Player() != null; } catch { player = false; }
            if (!player) return;

            try { MedicCall.DrawPrompt(); } catch (Exception e) { Fail("OnGUI MedicPrompt", e); }
            try { HitPeek.Draw(); } catch (Exception e) { Fail("OnGUI HitPeek", e); }
            try { MedicUI.Draw(); }
            catch (Exception e) { Fail("OnGUI MedicPanel", e); try { MedicUI.Close(); } catch { } }
            try { if (Plugin.C.DebugOverlay.Value) MedicUI.DrawDebug(); } catch (Exception e) { Fail("OnGUI Debug", e); }
        }
    }
}
