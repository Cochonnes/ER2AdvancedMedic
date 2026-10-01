using BepInEx.Configuration;

namespace AdvancedMedic
{
    public enum HpMode { Everyone, PlayerOnly, Noone }

    // Off = never; Once = one blood splash per bleeding hit (fades on its own); Persistent = keep the
    // splash on screen (re-triggered) the whole time you have an un-bandaged bleeding wound.
    public enum BloodScreenMode { Off, Once, Persistent }

    /// <summary>
    /// The live config schema (DESIGN.md §9 is the original plan, kept for history). Bound once at
    /// load; values are read where they are used, so most edits apply without a restart.
    /// Grouped by feature so the .cfg reads cleanly.
    /// </summary>
    internal class Cfg
    {
        // ---- General ----
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> OpenKey;         // parsed to KeyCode at use
        public ConfigEntry<bool> ReplaceVanillaHeal;
        public ConfigEntry<float> BandagePromptHideSeconds;
        public ConfigEntry<bool> DebugOverlay;
        public ConfigEntry<bool> DebugLogging;
        public ConfigEntry<string> DebugOverlayKey;
        public ConfigEntry<bool> UseGameIcons;
        public ConfigEntry<float> TreatRange;
        public ConfigEntry<bool> TreatOtherPlayers;
        public ConfigEntry<bool> BlockWeaponSwitchWhilePanelOpen;
        public ConfigEntry<bool> EnableMedicMark;
        public ConfigEntry<float> MedicMarkMaxDist;
        public ConfigEntry<float> MedicMarkSize;
        public ConfigEntry<float> MedicMarkBloodThreshold;

        // ---- HP / anti-oneshot ----
        public ConfigEntry<HpMode> ExtraHpMode;
        public ConfigEntry<float> DamageScale;
        public ConfigEntry<float> OverkillMargin;
        public ConfigEntry<float> SurviveLifeFrac;
        public ConfigEntry<float> OneShotHpThreshold;
        public ConfigEntry<float> AiDownChance;
        public ConfigEntry<float> HeadLethal;
        public ConfigEntry<float> HeadDown;
        public ConfigEntry<float> ChestLethal;
        public ConfigEntry<float> ChestDown;
        public ConfigEntry<float> LimbLethal;
        public ConfigEntry<float> LimbDown;
        public ConfigEntry<float> LethalityMult;
        public ConfigEntry<bool> LethalityAiOnly;
        public ConfigEntry<float> ExplosionDamageMult;
        public ConfigEntry<bool> VehicleOccupantLethal;
        public ConfigEntry<bool> MpClientHeadshotKill;
        public ConfigEntry<bool> MpClientWoundFromFlinch;
        public ConfigEntry<bool> GrenadeInstaKill;
        public ConfigEntry<float> GrenadeInstaKillChance;
        public ConfigEntry<float> GrenadeLethalRadius;
        public ConfigEntry<bool> HoldVanillaDownedTimer;
        public ConfigEntry<HpMode> DownedSurviveMode;

        // ---- Limbs ----
        public ConfigEntry<float> KnockdownLock;

        // ---- Hit feedback (bleed/pain suppression, knockdown, leg-slow, chest/head stamina) ----
        public ConfigEntry<bool> EnableHitKnockdown;
        public ConfigEntry<float> HitCrouchChance;
        public ConfigEntry<float> HitProneChance;
        public ConfigEntry<float> BleedSuppression;
        public ConfigEntry<float> ArmBleedSuppression;
        public ConfigEntry<float> PainSuppLight;
        public ConfigEntry<float> PainSuppMed;
        public ConfigEntry<float> PainSuppHigh;
        public ConfigEntry<float> LegBleedWalk;
        public ConfigEntry<float> ChestHeadBleedStaminaFactor;

        // ---- AI-medic treatment (press X near a medic) ----
        public ConfigEntry<bool> EnableMedicCall;
        public ConfigEntry<string> MedicCallKey;
        public ConfigEntry<float> MedicCallRange;
        public ConfigEntry<float> MedicCallTime;

        // ---- Medical-tent full heal (same key, proximity) ----
        public ConfigEntry<bool> EnableTentHeal;
        public ConfigEntry<float> TentRange;
        public ConfigEntry<float> TentHealTime;
        public ConfigEntry<string> TentToken1;
        public ConfigEntry<string> TentToken2;
        public ConfigEntry<float> TentHealLife;

        // ---- Pain ----
        public ConfigEntry<float> LightIntensity;
        public ConfigEntry<float> MedIntensity;
        public ConfigEntry<float> HighIntensity;
        public ConfigEntry<float> PainDeclinePerSec;
        public ConfigEntry<float> AspirinDeclineMult;
        public ConfigEntry<float> AspirinMin;
        public ConfigEntry<float> AspirinMax;
        public ConfigEntry<float> FlashStrength;
        public ConfigEntry<float> FlashSpeed;
        public ConfigEntry<bool> EnablePainPassout;
        public ConfigEntry<float> PainPassoutTime;
        public ConfigEntry<float> PainPassoutChancePerSec;
        public ConfigEntry<BloodScreenMode> BloodScreenMode;
        public ConfigEntry<float> BloodScreenPersistAlpha;
        public ConfigEntry<float> BloodScreenFadeOut;
        public ConfigEntry<bool> SelfShake;          // master toggle; auto-suppressed if IS present
        public ConfigEntry<float> SelfShakeScale;

        // ---- Wounds / balance ----
        public ConfigEntry<float> MinWoundDamage;
        public ConfigEntry<float> ExplosionShrapnelChance;
        public ConfigEntry<float> InferChestWeight;
        public ConfigEntry<float> InferLimbWeight;
        public ConfigEntry<float> InferHeadWeight;
        public ConfigEntry<float> ShrapnelRemovalBleedChance;
        public ConfigEntry<float> ProcedureTime;

        // ---- Vitals ----
        public ConfigEntry<float> MaxDownedTime;         // fallback death time when downed
        public ConfigEntry<bool> ArrestOnHitEnabled;     // random cardiac arrest when hit
        public ConfigEntry<float> IvAmount;              // blood fraction per IV administration
        public ConfigEntry<float> IvRatePerSec;          // how fast IV blood flows in
        public ConfigEntry<float> IvSpeedPerExtra;       // extra infusion speed per additional attached IV
        public ConfigEntry<int> IvMaxCount;              // max IVs attachable at once
        public ConfigEntry<float> BandageReopenMin;      // min seconds before a bandage reopens
        public ConfigEntry<float> BandageReopenMax;      // max seconds
        public ConfigEntry<float> DiagnoseTimeMin;
        public ConfigEntry<float> DiagnoseTimeMax;
        public ConfigEntry<float> CprTimeMin;
        public ConfigEntry<float> CprTimeMax;
        public ConfigEntry<float> StitchTimeBase;
        public ConfigEntry<float> StitchTimePerExtra;
        public ConfigEntry<float> NormalPulse;
        public ConfigEntry<float> WakeCheckInterval;

