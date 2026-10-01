using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Anti-oneshot: scale incoming damage by DamageScale (ExtraHpMode), then decide what an
    /// otherwise-lethal hit does by body part (Lethality odds): kill, DOWN (trimmed to just-lethal, so
    /// the game incapacitates instead of killing) or WOUND (trimmed to leave SurviveLifeFrac). The
    /// healthy player is never one-shot except by a headshot or a blast (DESIGN §6, §12).
    ///
    /// Process() is called from BOTH damage entry points (Soldier.DamageWithAnimation and
    /// Soldier.Damage) behind a re-entrancy guard, so whichever the game uses for a given hit is
    /// covered exactly once.
    /// </summary>
    internal static class HpLogic
    {
        // Set true while a DamageWithAnimation call is in flight so the inner Damage call it makes
        // isn't scaled a second time.
        public static bool InDamageWithAnimation;

        /// <summary>Controls the downed CONVERSION (trim an otherwise-lethal hit to just-lethal so the
        /// game incapacitates instead of instant-killing). Applies to MP players too, so you "go
        /// injured first" — the resulting downed state is the game's own, so vanilla respawn still runs.</summary>
        public static bool ConvertApplies(Soldier s) => InMode(s, Plugin.C.DownedSurviveMode.Value);

        /// <summary>Controls OUR downed management (vanilla timer hold, vitals-driven death, revive
        /// block). NOT for a human player in MP — that layer is what stuck you in a movable, cursor-less
        /// limbo and blocked the vanilla class-selection; let the game own a downed player's fate there.
        /// In MP it is additionally limited to the soldier's authority (Vitals.OwnsState). An MP human's
        /// own machine still applies the blood-loss rules to its avatar (Vitals.ControlOf: BleedOut) -
        /// through the game's own down / revive / synced kill, never this layer.</summary>
        public static bool DownedApplies(Soldier s) { return DownedApplies(s, Interop.InMpRoom()); }

        /// <summary>DownedApplies with the Photon room state handed in. Whether we are in a room
        /// cannot change inside a frame, so DownedLogic.TickAll reads it once for its whole pass
        /// rather than once per downed soldier, where it was a native call per soldier per frame.</summary>
        public static bool DownedApplies(Soldier s, bool inRoom)
        {
            // A human in a room: the game's own player-controlled flag (Interop.IsHuman), which reads the
            // same on every machine - not IsAI(), which is false for every AI on a client.
            if (inRoom && s != null && Interop.IsHuman(s)) return false;
            return ConvertApplies(s);
        }

        private static bool InMode(Soldier s, HpMode mode)
        {
            if (s == null || mode == HpMode.Noone) return false;
            // "Player" = any human-played soldier: on the host a CLIENT's avatar takes its damage here
            // too, and must get the players' rules, not the AI's.
            if (mode == HpMode.PlayerOnly && !Interop.IsHuman(s)) return false;
            return true;
        }

        /// <summary>
        /// Scale + downed-conversion. Scaling (extra survivability) follows ExtraHpMode. The
        /// downed conversion follows DownedSurviveMode (default Everyone): the game instant-kills
        /// on big OVERKILL but only incapacitates on a marginally-lethal hit, so for an otherwise
        /// lethal hit we (DownedChance of the time) trim the damage to just-lethal, dropping the
        /// soldier into the downed state instead of killing them.
        /// </summary>
        public static void Process(Soldier s, ref float dam, BodyPartType part, bool explosion)
        {
            if (dam <= 0f) return;
            // WHOSE rules: this machine's (SP / host / our own soldier on an unmodded host), the host's
            // published ones (modded host), or none = vanilla (DamageRules.For).
            var c = DamageRules.For(s);
            if (c == null) return;

            bool scale = InMode(s, c.ExtraHpMode);
            bool downed = InMode(s, c.DownedSurviveMode);   // the trim-to-downed conversion (runs for MP players too)
            if (!scale && !downed) return;

            float before = dam;

            if (explosion)
            {
                // Explosions/grenades bypass the anti-oneshot survivability reduction and hit HARDER,
                // so standing on a grenade is properly lethal (→ down or kill) instead of a survivable
                // scratch. Your bullet-tankiness simply doesn't apply to a point-blank blast.
                dam *= Mathf.Max(0.1f, c.ExplosionDamageMult);
            }
            else
            {
                // Lethality boost: make hits deadlier (optionally AI-only). Before the reduction so
                // the boosted damage also feeds the downed/kill decision.
                if (c.LethalityMult > 0f && c.LethalityMult != 1f)
                {
                    if (!c.LethalityAiOnly || !Interop.IsHuman(s)) dam *= c.LethalityMult;
                }
                if (scale) dam *= Mathf.Clamp(c.DamageScale, 0.05f, 1f);
            }

            string note = "";
            if (downed)
            {
                int life = CurrentLife(s);
                if (life > 0 && dam >= life) note = ResolveLethal(c, s, ref dam, part, explosion, life);   // this hit would otherwise kill
            }

            if (Plugin.C.DebugLogging.Value)
            {
                bool who = Interop.IsPlayer(s);
                Plugin.L.LogInfo($"[hp] {(who ? "PLAYER" : "ai")} {part} dam {before:0.0} -> {dam:0.0} (life={CurrentLife(s)} scale={scale} expl={explosion} rules={c.Source}{note})");
            }
        }

        /// <summary>
        /// What an otherwise-lethal hit does (the downed conversion): KILL (dam stays >= life), DOWN (trim to
        /// just past lethal so the game incapacitates) or WOUND (trim to leave SurviveLifeFrac). Returns the
        /// log note.
        /// </summary>
        private static string ResolveLethal(DamageRules c, Soldier s, ref float dam, BodyPartType part, bool explosion, int life)
        {
            // Human-played (the local player, or another player's avatar on any machine - the game's own
            // player-controlled flag, Interop.IsOtherHuman): the player rules below.
            bool isPlayer = Interop.IsHuman(s);
            bool local = Interop.IsPlayer(s);
            float downDam = life + Mathf.Max(0f, c.OverkillMargin);
            float woundDam = Mathf.Max(1f, life * (1f - Mathf.Clamp01(c.SurviveLifeFrac)));

            // Vehicle blown up / destroyed with the occupant aboard (tank, plane, car…), or a plane crash:
            // NO down-conversion — the hit kills (dam already >= life). Player + AI. A lethal hit only reaches
            // an occupant once the vehicle's protection is gone, so being aboard for it is enough.
            if (c.VehicleOccupantLethal && Interop.IsOnVehicle(s)) return " -> VEHICLE KILL";

            if (isPlayer)
            {
                // PLAYER: while you were healthy (>= OneShotHpThreshold before the hit) a lethal hit NEVER
                // kills you outright — you go DOWN or survive badly WOUNDED, so you always end up with
                // something to treat. Only a hit taken when already below the threshold finishes you, so
                // you're not literally un-killable. Set the threshold to 0 to never be one-shot at all. (In
                // MP this still runs — injured-first — with the downed FUSE off so vanilla respawn works: see
                // DownedApplies.)
                if (life < c.OneShotHpThreshold) return " -> KILL(finish)";   // already below threshold

                GetOdds(c, part, out float lethal, out float down);
                float roll = UnityEngine.Random.value;
                if (part == BodyPartType.head)
                {
                    // HEADSHOTS stay lethal even when healthy (no un-killable headshots) — use the full
                    // kill/down/wound odds (HeadLethal defaults to 85% kill).
                    if (roll < lethal) return " -> KILL(head)";
                    if (roll < lethal + down) { dam = downDam; return " -> DOWN(head)"; }
                    dam = woundDam; return " -> WOUND(head)";
                }
                if (explosion)
                {
                    // A blast strong enough to be lethal (i.e. close) mostly KILLS — occasionally just downs.
                    // Matches the "grenade right next to me = ~90% death" rule; the MP-client path does the
                    // same by proximity (Hooks.CreateExplosion_Post). This blast is then resolved for the
                    // local player: the proximity check must not roll it a second time.
                    if (local) Hooks.PlayerBlastRolledSeq = Hooks.BlastSeq;
                    if (UnityEngine.Random.value < Mathf.Clamp01(c.GrenadeInstaKillChance)) return " -> KILL(expl)";
                    dam = downDam; return " -> DOWN(expl)";
                }
                // Body/limbs: anti-oneshot — down or wounded, never outright killed while healthy.
                if (roll < down) { dam = downDam; return " -> DOWN"; }
                dam = woundDam; return " -> WOUND";
            }

            if (Body.IsLimb(part) && !explosion)
            {
                // AI shot in an ARM/LEG: a limb hit alone rarely kills. Use the limb odds — mostly left
                // standing wounded, sometimes downed, rarely killed (a rifle leg shot is already lethal in
                // vanilla ER2, ~105 damage vs 100 life).
                GetOdds(c, part, out float lethal, out float down);
                float roll = UnityEngine.Random.value;
                if (roll < lethal) return " -> AI KILL(limb)";
                if (roll < lethal + down) { dam = downDam; return " -> AI DOWN(limb)"; }
                dam = woundDam; return " -> AI WOUND(limb)";
            }

            // AI head/chest: die almost like vanilla, with just a slight chance to go DOWN (revivable).
            if (UnityEngine.Random.value < Mathf.Clamp01(c.AiDownChance)) { dam = downDam; return " -> AI DOWN"; }
            return " -> AI KILL";
        }

        private static void GetOdds(DamageRules c, BodyPartType part, out float lethal, out float down)
        {
            switch (part)
            {
                case BodyPartType.head:  lethal = c.HeadLethal;  down = c.HeadDown;  break;
                case BodyPartType.chest: lethal = c.ChestLethal; down = c.ChestDown; break;
                case BodyPartType.arm_l:
                case BodyPartType.arm_r:
                case BodyPartType.leg_l:
                case BodyPartType.leg_r: lethal = c.LimbLethal;  down = c.LimbDown;  break;
                default:                 lethal = c.ChestLethal; down = c.ChestDown; break;  // auto_detect → chest odds
            }
        }

        private static int CurrentLife(Soldier s)
        {
            try { var l = Items.Lua(s); if (l != null) return l.getHealth(); } catch { }
            return 100;
        }
    }
}
