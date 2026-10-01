using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// "Request treatment" on the interact key (default X), for two sources sharing that key:
    ///  1. A looked-at AI medic within range — stitches the player's bandaged wounds + one blood pack.
    ///  2. Standing in a medical tent's vicinity — a FULL health reset to the standard value.
    /// Both play a first-person bandage animation on the player (the medic also plays a third-person
    /// one). The tent is detected by Tents (a name match on nearby objects, since a medical tent is a
    /// data-driven map prop with no typed component) — the tokens are configurable, and a debug dump
    /// of nearby object names is logged when nothing matches so the real name can be found.
    /// Neither works while the player is downed: that is a revive, which the tent's full reset would
    /// otherwise hand out on a second X press.
    ///
    /// The prompt only shows while the source can actually do something: a medic treats wounds (open,
    /// bandaged, infected, shrapnel, fractures), pain, tourniquets, lost blood and poor vitals; only the
    /// tent restores plain lost HP.
    /// </summary>
    internal static class MedicCall
    {
        public enum Source { None, Medic, Tent }

        private static bool _active;
        private static bool _tent;           // true = tent full-heal, false = AI-medic treatment
        private static float _endTime;
        private static Soldier _medic;       // the AI medic (null for a tent heal)

        private static Source _prompt;   // what the on-screen hint should show right now
        private static float _nextEval;  // throttle for the availability check
        private const float EvalSeconds = 0.15f;

        private const float RangeSlack = 1.5f;       // a little room for shuffling during the clip
        private const float IvUsefulBelowBlood = 0.95f;   // the medic only hangs a blood pack when there's room for it

        public static bool Active => _active;

        /// <summary>Drop the running heal and the prompt (player gone / session flush). The medic-bandage
        /// restore watcher is NOT player state and survives it (ClearRestore drops it on a scene flush).</summary>
        public static void Reset() { _active = false; _tent = false; _medic = null; _prompt = Source.None; }

        /// <summary>Runs from Effects.Tick with a live player. Handles the keypress and completion.</summary>
        public static void Tick(Soldier player)
        {
            var c = Plugin.C;
            if (player == null) { Reset(); return; }

            if (_active)
            {
                _prompt = Source.None;
                if (Time.time >= _endTime)
                {
                    // The heal only lands if it still can: going down during it would otherwise be
                    // revived by the tent's full reset, and a medic who died, went down or walked off
                    // can't have treated anyone.
                    string broken = Interrupted(player);
                    if (broken != null)
                    {
                        if (c.DebugLogging.Value) Plugin.L.LogInfo($"[heal] X-heal not applied: {broken}");
                    }
                    else
                    {
                        if (_tent) CompleteTent(player); else CompleteMedic(player);
                        if (c.DebugLogging.Value) Plugin.L.LogInfo($"[heal] X-heal complete: player bandages now={Items.CountOf(player, c.IdBandage.Value)}");
                    }
                    Reset();
                }
                return;
            }

            // Throttled availability check that drives the on-screen prompt. A raycast + a small
            // overlap a few times a second is plenty for a hint and keeps it off the 60 Hz path.
            if (Time.time >= _nextEval)
            {
                _nextEval = Time.time + EvalSeconds;
                _prompt = Evaluate(player, out _);
            }

            if (!Interop.GetKeyDown(c.MedicCallKey.Value)) return;
            // Not while typing in chat or paused: an "x" in a chat line next to a medic or a tent started a heal.
            if (Interop.InputBlocked()) return;

            // Fresh evaluation at the moment of action (never act on a stale cached soldier).
            var kind = Evaluate(player, out var medic);
            if (kind == Source.Medic && medic != null) StartMedic(medic, player);
            else if (kind == Source.Tent) StartTent(player);
            else if (c.EnableTentHeal.Value && c.DebugLogging.Value && Interop.TryPos(player, out var pos))
                DumpNearby(pos, c.TentRange.Value);   // discovery aid when nothing matched
        }

        /// <summary>Why a finished X-heal can no longer take effect, or null if it can.</summary>
        private static string Interrupted(Soldier player)
        {
            var c = Plugin.C;
            if (Interop.IsDowned(player)) return "player went down during the heal";
            if (_tent)
            {
                if (!Interop.TryPos(player, out var pos) || !Tents.Near(pos, c.TentRange.Value + RangeSlack)) return "left the tent";
                return null;
            }
            var m = _medic;
            if (m == null || Interop.IsDead(m) || Interop.IsDowned(m)) return "the medic is down";
            if (Interop.Distance(player, m) > c.MedicCallRange.Value + RangeSlack) return "the medic is out of reach";
            return null;
        }

        /// <summary>Which heal source is available right now (a looked-at medic wins over a tent).</summary>
        private static Source Evaluate(Soldier player, out Soldier medic)
        {
            medic = null;
            var c = Plugin.C;
            if (MedicUI.Open || Procedure.Active) return Source.None;

            // Downed: a self-revive by the tent's full reset (second X press) or the medic's. Getting up
            // is the vitals' wake-up roll or a real medic's revive, not a key.
            if (Interop.IsDowned(player)) return Source.None;
            var w = WoundState.Peek(player);
            bool medicUseful = MedicCanHelp(player, w);
            bool tentUseful = medicUseful || LowLife(player);

            if (c.EnableMedicCall.Value && medicUseful)
            {
                var m = FindMedic(player);
                if (m != null) { medic = m; return Source.Medic; }
            }
            if (c.EnableTentHeal.Value && tentUseful && Interop.TryPos(player, out var pos) && Tents.Near(pos, c.TentRange.Value))
                return Source.Tent;

            return Source.None;
        }

        /// <summary>Bottom-centre hint ("[X] to heal") while a medic or tent is in reach. Called from
        /// the OnGUI hook. No-op while a treatment is running or the overlay is open.</summary>
        public static void DrawPrompt()
        {
            if (_active || _prompt == Source.None || MedicUI.Open) return;
            // Repaint only: the two skin writes plus the message string were being paid on every
            // OnGUI pass, and nothing here takes part in layout.
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            string msg = PromptText();
            const float w = 220f, h = 28f;
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.70f;

            var prevCol = GUI.color;
            int prevSize = 0; TextAnchor prevAlign = TextAnchor.UpperLeft;
            try
            {
                prevSize = GUI.skin.label.fontSize; prevAlign = GUI.skin.label.alignment;
                GUI.skin.label.fontSize = 16; GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            }
            catch { }
            GUI.color = new Color(1f, 1f, 1f, 0.95f);
            GUI.Label(new Rect(x, y, w, h), msg);
            GUI.color = prevCol;
            try { GUI.skin.label.fontSize = prevSize; GUI.skin.label.alignment = prevAlign; } catch { }
        }

        // The prompt text, rebuilt only when the configured key changes (it was interpolated fresh
        // on every OnGUI pass).
        private static string _promptKey, _promptText;

        private static string PromptText()
        {
            var k = Plugin.C.MedicCallKey.Value;
            if (!ReferenceEquals(k, _promptKey) || _promptText == null)
            {
                _promptKey = k;
                _promptText = "[" + k + "] to heal";
            }
            return _promptText;
        }

        // ---- AI medic ------------------------------------------------------------------------
        private static void StartMedic(Soldier medic, Soldier player)
        {
            _medic = medic; _tent = false; _active = true;
            _endTime = Time.time + Mathf.Max(0.1f, Plugin.C.MedicCallTime.Value);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[heal] X-heal start (medic): player bandages={Items.CountOf(player, Plugin.C.IdBandage.Value)} medic bandages={Items.CountOf(medic, Plugin.C.IdBandage.Value)}");
            PlayPlayerAnim(player);
            PlayMedicAnim(medic);
        }

        private static Soldier FindMedic(Soldier player)
        {
            var s = Targeting.CursorSoldier();
            if (s == null || Interop.SamePtr(s, player)) return null;
            if (Interop.IsDead(s) || Interop.IsDowned(s)) return null;
            if (!Interop.IsMedic(s)) return null;
            if (Interop.Distance(player, s) > Plugin.C.MedicCallRange.Value) return null;
            return s;
        }

        /// <summary>Something an AI medic's treatment would actually change: an active wound (open,
        /// bandaged or infected, shrapnel, a fracture to splint, a bruise), a tourniquet to take off, pain
        /// to clear, lost blood the IV would top up, vitals out of range, or the game's bleeding flag.
        /// Plain lost HP is NOT on the list - a medic doesn't restore it, and offering "[X] to heal" for it
        /// handed out a free pain clear and blood pack on every press.</summary>
        private static bool MedicCanHelp(Soldier player, SoldierWounds w)
        {
            if (Interop.IsBleeding(player)) return true;
            if (w == null) return false;
            if (w.ActiveCount() > 0 || w.Tourniquets.Count > 0) return true;
            if (w.PainValue >= 1f) return true;
            if (w.VitalsInit && w.Blood + w.IvRemaining < IvUsefulBelowBlood) return true;   // needs blood (not already on its way in)
            return w.VitalsInit && !Vitals.VitalsInRange(w);
        }

        /// <summary>Low HP only counts for the tent, and only where it can restore it: on the soldier's
        /// owner, which is always true for the local player now that CompleteTent syncs the new life.</summary>
        private static bool LowLife(Soldier player)
        {
            if (!Interop.HasAuthority(player)) return false;
            int life = Interop.Life(player);
            return life >= 0 && life < Mathf.RoundToInt(Plugin.C.TentHealLife.Value);
        }

        private static void CompleteMedic(Soldier player)
        {
            var w = WoundState.Get(player);
            if (w == null) return;

            // STAGE 1: while anything is still openly bleeding, this press just BANDAGES it all.
            if (BandageStage(player, w, "mediccall")) return;

            // STAGE 2: nothing bleeding openly → the deeper treatment. Stitch every bandaged wound (which
            // also clears any infection), pull any shrapnel, dose painkillers (clear pain), and hang a
            // blood pack if there's room for one.
            int stitched = 0, shr = 0;
            foreach (var kv in w.Parts)
                foreach (var wd in kv.Value)
                {
                    if (!wd.Active) continue;
                    if (wd.Bandaged) { Treatment.Close(wd); stitched++; }
                    else if (wd.Type == WoundType.Shrapnel) { Treatment.Close(wd); shr++; }
                    else if (wd.Type == WoundType.Fracture || wd.Type == WoundType.Bruise) Treatment.Close(wd);   // set and cast / dressed
                }
            w.InvalidateMove();

            w.PainValue = 0f; w.Pain = PainTier.None; w.AspirinActive = false;   // painkillers
            w.Tourniquets.Clear();                                              // bleeding's handled — take them off
            Pain.Refresh(w);   // re-derive the wound floor now (a stale one would put the pain straight back)
            if (!w.AnyBleeding()) Interop.SetBleeding(player, false);
            Vitals.Init(w);
            // Counting what is still flowing in: every repeated press used to hang another bag (up to
            // IvMaxCount) while the first was still running.
            bool iv = w.Blood + w.IvRemaining < IvUsefulBelowBlood;
            if (iv) Vitals.StartIv(w);   // one blood pack (IvAmount, ~25%) flows in
            MpSync.Send(player);

            if (Plugin.C.DebugLogging.Value)
                Plugin.L.LogInfo($"[mediccall] stage 2 — stitched {stitched}, removed {shr} shrapnel, painkillers{(iv ? " + 1 blood pack" : "")}");
        }

        // ---- medical tent --------------------------------------------------------------------
        private static void StartTent(Soldier player)
        {
            _medic = null; _tent = true; _active = true;
            _endTime = Time.time + Mathf.Max(0.1f, Plugin.C.TentHealTime.Value);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[heal] X-heal start (tent): player bandages={Items.CountOf(player, Plugin.C.IdBandage.Value)}");
            PlayPlayerAnim(player);
        }

        /// <summary>Staged tent heal: the FIRST press (while anything is openly bleeding) just bandages
        /// every bleeding wound; a SECOND press does the full reset — clear all wounds + vitals, revive if
        /// downed, restore HP to standard, and release the bleed/pain suppression floor.</summary>
        private static void CompleteTent(Soldier player)
        {
            var w = WoundState.Get(player);

            // STAGE 1: bandage all bleeding first.
            if (w != null && BandageStage(player, w, "tent")) return;

            // STAGE 2: full reset to the standard value.
            try { Vitals.StabilizeAndClear(player); } catch (Exception e) { Guard.Once("tent stabilise", e); }   // wounds/pain/blood/pulse/bp + wake

            // HP: the OWNER sets it and broadcasts it. The local player's avatar is always ours (a client owns
            // its own soldier and already broadcasts its own bleed/regen life changes), so this runs on
            // clients too. A bare life_total write never syncs: the other machines kept the old low value
            // and the next hit they computed pushed "stale - damage" back, undoing the heal (and, 25+
            // below the new value, turning vanilla bleeding on). NetLife pushes the new value right away.
            int life = Mathf.RoundToInt(Plugin.C.TentHealLife.Value);
            if (Interop.HasAuthority(player))
            {
                bool set = false;
                try { SetLife(player, life); set = true; } catch { }
                if (set) NetLife.SyncOwnLife(player, "tent heal");
                else
                {
                    // Fallback: Lua healSoldier is Damage(-x) - it ADDS x (and syncs by itself), so hand it
                    // the difference to the target, not the target.
                    int cur = -1;
                    try { cur = Items.Lua(player)?.getHealth() ?? -1; } catch { }
                    if (cur >= 0 && life > cur) { try { Items.Lua(player)?.healSoldier(life - cur); } catch { } }
                }
            }

            Interop.SetBleeding(player, false);
            Interop.SetExternalStressFloor(0f);

            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[tent] stage 2 — full heal, life reset to {life}");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SetLife(Soldier s, int life) { s.life_total = new ProtectedInt(life); }

        /// <summary>Stage 1 of a medic/tent heal: while anything bleeds openly, bandage it all and stop
        /// there (the next press does the rest). True if it did.</summary>
        private static bool BandageStage(Soldier player, SoldierWounds w, string tag)
        {
            if (!w.HasOpenBleed()) return false;
            int b = BandageAll(player, w);
            MpSync.Send(player);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[{tag}] stage 1 — bandaged {b} bleeding wound(s)");
            return true;
        }

        /// <summary>Bandage every open bleeding wound at once (medic/tent supplies — free to the player).</summary>
        public static int BandageAll(Soldier player, SoldierWounds w)
        {
            int n = 0;
            var c = Plugin.C;
            foreach (var kv in w.Parts)
                foreach (var wd in kv.Value)
                    if (wd.Active && wd.Type == WoundType.Bleeding && !wd.Bandaged)
                    {
                        wd.Bandaged = true;
                        wd.ReopenAt = Time.time + UnityEngine.Random.Range(c.BandageReopenMin.Value, c.BandageReopenMax.Value);
                        n++;
                    }
            if (n > 0) w.InvalidateMove();
            if (!w.AnyBleeding()) Interop.SetBleeding(player, false);
            return n;
        }

        /// <summary>When the tent didn't match, log the distinct nearby object names (debug only) so
        /// the real medical-tent name can be read and the NameToken config adjusted.</summary>
        private static void DumpNearby(Vector3 pos, float range)
        {
            if (!Plugin.C.DebugLogging.Value) return;
            try
            {
                var hits = Physics.OverlapSphere(pos, range);
                if (hits == null) return;
                var seen = new HashSet<string>();
                for (int i = 0; i < hits.Length && i < 256; i++)
                {
                    var col = hits[i]; if (col == null) continue;
                    var tr = col.transform;
                    for (int d = 0; d < 3 && tr != null; d++) { seen.Add(tr.gameObject.name); tr = tr.parent; }
                }
                Plugin.L.LogInfo("[tent] no match; nearby object names: " + string.Join(" | ", seen));
            }
            catch { }
        }

        // ---- shared helpers ------------------------------------------------------------------
        private static void PlayPlayerAnim(Soldier player)
        {
            // The self-bandage FP clip, animation-only: Procedure.PlayBandage pre-pays the bandage its
            // deferred consume will take (net zero, timing-independent) and takes that pre-payment back
            // if the game refuses the clip (vehicle, downed, another clip still locking) - the old inline
            // copy never did, so every refused clip handed out a free bandage.
            try
            {
                bool started = Procedure.PlayBandage(player, refund: true);
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[heal] X-heal clip started={started}");
            }
            catch (Exception e) { Guard.Once("mediccall player clip", e); }
        }

        // ---- the AI medic's bandage: give back what its clip spends --------------------------
        // The medic's third-person bandage clip may take a bandage out of ITS inventory, and the game only
        // does that when the clip ENDS - long after PlayBandageAnimationTPS returns. The old inline
        // snapshot/restore ran right after starting the clip, found nothing spent yet, and never gave
        // anything back. The count before the clip is kept instead and topped back up for a while after.
        private struct MedicRestore { public Soldier Medic; public IntPtr Ptr; public string Id; public int Before; public float Until, Next; }
        private static MedicRestore _restore;
        private static bool _restoreOn;
        private const float MedicRestoreSeconds = 10f;   // longer than any bandage clip
        private const float MedicRestoreEvery = 0.25f;

        private static void PlayMedicAnim(Soldier medic)
        {
            try
            {
                string id = Plugin.C.IdBandage.Value;
                // A second heal from the same (or another) medic while a restore is pending: settle that one
                // first, so the new "before" isn't read off a count still missing the last clip's bandage.
                if (_restoreOn) FinishRestore();
                int before = Items.CountOf(medic, id);
                PlayTpsBandage(medic, id);
                float now = Time.time;
                _restore = new MedicRestore
                {
                    Medic = medic, Ptr = Interop.PtrOf(medic), Id = id, Before = before,
                    Until = now + MedicRestoreSeconds, Next = now + MedicRestoreEvery,
                };
                _restoreOn = true;
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[heal] medic TPS bandage: before={before}, restoring for {MedicRestoreSeconds:0}s");
            }
            catch (Exception e) { Guard.Once("mediccall medic clip", e); }
        }

        /// <summary>World tick (runs on the respawn screen too): top the medic back up to its pre-clip
        /// bandage count until the window closes.</summary>
        public static void TickRestore()
        {
            if (!_restoreOn) return;
            float now = Time.time;
            if (now < _restore.Next) return;
            _restore.Next = now + MedicRestoreEvery;
            var m = _restore.Medic;
            if (m == null || Interop.IsDead(m)) { _restoreOn = false; _restore = default; return; }
            Items.RestoreTo(m, _restore.Id, _restore.Before);
            if (now >= _restore.Until) { _restoreOn = false; _restore = default; }
        }

        private static void FinishRestore()
        {
            var m = _restore.Medic;
            if (m != null && !Interop.IsDead(m)) Items.RestoreTo(m, _restore.Id, _restore.Before);
            _restoreOn = false; _restore = default;
        }

        /// <summary>A soldier is gone (SoldierRegistry): drop a pending restore for them.</summary>
        public static void Forget(IntPtr p)
        {
            if (_restoreOn && p != IntPtr.Zero && _restore.Ptr == p) { _restoreOn = false; _restore = default; }
        }

        /// <summary>Scene flush: the medic is about to be freed.</summary>
        public static void ClearRestore() { _restoreOn = false; _restore = default; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PlayTpsBandage(Soldier medic, string id)
        {
            var obj = ItemsDatabase.GetItemObject(id);
            if (obj != null) medic.PlayBandageAnimationTPS(obj);
        }
    }
}