        // ---- v3 (physiology / states / wounds / drugs / feedback) ----
        public ConfigEntry<float> BleedingCoefficient, BloodRegenPerSec, CompensateBelowBlood, MaxCompensationHr;
        public ConfigEntry<float> DownBlood, HeartFailBlood, ArrestBleedoutBlood, KnockOutBleeding;
        public ConfigEntry<float> StableBlood, StableSystolic, SpontaneousWakeChance, ArrestTime;
        public ConfigEntry<float> CprChanceMin, CprChanceMax, HeartHitChance, OrganDamageThreshold;
        public ConfigEntry<float> WoundDamageScale, FractureChance, FractureHealTime, BruiseHealTime;
        public ConfigEntry<int> MaxWoundsPerHit;
        public ConfigEntry<float> LimpWalk, LegFractureWalk, LegFractureSprint, ArmFractureSway, ArmSplintSway;
        public ConfigEntry<float> TourniquetPainAfter, TourniquetPainPerSec, TourniquetDebuffFraction;
        public ConfigEntry<float> PainCoefficient, PainKnockoutThreshold, PainKnockoutChance;
        public ConfigEntry<float> AspirinPainReduce, AspirinDuration, AspirinPeak;
        // Wound pain floor (Pain.WoundFloor): per-wound pain by size, per-part weights, dressing factors.
        public ConfigEntry<float> WoundPainMinor, WoundPainMedium, WoundPainLarge, WoundPainFracture, WoundPainMax;
        public ConfigEntry<float> PainWeightHead, PainWeightTorso, PainWeightArm, PainWeightLeg;
        public ConfigEntry<float> BandagedPainFactor, SplintedPainFactor, BruisePainFactor, PainReliefPerSec;
        public ConfigEntry<int> AspirinMaxDose, SyringeMaxDose;
        public ConfigEntry<float> SyringeWakeBoost, EpiDuration;
        public ConfigEntry<float> DesatFromBlood, DesatFullBlood, DesatMaxAlpha;
        public ConfigEntry<bool> HeartbeatSound, UnconsciousOverlay, PeekOnHit;
        public ConfigEntry<float> HeartbeatVolume, HeartbeatFastAbove, HeartbeatSlowBelow;
        public ConfigEntry<float> UnconsciousDarkness, ArrestDarkness, PeekSeconds, PeekSize;

        // ---- Infection ----
        public ConfigEntry<bool> InfectionEnabled;
        public ConfigEntry<float> InfectionOnsetMinutes, InfectionRampMinutes, InfectionPain, InfectionHeartRate;

        // ---- Hit sounds ----
        public ConfigEntry<bool> HitSounds;
        public ConfigEntry<float> HitSoundVolume;
        public ConfigEntry<float> HitSoundMinInterval;

        // ---- Supply ----
        public ConfigEntry<bool> DoubleMedsOnSpawn;
        public ConfigEntry<int> AspirinFullStack;
        public ConfigEntry<int> BandageMultiplier;
        public ConfigEntry<int> MedicBandageMultiplier;
        public ConfigEntry<bool> InjectSpawnKit;
        public ConfigEntry<int> MinBandages;
        public ConfigEntry<int> MinAspirin;

        // ---- Item IDs (resolved from the game; overridable) ----
        public ConfigEntry<string> IdBandage;
        public ConfigEntry<string> IdSyringe;
        public ConfigEntry<string> IdForceps;
        public ConfigEntry<string> IdScissors;
        public ConfigEntry<string> IdSplint;
        public ConfigEntry<string> IdAspirin;

