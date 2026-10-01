using System;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Decides which soldier the overlay treats: the downed/injured soldier under the cursor
    /// (if any, in range and not the player), otherwise the player (self-treat). Also handles
    /// the ReplaceVanillaHeal toggle via a GetInteractions strip.
    /// </summary>
    internal static class Targeting
    {
        /// <summary>Why the last J press fell back to the player (shown in the panel), or null.</summary>
        public static string LastReject;

        public static Soldier PickPatient()
        {
            LastReject = null;
            var player = Interop.Player();
            var target = CursorSoldier();
            if (target == null || Interop.SamePtr(target, player)) return player;

            string why = RejectReason(target, player);
            if (Plugin.C.DebugLogging.Value) LogTarget(target, why);
            if (why == null) return target;
            LastReject = why;
            return player;
        }

        private static string RejectReason(Soldier target, Soldier player)
        {
            if (Interop.IsDead(target)) return "that soldier is dead";
            if (!InRange(target, player)) return "too far away - get closer";
            // Every soldier is treatable — AI, other players, host or client. The optional safety switch
            // only exists because treating another human once crashed in an early build.
            if (!Plugin.C.TreatOtherPlayers.Value && Interop.IsOtherHuman(target)) return "another player (TreatOtherPlayers is off)";
            return null;
        }

        private static void LogTarget(Soldier s, string why)
        {
            // Debug-only, and called under a try (the reads below are game members: JIT trap).
            try { LogTargetRaw(s, why); } catch { }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void LogTargetRaw(Soldier s, string why)
        {
            bool ai = false, pl = false, inj = false, alive = false; int life = -999; string owner = "-";
            try { ai = s.IsAI(); } catch { }
            try { pl = s.IsPlayer(); } catch { }
            inj = Interop.IsDowned(s);
            try { alive = s.IsAlive; } catch { }
            life = Interop.Life(s);
            try
            {
                var pv = s.GetComponent<Photon.Pun.PhotonView>();
                if (pv != null) owner = pv.Owner == null ? "scene" : pv.Owner.NickName + (pv.Owner.IsMasterClient ? "(master)" : "");
            }
            catch { }
            Plugin.L.LogInfo($"[target] ai={ai} isPlayer={pl} otherHuman={Interop.IsOtherHuman(s)} injured={inj} alive={alive} life={life} owner={owner} -> {(why ?? "OK")}");
        }

        /// <summary>Every soldier that isn't dead can be examined/treated (standing, wounded or not,
        /// or downed), as long as they're within TreatRange of the player.</summary>
        private static bool InRange(Soldier s, Soldier p)
        {
            if (p == null) return true;
            return Interop.WithinRange(s, p, Mathf.Max(1f, Plugin.C.TreatRange.Value), unknown: true);
        }

        // A member of the raycast path gone in an update (MissingMember at JIT) latches the lookup off
        // for the session: J then opens on yourself and X finds no medic, instead of either failing.
        private static bool _cursorDead;

        /// <summary>Soldier under the aim cursor, via the game's cursor raycast. The game members live in
        /// CursorSoldierRaw (JIT trap): a CursorRaycast / GetUnit that vanished used to fail this method,
        /// and so the J target pick and the X medic lookup that call it, as a whole.</summary>
        public static Soldier CursorSoldier()
        {
            var pc = Interop.PC;
            if (pc == null || _cursorDead) return null;
            try { return CursorSoldierRaw(pc); }
            catch (Exception e)
            {
                if (e is MissingMemberException || e is TypeLoadException)
                {
                    _cursorDead = true;
                    Plugin.L.LogError("Cursor target lookup unavailable (J treats yourself, X finds no medic): " + e.Message);
                }
                return null;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Soldier CursorSoldierRaw(PlayerController pc)
        {
            try
            {
                if (!pc.CursorRaycast(out RaycastHit hit)) return null;
                var col = hit.collider;
                if (col == null) return null;

                // Bodies carry BodyPart colliders → GetUnit() → Soldier.
                try
                {
                    var bp = col.GetComponentInParent<BodyPart>();
                    if (bp != null)
                    {
                        var unit = bp.GetUnit();
                        var sol = Interop.CastTo<Soldier>(unit);
                        if (sol != null) return sol;
                    }
                }
                catch { }

                try { return col.GetComponentInParent<Soldier>(); } catch { }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// When ReplaceVanillaHeal is on (the default), drop the vanilla heal/bandage/syringe interactions
        /// from a soldier's interaction list so Advanced Medic (J) is the healing path. Carry/drag always
        /// stays. Matching is by WORD START, so a mismatch errs towards keeping an interaction.
        /// </summary>
        // Verdict per interaction text. The game asks for interactions every frame you look at someone,
        // and the texts are a small fixed set, so the word tests run once per distinct text instead of
        // once per interaction per frame.
        private static readonly System.Collections.Generic.Dictionary<string, bool> _healText
            = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.Ordinal);

        // A heal interaction has a WORD starting with one of these. Substring matching used to strip
        // "secure" (cure), "retreat" (treat), "raid" (aid) and the like.
        private static readonly string[] HealWordStarts =
        {
            "heal", "bandag", "syring", "medic", "cure", "treat", "reviv", "aid", "injur", "wound",
            "forceps", "scissor", "splint", "diagnos", "aspirin", "tourniquet", "plier",
        };

        private static bool IsHealText(string t)
        {
            if (_healText.TryGetValue(t, out bool heal)) return heal;
            var low = t.ToLowerInvariant();
            if (low.Contains("carry") || low.Contains("drag")) heal = false;   // always keep carry/drag
            else heal = HasHealWord(low);
            if (_healText.Count < 512) _healText[t] = heal;   // bounded: localisation can't grow it forever
            return heal;
        }

        /// <summary>Does any word (a run of letters) of the lower-cased text start with a heal stem?</summary>
        private static bool HasHealWord(string low)
        {
            int n = low.Length;
            for (int i = 0; i < n; i++)
            {
                if (!char.IsLetter(low[i]) || (i > 0 && char.IsLetter(low[i - 1]))) continue;   // not a word start
                for (int k = 0; k < HealWordStarts.Length; k++)
                {
                    var stem = HealWordStarts[k];
                    if (i + stem.Length <= n && string.CompareOrdinal(low, i, stem, 0, stem.Length) == 0) return true;
                }
            }
            return false;
        }

        public static void StripVanillaHeal(Il2CppSystem.Collections.Generic.List<Interaction> list)
        {
            if (!Plugin.C.ReplaceVanillaHeal.Value || list == null) return;
            try
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var it = list[i];
                    if (it == null) continue;
                    string t = null;
                    try { t = it.text; } catch { }
                    if (string.IsNullOrEmpty(t)) { try { t = it.Description; } catch { } }
                    if (string.IsNullOrEmpty(t)) continue;
                    if (IsHealText(t)) list.RemoveAt(i);
                }
            }
            catch (Exception e) { Guard.Once("StripVanillaHeal", e); }
        }
    }
}
