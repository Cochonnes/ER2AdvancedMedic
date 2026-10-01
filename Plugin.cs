using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace AdvancedMedic
{
    [BepInPlugin(Guid, "Advanced Medic", "0.1.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "cochonnes.er2.advancedmedic";

        // Gated source: Info/Warning only with General/DebugLogging on, errors always (see LogGate).
        internal static LogGate L;
        internal static Cfg C;

        public override void Load()
        {
            L = new LogGate(Log);
            C = new Cfg(Config);
            LogGate.Verbose = C.DebugLogging.Value;
            C.DebugLogging.SettingChanged += (s, e) => LogGate.Verbose = C.DebugLogging.Value;

            // Detect a co-installed ImmersiveEffects so self-shake can defer to it (decision #6).
            Interop.DetectImmersiveShake();

            var h = new Harmony(Guid);

            // IMGUI draw pass for the overlay + pain flash. Same hook ImmersiveEffects uses; a
            // second independent postfix is fine.
            TryPatch(h, typeof(PlayerController), "OnGUI", nameof(Hooks.PC_OnGUI_Post), post: true);

            // Per-frame logic tick (pain decay, timers, effect application). LateUpdate keeps it
            // after the game has posed the soldier this frame.
            TryPatch(h, typeof(PlayerController), "LateUpdate", nameof(Hooks.PC_LateUpdate_Post), post: true);

            // Wound sensor + anti-oneshot on the per-limb path (prefix scales, postfix records).
            TryPatch(h, typeof(Soldier), "DamageWithAnimation", nameof(Hooks.DamageWithAnimation_Pre), post: false);
            TryPatch(h, typeof(Soldier), "DamageWithAnimation", nameof(Hooks.DamageWithAnimation_Post), post: true);

            // Anti-oneshot on the direct damage path (guarded against double-scaling), plus a postfix
            // that records a wound for hits that skip the per-limb DamageWithAnimation path.
            TryPatch(h, typeof(Soldier), "Damage", nameof(Hooks.Damage_Pre), post: false);
            TryPatch(h, typeof(Soldier), "Damage", nameof(Hooks.Damage_Post), post: true);

            // Flag explosion damage so plain-Damage-path hits are classified as explosions (shrapnel +
            // ExplosionDamageMult). Latch around BOTH the blast entry (HitSoldierPart) AND the per-part
            // explosion-damage carrier (BodyPart.TryDamageWithExplosion) — the current build routes blast
            // damage through the latter (ImpactSpecifier redesign), so wrapping it makes the classification
            // robust even when the damage reaches the soldier outside HitSoldierPart's own window.
            TryPatch(h, typeof(Corvostudio.Weapons.Explosion), "HitSoldierPart", nameof(Hooks.Explosion_Pre), post: false, quiet: true);
            TryPatch(h, typeof(Corvostudio.Weapons.Explosion), "HitSoldierPart", nameof(Hooks.Explosion_Post), post: true, quiet: true);
            // TryDamageWithExplosion carries the blast's HitType, so a fire-typed blast opens a FIRE scope.
            TryPatch(h, typeof(BodyPart), "TryDamageWithExplosion", nameof(Hooks.TryDamageWithExplosion_Pre), post: false, quiet: true);
            TryPatch(h, typeof(BodyPart), "TryDamageWithExplosion", nameof(Hooks.TryDamageWithExplosion_Post), post: true, quiet: true);

            // Flag bullet damage the same way, so a plain-Damage hit is only read as a BULLET wound when
            // a bullet really delivered it; anything else unscoped (falls, bumps, scripted damage) gets
            // the blunt-trauma table. Only trusted when BOTH halves installed.
            bool bPre = TryPatch(h, typeof(Bullet), "BulletDamage", nameof(Hooks.Bullet_Pre), post: false, quiet: true);
            bool bPost = TryPatch(h, typeof(Bullet), "BulletDamage", nameof(Hooks.Bullet_Post), post: true, quiet: true);
            Hooks.BulletScopeInstalled = bPre && bPost;

            // Spawn: multiply each soldier's bandages + aspirin.
            TryPatch(h, typeof(Soldier), "Start", nameof(Hooks.Start_Post), post: true, quiet: true);

            // AI heal detection: note the game's own AI bandage/syringe and update our record once its clip ends.
            TryPatch(h, typeof(Soldier), "UseBandages", nameof(Hooks.UseBandages_Post), post: true, quiet: true);
            TryPatch(h, typeof(Soldier), "UseSyrynge", nameof(Hooks.UseSyrynge_Post), post: true, quiet: true);

            // Downed state: revive block (authority only) and the downed stamp / vanilla timer pin.
            TryPatch(h, typeof(Soldier), "SetIncapacitated", nameof(Hooks.SetIncapacitated_Pre), post: false);
            TryPatch(h, typeof(Soldier), "SetIncapacitated", nameof(Hooks.SetIncapacitated_Post), post: true);

            // Death cleanup: prune wound state (both kill paths).
            TryPatch(h, typeof(Creature), "Kill", nameof(Hooks.Kill_Post), post: true);
            TryPatch(h, typeof(Creature), "KillSynched", nameof(Hooks.KillSynched_Post), post: true, quiet: true);
            // Soldier declares its own Kill/KillSynched overrides: patch those leaves too, or a soldier's
            // death may never reach the Creature patches above (only if the native override calls base).
            TryPatchDeclared(h, typeof(Soldier), "Kill", nameof(Hooks.SoldierKill_Post));
            TryPatchDeclared(h, typeof(Soldier), "KillSynched", nameof(Hooks.SoldierKill_Post));

            // Diagnostic (log-only, DebugLogging): confirm the per-body-part hit entry for the player.
            // Installed ONLY with the flag on. BodyPart.HitPart runs for every bullet against every
            // body part on the map, so even a patch body that returns immediately is a Harmony
            // trampoline on one of the hottest methods in the game — and there is nothing to gain
            // from carrying it when it cannot log. Toggling DebugLogging needs a restart to install.
            if (C.DebugLogging.Value)
                TryPatch(h, typeof(BodyPart), "HitPart", nameof(Hooks.HitPart_Pre), post: false, quiet: true);

            // MP-client headshot lethality: the flinch trigger fires on the client with the real part,
            // even when host-authoritative damage skips our Damage hooks.
            TryPatch(h, typeof(Soldier), "PlayDamageAnimation", nameof(Hooks.PlayDamageAnimation_Post), post: true, quiet: true);

            // Grenade lethality by proximity (every context): the blast spawns everywhere for its visual,
            // so we read its position/radius here (the local player's blast damage never reaches our
            // Damage hooks).
            TryPatch(h, typeof(Corvostudio.Weapons.Explosion), "CreateExplosion", nameof(Hooks.CreateExplosion_Post), post: true, quiet: true);
            // Wrap the WHOLE CreateExplosion in the InExplosion flag: the blast applies its Soldier.Damage
            // synchronously here, and the log showed those hits arriving with InExplosion=false (→ misread
            // as a bullet, ×0.6, survivable) because HitSoldierPart/TryDamageWithExplosion didn't cover the
            // actual damage call. Wrapping the outermost call fixes grenade lethality on host + SP.
            // The scope reads the blast's HitType: an Explosion opens the explosion scope, a Fire-typed
            // blast a fire scope (burns + ordinary damage rules, never the blast multiplier / insta-kill).
            TryPatch(h, typeof(Corvostudio.Weapons.Explosion), "CreateExplosion", nameof(Hooks.CreateExplosionScope_Pre), post: false, quiet: true);
            TryPatch(h, typeof(Corvostudio.Weapons.Explosion), "CreateExplosion", nameof(Hooks.CreateExplosionScope_Post), post: true, quiet: true);

            // Movement slow from leg/chest wounds (scale the move vector; both movers).
            TryPatch(h, typeof(Soldier), "MoveFPS", nameof(Hooks.MoveScale_Pre), post: false, quiet: true);
            TryPatch(h, typeof(Soldier), "Move", nameof(Hooks.MoveScale_Pre), post: false, quiet: true);

            // ReplaceVanillaHeal: strip vanilla heal interactions (they come from the BODY PART you
            // look at, not the Soldier). Patch both BodyPart and the Interagible base.
            TryPatch(h, typeof(BodyPart), "GetInteractions", nameof(Hooks.GetInteractions_Post), post: true, quiet: true);
            TryPatch(h, typeof(Interagible), "GetInteractions", nameof(Hooks.GetInteractions_Post), post: true, quiet: true);

            // While the overlay is open: freeze player look and block player fire (both the Fire
            // entry and the downstream Shoot, so no fire path leaks through).
            TryPatch(h, typeof(PlayerController), "GetCameraRotationInput", nameof(Hooks.BlockLook_Pre), post: false, quiet: true);
            TryPatch(h, typeof(GenericGun), "Fire", nameof(Hooks.BlockFireEntry_Pre), post: false, quiet: true);
            TryPatch(h, typeof(GenericGun), "FakeFire", nameof(Hooks.BlockFireEntry_Pre), post: false, quiet: true);
            TryPatch(h, typeof(GenericGun), "Shoot", nameof(Hooks.BlockFire_Pre), post: false, quiet: true);

            // Vehicle turret fire-block: ONLY TurretGun.Shoot — a concrete leaf method, the direct analogue
            // of the stable GenericGun.Shoot patch above. Deliberately NOT patching Turret.SetGunTriggerPressed
            // (a base method with a derived override), which crashed the game on boot (IL2CPP trampoline
            // hazard). Cancels the local operator's vehicle shot while the overlay is open.
            TryPatch(h, typeof(TurretGun), "Shoot", nameof(Hooks.BlockTurretShoot_Pre), post: false, quiet: true);

            // Weapon switching (wheel AND number keys) while the panel is open: its own guarded method.
            InstallSwitchBlock(h);

            // (The "[B]" hold-prompt shown during the bandage animation is hidden from Effects.Tick via
            // Hooks.HideHealHoldPrompt — no patch needed; it never comes through the interaction system.)

            // Optional hit/hurt sounds: find the user's WAVs and start decoding them off the main thread.
            HitSounds.Init();

            L.LogInfo("Advanced Medic loaded. ImmersiveEffects present: " + Interop.ImmersiveShakePresent
                          + (LogGate.Verbose ? "" : " (set General/DebugLogging for details)"));
        }

        /// <summary>Soldier.SwitchTo(int) prefix: the local player's weapon switch - mouse wheel and number keys -
        /// is held while the medical panel is open (UI/BlockWeaponSwitchWhilePanelOpen), so the wheel only
        /// scrolls the wound list. SwitchTo has native callers (Tools/xref: SoldierAI.SequentialUpdate,
        /// UseBestWearedWeaponCheck) and its own body; ImmersiveHUD's tactical map prefixes it the same way,
        /// and Harmony chains both. Its own method (JIT trap): Soldier.SwitchTo vanishing in an update costs
        /// this one feature, not Load.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void InstallSwitchBlock(Harmony h)
        {
            try { TryPatch(h, typeof(Soldier), "SwitchTo", nameof(Hooks.BlockSwitch_Pre), post: false, quiet: true); }
            catch (Exception e) { L.LogError("Weapon-switch block not installed: " + e.Message); }
        }

        /// <summary>Postfix a method only if <paramref name="target"/> itself declares it (an override),
        /// never a base-class method reached through inheritance.</summary>
        private static void TryPatchDeclared(Harmony h, Type target, string method, string patchName)
        {
            try
            {
                var original = AccessTools.DeclaredMethod(target, method, Type.EmptyTypes);
                if (original == null) { L.LogInfo($"{target.Name}.{method} not declared - base patch covers it"); return; }
                h.Patch(original, postfix: new HarmonyMethod(AccessTools.Method(typeof(Hooks), patchName)));
                L.LogInfo($"Patched {target.Name}.{method} (declared override)");
            }
            catch (Exception e) { L.LogError($"Patch failed on {target.Name}.{method}: {e.Message}"); }
        }

        /// <summary>Install one patch. True when it went in.</summary>
        internal static bool TryPatch(Harmony h, Type target, string method, string patchName, bool post, bool quiet = false)
        {
            try
            {
                var original = AccessTools.Method(target, method);
                if (original == null)
                {
                    if (!quiet) L.LogWarning($"Could not find {target.Name}.{method} - that feature is disabled.");
                    return false;
                }
                var patch = new HarmonyMethod(AccessTools.Method(typeof(Hooks), patchName));
                if (post) h.Patch(original, postfix: patch);
                else h.Patch(original, prefix: patch);
                L.LogInfo($"Patched {target.Name}.{method}");
                return true;
            }
            catch (Exception e) { L.LogError($"Patch failed on {target.Name}.{method}: {e.Message}"); return false; }
        }
    }
}