        public Cfg(ConfigFile f)
        {
            Enabled            = f.Bind("General", "Enabled", true, "Master switch for Advanced Medic.");
            OpenKey            = f.Bind("General", "OpenKey", "J", "Key that opens the medical overlay (UnityEngine.KeyCode name).");
            ReplaceVanillaHeal = f.Bind("General", "ReplaceVanillaHeal", true, "Remove the vanilla 'hold to heal' interaction on a soldier (Advanced Medic replaces it via the J overlay). The drag/carry interaction is kept.");
            BandagePromptHideSeconds = f.Bind("General", "BandagePromptHideSeconds", 5f, "Seconds to hide the vanilla interaction key-prompt ('[B]' + bandage icon) after a bandage/heal starts, so it doesn't show over the animation. Covers both the J-overlay heal and a vanilla self-bandage.");
            DebugOverlay       = f.Bind("General", "DebugOverlay", false, "Draw the diagnostic overlay (wounds, hook status). Toggle in-game with DebugOverlayKey.");
            DebugOverlayKey    = f.Bind("General", "DebugOverlayKey", "F8", "Key that toggles the diagnostic overlay.");
            DebugLogging       = f.Bind("General", "DebugLogging", false, "Verbose logging to the BepInEx console (per-hit, treatment, patch list, etc.). Off: only errors and the one startup line are printed. Applies immediately.");
            EnableMedicMark    = f.Bind("MedicMark", "Enabled", true, "Show a red-cross marker over nearby healing sources — ALLIED MEDICS (friendly medic soldiers) and MEDICAL TENTS — so you can find where to get treated.");
            MedicMarkMaxDist   = f.Bind("MedicMark", "MaxDistance", 100f, "Max distance (metres) at which an allied-medic / medical-tent marker shows.");
            MedicMarkSize      = f.Bind("MedicMark", "Size", 12f, "Marker size in pixels (medical tents are drawn slightly larger).");
            MedicMarkBloodThreshold = f.Bind("MedicMark", "ShowBelowBlood", 0.70f, "The healing-source markers only appear when YOU are hurt: bleeding / needs bandaging, has shrapnel, STRONG (high) pain, or blood below this fraction (0.70 = under 70%). Blood at/above the threshold does NOT trigger it. Set 0 to disable the blood trigger.");
            UseGameIcons       = f.Bind("General", "UseGameIcons", true, "Show the game's own item icons on the overlay's action buttons (bundled PNGs until each one arrives). Loaded through the game's own async icon loader, which is safe alongside TextureReplacer / Asset Streaming Overhaul, and cached as PNGs in the plugin's icon_cache folder so they appear instantly in later sessions. Delete that folder to re-fetch them.");

            TreatOtherPlayers  = f.Bind("General", "TreatOtherPlayers", true, "J works on EVERY soldier in range: AI and other human players alike, in single player and multiplayer. Turn off only if treating another player ever crashes (an early build did); J then opens on yourself when you look at another human.");
            BlockWeaponSwitchWhilePanelOpen = f.Bind("UI", "BlockWeaponSwitchWhilePanelOpen", true, "While the medical panel (J) is open, your soldier does not switch weapons, so the mouse wheel only scrolls the wound list and a stray number key does nothing. Only your own soldier, only while the panel is open.");
            TreatRange         = f.Bind("General", "TreatRange", 5f, "J opens on the soldier you're looking at (alive, wounded or not, or downed) within this many metres; otherwise on yourself.");

            ExtraHpMode = f.Bind("HP", "ExtraHpMode", HpMode.PlayerOnly, "Who gets the anti-oneshot damage reduction: Everyone, PlayerOnly, or Noone.");
            DamageScale = f.Bind("HP", "DamageScale", 0.6f, "Incoming damage multiplier for affected soldiers. 0.6 = ~+66% effective HP.");
            OverkillMargin = f.Bind("HP", "OverkillMargin", 3f, "When converting a lethal hit to a knockdown, how far past 0 HP the trimmed damage lands (small = reliably triggers the game's incapacitation).");
            SurviveLifeFrac = f.Bind("HP", "SurviveLifeFrac", 0.45f, "On a 'just wounded' outcome the hit is capped to leave this fraction of health, so the soldier keeps standing.");
            OneShotHpThreshold = f.Bind("HP", "OneShotHpThreshold", 20f, "PLAYER one-shot gate: while you have at least this much HP, a lethal hit NEVER kills you outright — you go down or survive wounded. Only a hit taken when already BELOW this finishes you. Lower = more survivable; 0 = you can never be one-shot at all.");
            AiDownChance = f.Bind("HP", "AiDownChance", 0.12f, "HEAD/CHEST: chance a lethal hit puts an AI into the (revivable) downed state instead of killing. Low = AI die almost like vanilla. A lethal hit on an ARM/LEG uses the Lethality/LimbLethal + LimbDown odds instead (mostly wounded or downed, rarely killed).");
            // Per-body-part outcome split for an otherwise-lethal hit: lethal / downed / (remainder = stay up, just wounded).
            HeadLethal  = f.Bind("Lethality", "HeadLethal", 0.85f, "Head: chance a lethal hit kills outright.");
            HeadDown    = f.Bind("Lethality", "HeadDown", 0.15f, "Head: chance it downs instead (remainder stays standing).");
            ChestLethal = f.Bind("Lethality", "ChestLethal", 0.30f, "Chest: chance a lethal hit kills outright.");
            ChestDown   = f.Bind("Lethality", "ChestDown", 0.50f, "Chest: chance it downs (remainder = just wounded, 20% by default).");
            LimbLethal  = f.Bind("Lethality", "LimbLethal", 0.15f, "Limbs: chance a lethal hit kills outright.");
            LimbDown    = f.Bind("Lethality", "LimbDown", 0.30f, "Limbs: chance it downs (remainder = just wounded, 55% by default).");
            LethalityMult   = f.Bind("Lethality", "LethalityMult", 1.3f, "Multiplier on incoming damage to make hits deadlier (1.3 = +30% lethality). Applied before the anti-oneshot scaling.");
            LethalityAiOnly = f.Bind("Lethality", "LethalityAiOnly", true, "Apply the lethality multiplier to AI soldiers only, leaving the player unchanged.");
            ExplosionDamageMult = f.Bind("Lethality", "ExplosionDamageMult", 3.0f, "Damage multiplier for explosions/grenades (applied INSTEAD of the anti-oneshot reduction). >1 makes a point-blank blast lethal → downs/kills instead of a survivable scratch. Applies to player and AI. Raise it if grenades still feel weak.");
            VehicleOccupantLethal = f.Bind("Lethality", "VehicleOccupantLethal", true, "When an occupant of ANY vehicle (tank, plane, car…) takes an otherwise-lethal hit — the vehicle blown up or destroyed with them aboard, or a plane crash — skip the anti-oneshot down-conversion and let it KILL, player and AI alike. Being inside a wreck is not survivable.");
            MpClientHeadshotKill  = f.Bind("Lethality", "MpClientHeadshotKill", true, "MP CLIENT ON A HOST WITHOUT THIS MOD ONLY: there nobody applies the head-shot rules to your own soldier, so a head-hit flinch on your client rolls HeadLethal to kill (via KillSynched). On a host that runs the mod the machine that computed the hit already rolled it, so this never fires there. Turn off if it ever desyncs with the host.");
            MpClientWoundFromFlinch = f.Bind("Lethality", "MpClientWoundFromFlinch", true, "MP CLIENT ONLY: reconstruct a WOUND on the local client from the hit-flinch's real body part + type, since host-authoritative damage never reaches our wound sensor (the reason a client 'gets hit but never bleeds'). Bullets/headshots use the flinch; grenades use the proximity detector below (GrenadeInstaKill), whose non-lethal explosion wound this switch also gates, in every context.");
            GrenadeInstaKill       = f.Bind("Lethality", "GrenadeInstaKill", true, "Single player, host and client alike: detect an explosion going off near the LOCAL player by world position (the local player's blast damage never reaches our damage hooks), and if it's within GrenadeLethalRadius, roll GrenadeInstaKillChance to KILL, so a grenade at your feet is deadly. Beyond that radius (or if the roll fails) it leaves an explosion wound instead. Never fires while you're in a vehicle, for a blast that deals no damage, or when solid cover blocks the line from the blast to you. Turn off if it ever desyncs with the host.");
            GrenadeInstaKillChance = f.Bind("Lethality", "GrenadeInstaKillChance", 0.90f, "Chance a blast within GrenadeLethalRadius instantly kills the local player (0.90 = 90%). Also the kill share of an otherwise-lethal blast on the damage path.");
            GrenadeLethalRadius    = f.Bind("Lethality", "GrenadeLethalRadius", 4f, "Metres from an explosion within which it can insta-kill the local player (a grenade 'right next to you') when nothing solid is in between. Beyond this but within the blast radius you're wounded instead.");
            HoldVanillaDownedTimer = f.Bind("HP", "HoldVanillaDownedTimer", true, "Hold off the game's own short bleed-out countdown for a downed soldier we manage, so the vitals model decides when they die (cardiac-arrest clock, blood loss, Vitals/MaxDownedTime). Off = the vanilla countdown runs as normal; everything else (revive block, vitals) still works. Replaces the old DownedSurviveTime, whose number was never used as a time.");
            DownedSurviveMode = f.Bind("HP", "DownedSurviveMode", HpMode.Everyone, "Who a lethal hit can DOWN instead of kill (Lethality odds), and whose downed state the vitals model then manages: Everyone (so you can reach and revive downed AI), PlayerOnly, or Noone. In multiplayer a human player (Everyone / PlayerOnly) only gets the blood-loss rules, applied by their own game: collapsing from blood loss or heavy bleeding, and dying when the blood runs out (cardiac arrest) or after Vitals/MaxDownedTime still bleeding; the rest of their downed state (revives, respawn) stays the game's. In EVERY mode, anyone whose blood runs out (0%) dies. Separate from ExtraHpMode so AI can survive downed without also getting damage reduction.");

            KnockdownLock          = f.Bind("Limbs", "KnockdownLock", 0.75f, "Seconds a knockdown pose is held before the player regains stand control.");

            // Hit feedback. A leg/chest hit rolls a knockdown; active bleeding pins an ImmersiveEffects
            // suppression floor (base + arm bonus + pain), slows a bleeding leg, and cuts stamina on a
            // bleeding chest/head.
            EnableHitKnockdown          = f.Bind("HitFeedback", "EnableHitKnockdown", true, "Force a CROUCH drop when hit in the leg/chest (a brief kneel-down reaction). On by default. Crouch-only: the more violent prone snap is disabled because forcing prone mid-hit spun the head oddly. Set HitProneChance > 0 to re-enable prone at your own risk.");
            HitCrouchChance             = f.Bind("HitFeedback", "HitCrouchChance", 0.33f, "Chance a leg or chest hit forces the soldier to crouch (only if EnableHitKnockdown).");
            HitProneChance              = f.Bind("HitFeedback", "HitProneChance", 0f, "Chance a leg or chest hit forces the soldier prone. 0 by default (prone snap caused the head-spin). Raise at your own risk; takes priority over crouch.");
            BleedSuppression            = f.Bind("HitFeedback", "BleedSuppression", 25f, "ImmersiveEffects suppression floor while bleeding anywhere. The floor only ever raises suppression, never lowers it.");
            ArmBleedSuppression         = f.Bind("HitFeedback", "ArmBleedSuppression", 35f, "Extra suppression added on top of the base while an ARM is actively bleeding (base 25 + 35 = 60).");
            PainSuppLight               = f.Bind("HitFeedback", "PainSuppLight", 5f, "Suppression added while in light pain (persists while in pain, even after bleeding stops).");
            PainSuppMed                 = f.Bind("HitFeedback", "PainSuppMed", 10f, "Suppression added while in medium pain.");
            PainSuppHigh                = f.Bind("HitFeedback", "PainSuppHigh", 15f, "Suppression added while in high pain.");
            LegBleedWalk                = f.Bind("HitFeedback", "LegBleedWalk", 0.80f, "Walk/run speed multiplier while a leg is actively bleeding (0.80 = -20%).");
            ChestHeadBleedStaminaFactor = f.Bind("HitFeedback", "ChestHeadBleedStaminaFactor", 0.30f, "Stamina is capped at this fraction of its max while a chest/head wound is actively bleeding (0.30 = total stamina -70%).");

            // AI-medic treatment: look at a nearby AI medic and press the key to have them stitch
            // all your BANDAGED wounds and give you one blood pack, with an animation on both.
            EnableMedicCall = f.Bind("MedicCall", "Enabled", true, "Press the key while looking at a nearby AI medic to have them treat you (stitch bandaged wounds + one blood pack).");
            MedicCallKey    = f.Bind("MedicCall", "Key", "X", "Key to request treatment from a looked-at AI medic (any UnityEngine.KeyCode name).");
            MedicCallRange  = f.Bind("MedicCall", "Range", 5f, "Maximum distance (metres) to the AI medic you're looking at.");
            MedicCallTime   = f.Bind("MedicCall", "Time", 2.5f, "Seconds the treatment takes before it applies (0 = instant). Kept close to the bandage clip length so it doesn't feel laggy.");

            // Medical tent: stand in its vicinity and press the same key for a FULL health reset.
            EnableTentHeal = f.Bind("TentHeal", "Enabled", true, "Stand near a medical tent and press the MedicCall key for a full health reset to standard.");
            TentRange      = f.Bind("TentHeal", "Range", 6f, "How close (metres) you must be to the medical tent.");
            TentHealTime   = f.Bind("TentHeal", "Time", 2.5f, "Seconds the tent treatment takes before it applies (0 = instant).");
            TentToken1     = f.Bind("TentHeal", "NameToken1", "tent", "Object-name substring #1 that identifies a medical tent (case-insensitive); BOTH tokens must be present in a nearby object's name. If it doesn't trigger, enable DebugLogging and press the key by the tent — nearby object names are logged so you can set the right tokens.");
            TentToken2     = f.Bind("TentHeal", "NameToken2", "medic", "Object-name substring #2 (leave blank to match on token #1 alone).");
            TentHealLife   = f.Bind("TentHeal", "StandardLife", 100f, "Health value the tent restores you to (game standard is 100).");

            LightIntensity     = f.Bind("Pain", "LightIntensity", 5f, "Intensity for light pain.");
            MedIntensity       = f.Bind("Pain", "MedIntensity", 10f, "Intensity for medium pain.");
            HighIntensity      = f.Bind("Pain", "HighIntensity", 20f, "Intensity for high pain.");
            PainDeclinePerSec  = f.Bind("Pain", "PainDeclinePerSec", 0.18f, "Pain points (0-100 scale) lost per second without a painkiller, for the pain ABOVE what your current wounds cause (see WoundPain*): 0.18 takes High pain below High in ~5 min and away in ~8. Tiers are bands: High >=67, Medium >=34, Light >=1. Pain never fades below the wound floor while the wounds are still there.");
            AspirinDeclineMult = f.Bind("Pain", "AspirinDeclineMult", 4f, "How much faster pain declines while a painkiller is active (multiplies PainDeclinePerSec). 4 ≈ full High clears in ~1 min instead of ~4. The dose lasts AspirinMin..AspirinMax seconds.");
            AspirinMin         = f.Bind("Pain", "AspirinMin", 70f, "Minimum seconds an aspirin dose keeps pain declining faster (long enough to clear High pain in ~1 min). See AspirinDeclineMult.");
            AspirinMax         = f.Bind("Pain", "AspirinMax", 100f, "Maximum seconds an aspirin dose keeps pain declining faster (it also ends early once pain reaches 0).");
            FlashStrength      = f.Bind("Pain", "FlashStrength", 0.9f, "Peak opacity (0-1) of the white pain flash at max intensity. 0.9 = the standard look; lower = subtler, 0 = no flash.");
            // FlashStrength used to be ignored below 0.9 (the flash drew Max(0.9, value)), so the old 0.14
            // default always LOOKED like 0.9. The setting works now: an untouched old default moves to 0.9,
            // which keeps the look everyone had.
            if (System.Math.Abs(FlashStrength.Value - 0.14f) < 0.0001f) FlashStrength.Value = 0.9f;
            FlashSpeed         = f.Bind("Pain", "FlashSpeed", 1.6f, "Pulse speed (rad/s) of the pain flash.");
            EnablePainPassout       = f.Bind("Pain", "EnablePainPassout", true, "After sustained HIGH pain you can pass out into the downed state (needs reviving). Applies to soldiers whose downed state the mod owns (SP: everyone; MP: AI — an MP human's downed/respawn stays vanilla).");
            PainPassoutTime         = f.Bind("Pain", "PainPassoutTime", 120f, "Seconds of CONTINUOUS high pain before a pass-out becomes possible (~2 min). Drops to zero-risk the moment pain falls below High.");
            PainPassoutChancePerSec = f.Bind("Pain", "PainPassoutChancePerSec", 0.02f, "Once past PainPassoutTime, chance per second of passing out (0.02 ≈ ~50/50 within the next ~35 s). Higher = passes out sooner after the threshold.");
            BloodScreenMode         = f.Bind("BloodScreen", "Mode", AdvancedMedic.BloodScreenMode.Persistent, "Play the game's blood-splash screen effect when you're hit and actively bleeding. Off = never; Once = one splash per bleeding hit, fades on its own; Persistent = a splash is HELD steadily on screen (not replayed) until every bleeding wound is bandaged.");
            BloodScreenPersistAlpha = f.Bind("BloodScreen", "PersistAlpha", 1.0f, "Persistent mode: opacity the held blood splash is pinned at (0-1). Lower it if a full-strength blood screen obscures too much.");
            BloodScreenFadeOut      = f.Bind("BloodScreen", "FadeOutSeconds", 1.0f, "Persistent mode: seconds the held blood splash takes to fade away once the last bleeding wound is bandaged.");
            SelfShake          = f.Bind("Pain", "SelfShake", true, "Apply a self-contained camera shake for pain. Auto-suppressed if ImmersiveEffects is installed.");
            SelfShakeScale     = f.Bind("Pain", "SelfShakeScale", 0.4f, "Scale of the self-contained pain shake.");

            MinWoundDamage             = f.Bind("Wounds", "MinWoundDamage", 2f, "Minimum damage a hit must deal to leave a wound (ignores tiny scratches). Lower = more hits draw blood.");
            ExplosionShrapnelChance    = f.Bind("Wounds", "ExplosionShrapnelChance", 0.6f, "Chance an explosion/grenade hit embeds shrapnel (needs scissors then forceps to remove). Explosions always also leave a bleeding wound.");
            // Weights for the HP-watchdog inferred wound (an MP-client hit whose location we never got).
            // Relative — they don't need to sum to 100. Default 40 chest / 30 any-limb / 10 head.
            InferChestWeight           = f.Bind("Wounds", "InferChestWeight", 40f, "Weight for an inferred wound landing on the chest (host-side MP hits of unknown location).");
            InferLimbWeight            = f.Bind("Wounds", "InferLimbWeight", 30f, "Weight for an inferred wound landing on a limb (a random arm or leg).");
            InferHeadWeight            = f.Bind("Wounds", "InferHeadWeight", 10f, "Weight for an inferred wound landing on the head.");
            ShrapnelRemovalBleedChance = f.Bind("Wounds", "ShrapnelRemovalBleedChance", 0.25f, "Chance that removing shrapnel with forceps opens a bleeding wound that then needs a bandage.");
            ProcedureTime      = f.Bind("Wounds", "ProcedureTime", 4f, "Seconds a bandage / tourniquet / splint / scissors / forceps / syringe / aspirin / blood IV treatment takes: the overlay stays hidden while the heal animation plays, then the effect applies. Minimum 0.5.");

            MaxDownedTime         = f.Bind("Vitals", "MaxDownedTime", 300f, "Fallback: a downed soldier who is STILL BLEEDING after this many seconds dies. A bandaged (not bleeding) patient is never killed by this clock.");
            ArrestOnHitEnabled    = f.Bind("Vitals", "ArrestOnHitEnabled", true, "A wounding hit has a small random chance to trigger a traumatic cardiac arrest (pulse to 0 → collapse → needs CPR within ArrestTime or death).");
            IvAmount              = f.Bind("Vitals", "IvAmount", 0.25f, "Blood-pool fraction added per IV administration (flows in over time).");
            IvRatePerSec          = f.Bind("Vitals", "IvRatePerSec", 0.0021f, "How fast ONE IV's blood flows into the pool per second (a 25% dose takes ~2.0 min).");
            IvSpeedPerExtra       = f.Bind("Vitals", "IvSpeedPerExtra", 0.5f, "Each additional attached IV speeds infusion by this fraction: 2 IVs = 1.5x, 3 = 2.0x, 4 = 2.5x.");
            IvMaxCount            = f.Bind("Vitals", "IvMaxCount", 4, "Maximum IVs attachable to one soldier at once.");
            BandageReopenMin      = f.Bind("Vitals", "BandageReopenMin", 120f, "Min seconds before a bandaged wound may reopen.");
            BandageReopenMax      = f.Bind("Vitals", "BandageReopenMax", 300f, "Max seconds before a bandaged wound may reopen.");
            DiagnoseTimeMin       = f.Bind("Vitals", "DiagnoseTimeMin", 1.7f, "Min diagnose duration (s).");
            DiagnoseTimeMax       = f.Bind("Vitals", "DiagnoseTimeMax", 3f, "Max diagnose duration (s).");
            CprTimeMin            = f.Bind("Vitals", "CprTimeMin", 12f, "Min CPR duration (s).");
            CprTimeMax            = f.Bind("Vitals", "CprTimeMax", 16f, "Max CPR duration (s).");
            StitchTimeBase        = f.Bind("Vitals", "StitchTimeBase", 6f, "Base stitch duration (s) for one wound.");
            StitchTimePerExtra    = f.Bind("Vitals", "StitchTimePerExtra", 4f, "Extra stitch seconds per additional wound on the limb.");
            NormalPulse           = f.Bind("Vitals", "NormalPulse", 80f, "Resting heart rate. With the v3 physiology 80 bpm gives a blood pressure of 120/80; higher reads higher.");
            WakeCheckInterval     = f.Bind("Vitals", "WakeCheckInterval", 10f, "How often (s) a downed soldier rolls to wake up when all vitals are in range. NOT tied to the syringe.");

            // ---- v3 vitals. Values softened where marked "EASY" so ER2 stays a fairly forgiving game
            // (some maps have no medics or tents at all). ----
            const string V = "Vitals";
            BleedingCoefficient  = f.Bind(V, "BleedingCoefficient", 1.0f, "Bleeding coefficient. Blood lost per second = wound bleeding x cardiac output x this. 1 = default (one rifle wound loses ~40% blood in ~4 min untreated).");
            BloodRegenPerSec     = f.Bind(V, "BloodRegenPerSec", 0.0008f, "EASY: blood the body rebuilds by itself per second while nothing is bleeding and the heart beats (0.0008 = ~5% per minute), so maps with no medics/IVs stay playable. 0 = off (only an IV restores blood).");
            CompensateBelowBlood = f.Bind(V, "CompensateBelowBlood", 0.85f, "Below this blood the heart speeds up to hold blood pressure (class II hemorrhage and worse). 0.85 is realistic and keeps a bleeding soldier conscious longer.");
            MaxCompensationHr    = f.Bind(V, "MaxCompensationHr", 175f, "EASY: highest heart rate blood loss, pain and drugs together can drive. Keeps a self-medicating soldier out of the fatal zone (HR > 220, or diastolic >= 190, which the blood-pressure model reaches near 190 bpm).");
            DownBlood            = f.Bind(V, "DownBlood", 0.60f, "Blood below this collapses the soldier (unconscious / downed). Class IV hemorrhage.");
            HeartFailBlood       = f.Bind(V, "HeartFailBlood", 0.45f, "Blood below this and the heart fails: the pulse sinks until it stops (cardiac arrest). (EASY)");
            ArrestBleedoutBlood  = f.Bind(V, "ArrestBleedoutBlood", 0.30f, "A soldier in cardiac arrest whose blood falls below this dies (bleed-out).");
            KnockOutBleeding     = f.Bind(V, "KnockOutBleeding", 0.6f, "Total wound bleeding (1 = a fully open chest) above which the soldier collapses. (EASY)");
            StableBlood          = f.Bind(V, "StableBlood", 0.70f, "Minimum blood for STABLE vitals, needed to wake up. (EASY)");
            StableSystolic       = f.Bind(V, "StableSystolic", 60f, "Minimum systolic blood pressure for stable vitals.");
            SpontaneousWakeChance = f.Bind(V, "SpontaneousWakeChance", 0.30f, "Chance a downed soldier with stable vitals wakes up on each check (every WakeCheckInterval s). (EASY)");
            ArrestTime           = f.Bind(V, "ArrestTime", 300f, "Seconds a soldier survives in cardiac arrest (+-10%). The clock runs at half speed while someone gives CPR.");
            CprChanceMin         = f.Bind(V, "CprChanceMin", 0.40f, "CPR success chance at 60% blood or less.");
            CprChanceMax         = f.Bind(V, "CprChanceMax", 0.60f, "CPR success chance at 85% blood or more. (EASY)");
            HeartHitChance       = f.Bind(V, "HeartHitChance", 0.05f, "Chance a heavy chest wound (above OrganDamageThreshold) hits the heart and stops it.");
            OrganDamageThreshold = f.Bind(V, "OrganDamageThreshold", 0.6f, "Wound damage (wound-table scale) a chest wound needs before it can hit the heart.");

            const string W = "Wounds";
            WoundDamageScale       = f.Bind(W, "WoundDamageScale", 0.02f, "Converts ER2 hit damage to the wound-table scale (0.02: a 25-damage pistol hit = 0.5 = a medium bleeding wound, a 150-damage rifle hit = 3 = one large wound). Below ~0.35 on that scale a bullet only bruises. Raise for bigger/more wounds per hit.");
            MaxWoundsPerHit   = f.Bind(W, "MaxWoundsPerHit", 6, "EASY: cap on the wounds one hit creates.");
            FractureChance    = f.Bind(W, "FractureChance", 0.35f, "Chance a heavy limb wound (velocity/crush, damage > 0.5) breaks the bone. (EASY)");
            FractureHealTime  = f.Bind(W, "FractureHealTime", 600f, "EASY: seconds after splinting until a fracture heals by itself (so maps without medics don't leave you crippled). 0 = never.");
            BruiseHealTime    = f.Bind(W, "BruiseHealTime", 180f, "Seconds until a non-bleeding bruise/burn clears by itself.");
            LimpWalk          = f.Bind(W, "LimpWalk", 0.96f, "Walk AND sprint speed multiplier per leg that still has an UNSTITCHED wound (open or bandaged) or fracture (0.96 = -4%). Multiplies with ImmersiveEffects' load/sprint multipliers.");
            LegFractureWalk   = f.Bind(W, "LegFractureWalk", 0.85f, "Extra speed multiplier on an UNSPLINTED broken leg (splinting removes it).");
            LegFractureSprint = f.Bind(W, "LegFractureSprint", 0.80f, "Extra multiplier while SPRINTING on an unsplinted broken leg.");
            ArmFractureSway   = f.Bind(W, "ArmFractureSway", 20f, "ImmersiveEffects stress/sway floor added per unsplinted broken arm.");
            ArmSplintSway     = f.Bind(W, "ArmSplintSway", 6f, "Stress/sway floor per splinted broken arm.");
            TourniquetPainAfter  = f.Bind(W, "TourniquetPainAfter", 120f, "Seconds a tourniquet can stay on before it starts to hurt.");
            TourniquetPainPerSec = f.Bind(W, "TourniquetPainPerSec", 0.1f, "Pain (0-100) added per second once a tourniquet is left on too long.");
            TourniquetDebuffFraction = f.Bind(W, "TourniquetDebuffFraction", 0.5f, new ConfigDescription(
                "A tourniquet isn't free: a tourniqueted limb applies this fraction of the debuff a BLEEDING limb of the same kind would. Leg = that share of HitFeedback/LegBleedWalk's slowdown (0.5 with the default 0.80 = -10% instead of -20%); arm = that share of HitFeedback/ArmBleedSuppression's sway (needs ImmersiveEffects). 0 = free, at most half.",
                new AcceptableValueRange<float>(0f, 0.5f)));

            const string P = "Pain";
            PainCoefficient       = f.Bind(P, "PainCoefficient", 0.8f, "Multiplier on the pain wounds cause: both a hit's own pain spike and the wound floor below. (EASY)");

            // Wound pain floor: real pain never fades below the SUM of what the current wounds cause.
            // Each active wound adds WoundPain<size> x PainWeight<part> (x BandagedPainFactor once
            // dressed), all x PainCoefficient, capped at WoundPainMax. Defaults (x0.8): a medium chest
            // wound ~25 (Light), two ~49 (Medium), three ~74 and four ~98 (High); a large limb wound ~35-38
            // (Medium); four bandaged medium chest wounds ~49 (Medium).
            WoundPainMinor     = f.Bind(P, "WoundPainMinor", 10f, "Pain (0-100, before part weight and PainCoefficient) one open MINOR wound keeps up while it's on you.");
            WoundPainMedium    = f.Bind(P, "WoundPainMedium", 22f, "Pain one open MEDIUM wound keeps up (a pistol-class hit).");
            WoundPainLarge     = f.Bind(P, "WoundPainLarge", 48f, "Pain one open LARGE wound keeps up (a rifle-class hit).");
            WoundPainFracture  = f.Bind(P, "WoundPainFracture", 25f, "Pain an unsplinted broken bone keeps up (see SplintedPainFactor).");
            WoundPainMax       = f.Bind(P, "WoundPainMax", 100f, "Cap on the pain the wounds together keep up (0-100). 0 = no wound floor (pain only from the hit spike, which fades — the pre-2026-09-27 behaviour).");
            PainWeightHead     = f.Bind(P, "PainWeightHead", 1.5f, "Pain multiplier for wounds on the head.");
            PainWeightTorso    = f.Bind(P, "PainWeightTorso", 1.4f, "Pain multiplier for wounds on the chest/torso.");
            PainWeightArm      = f.Bind(P, "PainWeightArm", 0.9f, "Pain multiplier for wounds on an arm.");
            PainWeightLeg      = f.Bind(P, "PainWeightLeg", 1.0f, "Pain multiplier for wounds on a leg.");
            BandagedPainFactor = f.Bind(P, "BandagedPainFactor", 0.5f, "A bandaged (not yet stitched) wound keeps this fraction of its pain. Stitched = no pain.");
            SplintedPainFactor = f.Bind(P, "SplintedPainFactor", 0.4f, "A splinted fracture keeps this fraction of its pain until it heals.");
            BruisePainFactor   = f.Bind(P, "BruisePainFactor", 0.5f, "A non-bleeding bruise/abrasion keeps this fraction of its size's pain (burns hurt fully).");
            PainReliefPerSec   = f.Bind(P, "PainReliefPerSec", 0.02f, "While a painkiller (aspirin) is active: fraction of the pain ABOVE the wound floor that fades per second, before AspirinDeclineMult. Without aspirin only PainDeclinePerSec applies, so untreated pain crawls down over minutes.");
            PainKnockoutThreshold = f.Bind(P, "PainKnockoutThreshold", 50f, "A single hit causing at least this much pain (0-100) can knock the soldier out.");
            PainKnockoutChance    = f.Bind(P, "PainKnockoutChance", 0.05f, "Chance such a hit knocks out. (EASY)");
            AspirinPainReduce     = f.Bind(P, "AspirinPainReduce", 60f, "Pain (0-100) one aspirin MASKS at full effect; the pain returns as it wears off. (EASY)");
            AspirinDuration       = f.Bind(P, "AspirinDuration", 420f, "Seconds an aspirin stays in the system.");
            AspirinPeak           = f.Bind(P, "AspirinPeak", 30f, "Seconds until an aspirin reaches full effect. (EASY)");
            AspirinMaxDose        = f.Bind(P, "AspirinMaxDose", 6, "Aspirin doses in the system (+-1) before an overdose knocks the soldier out.");
            SyringeMaxDose        = f.Bind(P, "SyringeMaxDose", 9, "Syringe (epinephrine) doses in the system (+-2) before an overdose.");
            SyringeWakeBoost      = f.Bind(P, "SyringeWakeBoost", 2.0f, "While a syringe (epinephrine) is active, the spontaneous wake-up chance is multiplied by this.");
            EpiDuration           = f.Bind(P, "EpiDuration", 120f, "Seconds a syringe (epinephrine) stays in the system.");

            const string F = "Feedback";
            DesatFromBlood      = f.Bind(F, "DesatFromBlood", 0.85f, "Blood level where the screen starts losing colour.");
            DesatFullBlood      = f.Bind(F, "DesatFullBlood", 0.60f, "Blood level where the colour loss is at its strongest.");
            DesatMaxAlpha       = f.Bind(F, "DesatMaxAlpha", 0.65f, "Strength of the grey wash at DesatFullBlood.");
            HeartbeatSound      = f.Bind(F, "HeartbeatSound", true, "Hear your own heartbeat when it races or crawls. Synthesised at runtime, no sound files.");
            HeartbeatVolume     = f.Bind(F, "HeartbeatVolume", 0.6f, "Heartbeat volume (0-1).");
            HeartbeatFastAbove  = f.Bind(F, "HeartbeatFastAbove", 150f, "Heart rate above which you hear it.");
            HeartbeatSlowBelow  = f.Bind(F, "HeartbeatSlowBelow", 55f, "Heart rate below which you hear it.");
            UnconsciousOverlay  = f.Bind(F, "UnconsciousOverlay", true, "Darken the screen while you're downed, drifting in and out every 15-20 s. Deeper in cardiac arrest.");
            UnconsciousDarkness = f.Bind(F, "UnconsciousDarkness", 0.55f, "How dark the screen gets while unconscious (0-1). Kept light enough to watch for a medic.");
            ArrestDarkness      = f.Bind(F, "ArrestDarkness", 0.80f, "How dark the screen gets in cardiac arrest (0-1).");
            PeekOnHit           = f.Bind(F, "PeekOnHit", false, "Briefly show a small body diagram of your wounds after being hit.");
            PeekSeconds         = f.Bind(F, "PeekSeconds", 5f, "How long the hit peek stays up.");
            PeekSize            = f.Bind(F, "PeekSize", 150f, "Size of the hit-peek diagram in pixels.");

            const string I = "Infection";
            InfectionEnabled      = f.Bind(I, "Enabled", true, "A bandaged wound that is not stitched slowly gets infected: it hurts more and the pulse creeps up. Stitching it (a medic), an AI medic's second X treatment or a medical tent clears it. Never fatal by itself, and infection pain never knocks anyone out or keeps them from waking up.");
            InfectionOnsetMinutes = f.Bind(I, "OnsetMinutes", 6f, "Minutes after the wound was made before a dressed-but-unstitched wound starts to get infected. It only progresses while the wound is dressed.");
            InfectionRampMinutes  = f.Bind(I, "RampMinutes", 3f, "Minutes from the first sign of infection to fully infected.");
            InfectionPain         = f.Bind(I, "PainAdded", 12f, "Extra pain (0-100 scale, before the body-part weight and PainCoefficient) a FULLY infected wound keeps up, on top of its bandaged pain. Partial infection adds its share.");
            InfectionHeartRate    = f.Bind(I, "HeartRateAdded", 15f, "Beats per minute added to the heart-rate target by the most infected wound at full infection (a fever). Still capped by MaxCompensationHr, so it can't push the heart into the fatal zone.");

            const string S = "HitSounds";
            HitSounds           = f.Bind(S, "Enabled", true, "Play a hurt sound when YOU are wounded, from your own WAV files in BepInEx/plugins/ER2AdvancedMedic/sounds/ named hitLight1..3.wav, hitMedium1..3.wav and hitHeavy1..3.wav (any may be missing; a random one of the hit's tier plays, or the nearest tier that has files). Light = only minor wounds / bruises; Medium = a medium wound or embedded shrapnel; Heavy = a large wound, a broken bone, or a hit that downs you or stops your heart. No files = silence. New files are picked up on the next game start.");
            HitSoundVolume      = f.Bind(S, "Volume", 0.8f, new ConfigDescription("Hurt sound volume (0-1).", new AcceptableValueRange<float>(0f, 1f)));
            HitSoundMinInterval = f.Bind(S, "MinInterval", 0.4f, "Minimum seconds between two hurt sounds, so a burst of hits doesn't stack them.");


            DoubleMedsOnSpawn  = f.Bind("Supply", "DoubleMedsOnSpawn", true, "On spawn: multiply each soldier's bandages (see BandageMultiplier / MedicBandageMultiplier), and give a full aspirin stack to anyone carrying none.");
            BandageMultiplier  = f.Bind("Supply", "BandageMultiplier", 2, "How many times their own bandages an ordinary soldier spawns with (2 = double). 1 = leave them alone. A soldier who carries no bandages still gets none.");
            MedicBandageMultiplier = f.Bind("Supply", "MedicBandageMultiplier", 3, "Same for MEDICS (soldiers the game flags as a medic / carrying a syringe) — 3 = triple, so they can treat the squad.");
            AspirinFullStack   = f.Bind("Supply", "AspirinFullStack", 4, "Size of the aspirin stack handed to a soldier who spawns without any aspirin.");
            InjectSpawnKit     = f.Bind("Supply", "InjectSpawnKit", false, "Top the soldier YOU play up to a minimum medical kit (MinBandages / MinAspirin), once per soldier you take control of. AI soldiers are not affected (their kit is DoubleMedsOnSpawn's).");
            MinBandages        = f.Bind("Supply", "MinBandages", 1, "Minimum bandages you are topped up to (if InjectSpawnKit).");
            MinAspirin         = f.Bind("Supply", "MinAspirin", 1, "Minimum aspirin you are topped up to (if InjectSpawnKit).");


            // Confirmed in-game via the inventory dump: bandages="bandages", syringe="syringe".
            // Scissors/pliers match the native Soldier.InjureType {scissors, pliers, syringe}
            // (best-guess ids). Blank id = treated as always-available (a free action).
            // All confirmed from the medic's live inventory dump.
            IdBandage  = f.Bind("Items", "IdBandage", "bandages", "Item id for bandages (confirmed).");
            IdSyringe  = f.Bind("Items", "IdSyringe", "syringe", "Item id for syringe (confirmed).");
            IdForceps  = f.Bind("Items", "IdForceps", "splinter_scissors", "Item id for splinter forceps (confirmed).");
            IdScissors = f.Bind("Items", "IdScissors", "bullet_scissors", "Item id for bullet/bandage scissors (confirmed).");
            IdSplint   = f.Bind("Items", "IdSplint", "", "No vanilla splint item; blank = always available (free action).");
            IdAspirin  = f.Bind("Items", "IdAspirin", "aspirine", "Item id for aspirin (confirmed; note spelling 'aspirine').");

            // Keys earlier versions bound and later removed: forget them so the .cfg stops carrying them
            // (BepInEx keeps an unbound key's line forever otherwise). Saved once, only if one was there.
            bool dropped = false;
            foreach (var (section, key) in RetiredKeys) dropped |= DropOrphan(f, section, key);
            if (dropped) { try { f.Save(); } catch { } }
        }

