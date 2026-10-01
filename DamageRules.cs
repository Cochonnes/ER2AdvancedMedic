using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AdvancedMedic
{
    /// <summary>
    /// The damage rules HpLogic.Process (and the grenade proximity roll) apply, and WHOSE they are.
    ///
    /// Damage in ER2 is computed on the SHOOTER's machine (BodyPart.HitPart -> Soldier.DamageWithAnimation
    /// -> TrySyncDamage), which is usually not the victim's owner. Applying each shooter's own config made
    /// a modded client a damage lever over everyone it shot, and gave a modded host's room different rules
    /// depending on who fired. So:
    /// - Single player, and the host itself: this machine's config.
    /// - A client whose host runs the mod (MpSync.HostSendsWounds): the HOST's values, published in its
    ///   hello (MpSync, protocol 3). Until they arrive: vanilla (null = no scaling, no conversion).
    /// - A client whose host doesn't run the mod: vanilla for every soldier it doesn't own; its own
    ///   soldier (self-inflicted blasts, falls, fire) keeps this machine's config - nobody else is affected.
    /// </summary>
    internal sealed class DamageRules
    {
        public HpMode ExtraHpMode, DownedSurviveMode;
        public float DamageScale, OverkillMargin, SurviveLifeFrac, OneShotHpThreshold, AiDownChance;
        public float HeadLethal, HeadDown, ChestLethal, ChestDown, LimbLethal, LimbDown;
        public float LethalityMult, ExplosionDamageMult, GrenadeInstaKillChance, GrenadeLethalRadius;
        public bool LethalityAiOnly, VehicleOccupantLethal, GrenadeInstaKill;
        public string Source;

        /// <summary>Number of ints the packed form carries (after its own count).</summary>
        private const int Packed = 20;

        // ---- this machine's config, refreshed at most once a frame ----
        private static readonly DamageRules _local = new DamageRules { Source = "local" };
        private static int _localFrame = -1;

        public static DamageRules Local
        {
            get
            {
                int f = Time.frameCount;
                if (f != _localFrame) { _localFrame = f; _local.ReadConfig(); }
                return _local;
            }
        }

        private void ReadConfig()
        {
            var c = Plugin.C;
            ExtraHpMode = c.ExtraHpMode.Value; DownedSurviveMode = c.DownedSurviveMode.Value;
            DamageScale = c.DamageScale.Value; OverkillMargin = c.OverkillMargin.Value; SurviveLifeFrac = c.SurviveLifeFrac.Value;
            OneShotHpThreshold = c.OneShotHpThreshold.Value; AiDownChance = c.AiDownChance.Value;
            HeadLethal = c.HeadLethal.Value; HeadDown = c.HeadDown.Value;
            ChestLethal = c.ChestLethal.Value; ChestDown = c.ChestDown.Value;
            LimbLethal = c.LimbLethal.Value; LimbDown = c.LimbDown.Value;
            LethalityMult = c.LethalityMult.Value; ExplosionDamageMult = c.ExplosionDamageMult.Value;
            GrenadeInstaKillChance = c.GrenadeInstaKillChance.Value; GrenadeLethalRadius = c.GrenadeLethalRadius.Value;
            LethalityAiOnly = c.LethalityAiOnly.Value; VehicleOccupantLethal = c.VehicleOccupantLethal.Value;
            GrenadeInstaKill = c.GrenadeInstaKill.Value;
        }

        /// <summary>Append [count, values...] (floats as their raw bits, so nothing is rounded).</summary>
        public void WriteTo(List<int> dst)
        {
            dst.Add(Packed);
            dst.Add((int)ExtraHpMode); dst.Add((int)DownedSurviveMode);
            F(dst, DamageScale); F(dst, OverkillMargin); F(dst, SurviveLifeFrac); F(dst, OneShotHpThreshold); F(dst, AiDownChance);
            F(dst, HeadLethal); F(dst, HeadDown); F(dst, ChestLethal); F(dst, ChestDown); F(dst, LimbLethal); F(dst, LimbDown);
            F(dst, LethalityMult); F(dst, ExplosionDamageMult); F(dst, GrenadeInstaKillChance); F(dst, GrenadeLethalRadius);
            dst.Add(LethalityAiOnly ? 1 : 0); dst.Add(VehicleOccupantLethal ? 1 : 0); dst.Add(GrenadeInstaKill ? 1 : 0);
        }

        private static void F(List<int> dst, float v) => dst.Add(BitConverter.SingleToInt32Bits(v));

        /// <summary>Read what WriteTo wrote, starting at arr[at] (the count). Null if it's short or garbled.</summary>
        public static DamageRules Read(Il2CppStructArray<int> arr, int at, string source)
        {
            if (arr == null || at < 0 || at >= arr.Length) return null;
            int n = arr[at];
            if (n < Packed || at + 1 + Packed > arr.Length) return null;
            int i = at + 1;
            var r = new DamageRules { Source = source };
            r.ExtraHpMode = Mode(arr[i++]); r.DownedSurviveMode = Mode(arr[i++]);
            r.DamageScale = G(arr[i++]); r.OverkillMargin = G(arr[i++]); r.SurviveLifeFrac = G(arr[i++]);
            r.OneShotHpThreshold = G(arr[i++]); r.AiDownChance = G(arr[i++]);
            r.HeadLethal = G(arr[i++]); r.HeadDown = G(arr[i++]); r.ChestLethal = G(arr[i++]); r.ChestDown = G(arr[i++]);
            r.LimbLethal = G(arr[i++]); r.LimbDown = G(arr[i++]);
            r.LethalityMult = G(arr[i++]); r.ExplosionDamageMult = G(arr[i++]);
            r.GrenadeInstaKillChance = G(arr[i++]); r.GrenadeLethalRadius = G(arr[i++]);
            r.LethalityAiOnly = arr[i++] != 0; r.VehicleOccupantLethal = arr[i++] != 0; r.GrenadeInstaKill = arr[i++] != 0;
            return r;
        }

        private static HpMode Mode(int v) => v >= (int)HpMode.Everyone && v <= (int)HpMode.Noone ? (HpMode)v : HpMode.Noone;

        private static float G(int bits)
        {
            float v = BitConverter.Int32BitsToSingle(bits);
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        }

        public override string ToString() =>
            $"{Source}: hp={ExtraHpMode} downed={DownedSurviveMode} scale={DamageScale:0.##} lethal x{LethalityMult:0.##}{(LethalityAiOnly ? " (AI)" : "")} " +
            $"expl x{ExplosionDamageMult:0.##} head {HeadLethal:0.##}/{HeadDown:0.##} grenade {(GrenadeInstaKill ? GrenadeInstaKillChance.ToString("0.##") : "off")}";

        // ---- whose rules apply to a hit ----
        private static string _lastWhy;

        /// <summary>The rules for damage this machine is applying to <paramref name="victim"/> right now,
        /// or null for vanilla (see the class comment).</summary>
        public static DamageRules For(Soldier victim)
        {
            string why;
            DamageRules r;
            if (!Interop.InMpRoom()) return Local;                       // single player
            if (Interop.IsMaster()) { r = Local; why = "host: own config"; }
            else if (MpSync.HostSendsWounds)
            {
                r = MpSync.HostRules;
                why = r != null ? "modded host: host's rules" : "modded host: rules not received yet -> vanilla";
            }
            else if (Interop.HasAuthority(victim)) { r = Local; why = "unmodded host: own soldier -> own config"; }
            else { r = null; why = "unmodded host: not our soldier -> vanilla"; }

            if (why != _lastWhy)   // logged on a change of decision, not per hit
            {
                _lastWhy = why;
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] damage rules -> {why}{(r != null ? " (" + r + ")" : "")}");
            }
            return r;
        }

        /// <summary>Forget the logged decision (session flush), so the next match logs its own.</summary>
        public static void ResetLog() { _lastWhy = null; }
    }
}