        /// <summary>Every key an earlier version wrote that nothing binds any more (the v2 vitals, the old
        /// limb/pain/syringe tuning, the withdrawn downed self-aid, ...). Add a key here when removing it.</summary>
        private static readonly (string section, string key)[] RetiredKeys =
        {
            ("General", "TreatWhileTimeRuns"),
            ("HP", "DownedSurviveTime"),
            ("Limbs", "ArmIntensityFloor"), ("Limbs", "ChestFallChance"), ("Limbs", "ChestFallProneVsCrouch"),
            ("Limbs", "ChestIntensityFloor"), ("Limbs", "ChestWalk"), ("Limbs", "LegFallChance"), ("Limbs", "LegWalk"),
            ("MP", "TreatAiInMp"),
            ("MedicMark", "MedicOnly"),
            ("Pain", "HighDecay"), ("Pain", "LightDecay"), ("Pain", "MedDecay"), ("Pain", "PainChancePerWound"),
            ("Supply", "LootBodies"),
            ("Syringe", "HealRateMultiplier"), ("Syringe", "ReviveEnabled"), ("Syringe", "SyringeHealAmount"),
            ("Vitals", "AdminBoostTime"), ("Vitals", "ArrestBloodThreshold"), ("Vitals", "ArrestOnHitBase"),
            ("Vitals", "ArrestOnHitMax"), ("Vitals", "ArrestOnHitPerWound"), ("Vitals", "AspirinRampTime"),
            ("Vitals", "BloodDrainPerSec"), ("Vitals", "BloodWakeMin"), ("Vitals", "BpWakeMax"), ("Vitals", "BpWakeMin"),
            ("Vitals", "CprChanceHigh"), ("Vitals", "CprChanceLow"), ("Vitals", "CprPulseRestore"),
            ("Vitals", "DesatStartBlood"), ("Vitals", "DrugDecayRate"), ("Vitals", "DrugHoldMax"), ("Vitals", "DrugHoldMin"),
            ("Vitals", "DrugPulseMax"), ("Vitals", "DrugPulseMin"), ("Vitals", "HighPulseDeathTime"),
            ("Vitals", "HighPulseDown"), ("Vitals", "HighPulseLethal"), ("Vitals", "NoPulseDeathTime"),
            ("Vitals", "NormalBp"), ("Vitals", "PulseWakeMax"), ("Vitals", "PulseWakeMin"), ("Vitals", "StabilizeFastRate"),
            ("Vitals", "StabilizeRate"), ("Vitals", "SyringeBp"), ("Vitals", "SyringeRampTime"), ("Vitals", "WakeupChance"),
            ("Vitals", "SelfAidWhileDowned"), ("Vitals", "SelfAidTimeFactor"),
            ("Wounds", "ActionBaseTime"), ("Wounds", "BleedDrainPerSec"), ("Wounds", "DamageToAce"),
            ("Wounds", "ImprovisedAllowed"), ("Wounds", "ImprovisedSuccess"), ("Wounds", "ImprovisedTimeMult"),
            ("Wounds", "ShrapnelChance"), ("Wounds", "ShrapnelRebleedMax"), ("Wounds", "ShrapnelRebleedMin"),
        };

        /// <summary>Forget a key that is no longer bound, so the next Save doesn't write it back. True if it
        /// was there.</summary>
        private static bool DropOrphan(ConfigFile f, string section, string key)
        {
            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop?.GetValue(f) is System.Collections.Generic.Dictionary<ConfigDefinition, string> orphans)
                    return orphans.Remove(new ConfigDefinition(section, key));
            }
            catch { }
            return false;
        }
    }
}
