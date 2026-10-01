using System;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Per-frame logic (PlayerController.LateUpdate postfix): the scene guard, input polling, the
    /// per-soldier ticks (pain, vitals, downed), the local player's walk factor / pose lock /
    /// suppression floor / feedback, and the overlay's cursor + camera lock.
    ///
    /// Each subsystem that reaches game UI or player members is called through its own try/catch:
    /// a member removed by a game update kills the whole JIT-compiled method that references it, and
    /// the call site is the only place that failure can be caught. One logged line names it; the rest
    /// of the tick (above all ManageOverlay, which frees the cursor) keeps running.
    /// </summary>
    internal static class Effects
    {
        /// <summary>Current worst pain/limb intensity for the local player (0..20+). Drives PainFX.</summary>
        public static float PainIntensity;

        private static bool _hadSession;   // a mission has been ticking since the last full flush
        private static bool _hadPlayer;    // ...and the local player was alive last tick

        public static void Tick()
        {
            var c = Plugin.C;

            // Debug overlay toggle (touches no soldier state — safe any time).
            if (Interop.GetKeyDown(c.DebugOverlayKey.Value))
                c.DebugOverlay.Value = !c.DebugOverlay.Value;

            // Finish loading hurt sounds decoded in the background (no soldier state; one clip a frame).
            try { HitSounds.Pump(); } catch (Exception e) { TickFailed("HitSounds.Pump", e); }

            var player = Interop.Player();

            // Scene-transition / mission-teardown guard. A scene unload frees every tracked Soldier's
            // native object, but our static maps keep their pointers; the per-soldier ticks below call
            // straight into those soldiers (Vitals/DownedLogic/SpawnKit). Dereferencing a freed IL2CPP
            // pointer is a native access violation that try/catch CANNOT catch — that's the exit-to-menu
            // crash. So the moment the scene changes, or the player is gone AND the battle is no longer
            // live (exit to menu, battle over: the teardown window), every per-soldier reference goes and
            // nothing is touched.
            //
            // The player being gone on its own is NOT that: it is the respawn screen, and the battle goes
            // on. Before 2026-09-28 that flushed the world too - AI wounds erased, the host's MP sync
            // stopped, and a downed AI whose vanilla timer we had pinned lay downed forever. Now only the
            // player-local state goes there, and the world keeps ticking (see below).
            bool sceneChanged = Interop.SceneChanged();
            if (sceneChanged || (player == null && !Interop.MissionLive()))
            {
                // Per-MAP state only goes with the scene: the tent cache and which host sends wound data.
                if (sceneChanged) { Tents.Reset(); MpSync.ResetSession(); }
                if (_hadSession || WoundState.TrackedCount > 0 || sceneChanged) FlushSession(sceneChanged);
                _hadSession = false; _hadPlayer = false;
                ReleaseLocalFeedback();
                return;
            }
            _hadSession = true;

            if (player == null)
            {
                // Respawn screen: the local player is dead / not yet deployed, the battle goes on.
                if (_hadPlayer) ResetPlayerLocal();
                _hadPlayer = false;
                TickWorld();
                try { Hooks.HideHealHoldPrompt(); } catch (Exception e) { TickFailed("HideHealHoldPrompt", e); }   // restores a prompt we hid
                ReleaseLocalFeedback();
                return;
            }
            _hadPlayer = true;

            // ---- from here down: a live player and a map that only holds live soldiers ----

            // Advance any in-progress treatment (animation timer). Each subsystem on its own guard: they
            // all reach game members, and one of those vanishing in an update (a JIT failure at the call)
            // must not take the rest of the tick - above all ManageOverlay, which frees the cursor - with it.
            try { Procedure.Tick(); } catch (Exception e) { TickFailed("Procedure", e); }

            // "Request treatment" from a looked-at AI medic (press X near them).
            try { MedicCall.Tick(player); } catch (Exception e) { TickFailed("MedicCall", e); }

            // Open / close the medical overlay (ignored while a procedure animation is playing).
            // Opening is blocked while paused/in a menu, while chatting, or when downed (unless the
            // player can still treat themselves - incapacitated self-aid); closing is always allowed. An
            // already-open menu also closes if those states arise.
            try
            {
                if (!Procedure.Active && Interop.GetKeyDown(c.OpenKey.Value))
                {
                    if (MedicUI.Open || MedicUI.CanOpen(player)) MedicUI.Toggle();
                }
                if (MedicUI.Open && !MedicUI.CanOpen(player)) MedicUI.Close();
            }
            catch (Exception e) { TickFailed("OverlayToggle", e); try { MedicUI.Close(); } catch { } }

            // Every tracked soldier: spawn kits, MP sync, AI heals, pain, vitals, downed timers, the sweep.
            TickWorld();

            // Pain intensity for the LOCAL player (self effects). Treatment of other soldiers still
            // records wounds on them, but screen feedback is the player's. (The walk factor is applied
            // per soldier by the Move prefix from its own cache.)
            PainIntensity = 0f;

            try { Supply.EnsurePlayerKit(player); } catch (Exception e) { TickFailed("Supply", e); }

            // Catch damage that never reached our hooks (host-authoritative MP-client hits, mostly):
            // if the player's HP dropped without our sensor recording a wound, infer one.
            try { WatchPlayerDamage(player); } catch (Exception e) { TickFailed("WatchPlayerDamage", e); }

            // Suppress the vanilla bleeding flag on the player so the "hold to heal" prompt never
            // appears — bleeding is driven by our own wound/blood model instead.
            try { SuppressVanillaBleeding(player); } catch (Exception e) { TickFailed("SuppressVanillaBleeding", e); }

            var w = WoundState.Peek(player);
            if (w != null)
            {
                PainIntensity = ComputeIntensity(w);
                // Its own guard: SoldierPose / SetPose are game members, and a JIT failure here used to
                // throw out of the whole tick - past ManageOverlay, which frees the cursor.
                try { EnforcePoseLock(player, w); } catch (Exception e) { TickFailed("PoseLock", e); }
            }

            // Bleed/pain suppression floor into ImmersiveEffects + chest/head bleed stamina cut.
            UpdateSuppression(w);
            try { CapStamina(player, w); } catch (Exception e) { TickFailed("CapStamina", e); }

            try { BloodScreen.Tick(player); } catch (Exception e) { TickFailed("BloodScreen", e); }   // persistent blood-splash while bleeding
            try { Heartbeat.Tick(player); } catch (Exception e) { TickFailed("Heartbeat", e); }       // heartbeat when the pulse races or crawls
            try { Hooks.HideHealHoldPrompt(); } catch (Exception e) { TickFailed("HideHealHoldPrompt", e); }   // the "[B]" during our heal animation
            try { PainFX.Tick(); } catch (Exception e) { TickFailed("PainFX", e); }
            ManageOverlay();   // last: free cursor + freeze camera so look can't leak through
        }

        /// <summary>The per-soldier world: everything that runs for every tracked soldier whether or not
        /// the local player is alive (the respawn screen keeps it running). Each on its own guard.</summary>
        private static void TickWorld()
        {
            try { SpawnKit.Tick(); } catch (Exception e) { TickFailed("SpawnKit", e); }
            try { MpSync.Tick(); } catch (Exception e) { TickFailed("MpSync", e); }       // subscription + batched snapshots (no-op in SP)
            try { Icons.Pump(); } catch (Exception e) { TickFailed("Icons", e); }         // copy freshly-delivered game icons before a map change can unload them
            try { Hooks.TickAiHeals(); } catch (Exception e) { TickFailed("AiHeals", e); } // AI self-bandages / AI-medic revives once the game's clip has finished
            try { Hooks.TickPendingBlasts(); } catch (Exception e) { TickFailed("PendingBlasts", e); } // remote-thrower grenades awaiting the thrower's resolution
            try { MedicCall.TickRestore(); } catch (Exception e) { TickFailed("MedicRestore", e); }  // give an AI medic back the bandage its clip spent
            // Decay pain / resolve aspirin, run vitals (blood/pulse/BP/IV/death), and keep the
            // vanilla countdown pinned, for every tracked soldier.
            try { Pain.TickAll(); } catch (Exception e) { TickFailed("Pain", e); }
            try { Vitals.TickAll(); } catch (Exception e) { TickFailed("Vitals", e); }
            try { DownedLogic.TickAll(); } catch (Exception e) { TickFailed("DownedLogic", e); }
            try { SweepIdleSoldiers(); } catch (Exception e) { TickFailed("Sweep", e); }
        }

        /// <summary>No live player this tick: release what the player's feedback holds (the ImmersiveEffects
        /// floor, the screen shake decay, the cursor if the overlay was open).</summary>
        private static void ReleaseLocalFeedback()
        {
            Interop.SetExternalStressFloor(0f);
            try { PainFX.Tick(); } catch (Exception e) { TickFailed("PainFX", e); }
            ManageOverlay();
        }

        // One line per subsystem that fails (a missing game member = a JIT failure at its call site),
        // not one per frame.
        private static void TickFailed(string what, Exception e) => Guard.Once("tick " + what, e);

        /// <summary>Keep the vanilla bleeding flag off on the player so the "hold to heal" prompt never
        /// appears. NOT while a procedure or a medic/tent heal runs: their self-bandage animation needs
        /// the player to stay "bleeding" or the vanilla clip aborts mid-play (that's why self-heal
        /// animations weren't showing). Its own method: isBleeding/SetBleeding are game members (see
        /// the class comment).</summary>
        private static void SuppressVanillaBleeding(Soldier player)
        {
            if (!Procedure.Active && !MedicCall.Active && Interop.IsBleeding(player)) Interop.SetBleeding(player, false);
        }

        // Pointers to drop at the end of an idle sweep, reused so the sweep allocates nothing.
        private static readonly System.Collections.Generic.List<IntPtr> _dropList
            = new System.Collections.Generic.List<IntPtr>();
        private static readonly System.Collections.Generic.List<bool> _dropGone
            = new System.Collections.Generic.List<bool>();
        private static float _sweepAt;
        private const float SweepSeconds = 5f;
        private const float DiagnosisKeepSeconds = 300f;   // how long a diagnosis survives on an otherwise idle soldier

        /// <summary>
        /// Bound the tracked-soldier map. WoundState only ever shrank on death, so a soldier who was
        /// wounded and then left the scene without going through Kill/KillSynched stayed in it for
        /// the rest of the match - and every surviving entry is walked by Pain.TickAll,
        /// DownedLogic.TickAll and Vitals.TickAll on their own cadences, plus carries a cached
        /// Lua_Soldier and InventoryManager.
        ///
        /// Runs every SweepSeconds, not per frame, and collects into a reused list so it allocates
        /// nothing. Removal happens AFTER the loop: pruning inside it would modify the dictionary
        /// being iterated and abort the rest of the pass (the same trap Vitals' deferred kill list
        /// exists for).
        /// </summary>
        private static void SweepIdleSoldiers()
        {
            float now = Time.time;
            if (now < _sweepAt) return;
            _sweepAt = now + SweepSeconds;

            var playerPtr = Interop.PlayerPtr();
            _dropList.Clear(); _dropGone.Clear();

            var openPtr = MedicUI.Open ? Interop.PtrOf(MedicUI.Patient) : IntPtr.Zero;
            foreach (var kv in WoundState.All())
            {
                var w = kv.Value;
                w.PruneTreated();                             // closed wounds: nothing reads them any more
                if (kv.Key == playerPtr) continue;            // never sweep ourselves
                if (kv.Key == openPtr) continue;              // nor the patient whose panel is open
                // A destroyed soldier reads as null through Unity's operator. That is the leak: no
                // Kill ever fired for them, so nothing pruned the entry.
                bool gone = w.Owner == null;
                if (gone || Idle(w)) { _dropList.Add(kv.Key); _dropGone.Add(gone); }
            }

            if (_dropList.Count == 0) return;
            // A destroyed soldier is gone for good; a merely idle one is alive and keeps its spawn-kit
            // bookkeeping (SoldierRegistry.Forget).
            for (int i = 0; i < _dropList.Count; i++)
                SoldierRegistry.Forget(_dropList[i], dead: _dropGone[i]);
            if (Plugin.C.DebugLogging.Value)
                Plugin.L.LogInfo($"[sweep] dropped {_dropList.Count} idle soldier(s); {WoundState.TrackedCount} tracked");
            _dropList.Clear(); _dropGone.Clear();
        }

        /// <summary>Nothing left to simulate for this soldier. Dropping the entry is safe only when
        /// it holds no state the physiology still needs, because the next wound re-creates it fresh
        /// through WoundState.Get - all that should be lost is a stale diagnosis snapshot for
        /// someone with nothing wrong with them. The local player is never swept.
        ///
        /// EVERY persistent field on SoldierWounds has to be represented here. If the model gains
        /// one, add it: the failure mode is silent, and it looks like a treatment un-applying itself
        /// a few seconds later rather than like a bug in the sweep.
        ///
        /// Per-WOUND state (dressing, reopen/heal clocks, Infection) needs nothing here: any active wound
        /// already keeps the soldier through ActiveCount.
        ///
        /// Deliberately NOT guarded: Exposed (scissored parts - meaningless with no wound left),
        /// HasDiagnosis/Dx* past DiagnosisKeepSeconds (a reading of an uninjured soldier) and the
        /// Move* cache (rebuilt on demand).</summary>
        private static bool Idle(SoldierWounds w)
        {
            if (w.State != MedState.Awake) return false;
            if (w.ActiveCount() > 0) return false;

            // A diagnosis is data the medic is reading off the panel. Dropping the entry threw it away
            // (the vitals box appeared and vanished a few seconds later), so keep it for a while.
            if (w.HasDiagnosis && Time.time - w.DxTime < DiagnosisKeepSeconds) return false;

            // pain, real or masked, and the high-pain pass-out timer
            if (w.PainValue > 0f || w.PainSuppress > 0f || w.HighPainSince >= 0f) return false;

            // downed / arrest / CPR (CollapsedByVitals only ever marks a down, which these already keep)
            if (w.DownedAt > 0f || w.Arrested || w.ArrestTimeLeft > 0f) return false;
            if (w.CprUntil > Time.time) return false;

            // Drugs still in the body. Meds keeps metabolising after the pain it masked is gone, and
            // Resistance/Overdosed are the effects it drives - dropping the entry here would make an
            // administered dose silently vanish.
            if (w.Meds.Count > 0 || w.Overdosed) return false;
            if (Mathf.Abs(w.Resistance - Physiology.DefaultResistance) > 0.5f) return false;

            // A tourniquet is a treatment that is still ON the soldier and has to come off again;
            // its wound can read as inactive while it is holding.
            if (w.Tourniquets.Count > 0) return false;

            // fluids
            if (w.IvRemaining > 0f || w.IvCount > 0) return false;

            if (w.PoseLockUntil > Time.time) return false;
            if (w.VitalsInit && w.Blood < 0.999f) return false;
            return true;
        }

        /// <summary>The local player is gone but the battle goes on (the respawn screen): drop what belongs
        /// to the player's own life - the open panel, a running procedure or medic/tent heal, the screen
        /// feedback, the HP watchdog, pending grenade checks on the old body - and leave every soldier's
        /// medical state and the world ticks alone.</summary>
        private static void ResetPlayerLocal()
        {
            try { MedicUI.Close(); } catch { }
            try { Procedure.Cancel(); } catch { }
            MedicCall.Reset();
            BloodScreen.Reset();
            MedicUI.ClearCaches();
            Heartbeat.Reset();
            HitPeek.Reset();
            Hooks.ResetPlayerLocal();       // bandage-prompt window, grenade checks on the old body
            PainIntensity = 0f;
            _lastPlayerHp = -1;             // the next life's HP is not a drop from this one
            _inferSince = -1f;
            _maxStamina = 1f; _staminaFor = IntPtr.Zero;   // the next life learns its own stamina max (IL2CPP reuses pointers)
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo("[session] player gone (respawn screen): player-local state reset, soldiers keep ticking");
        }

        /// <summary>Drop every per-soldier reference the mod holds. Only on a scene change or a mission
        /// teardown (player gone and the battle no longer live), so no freed IL2CPP pointer is ever touched
        /// after a scene unload.</summary>
        private static void FlushSession(bool sceneChanged)
        {
            ResetPlayerLocal();
            BloodScreen.Reset(forgetTexture: true);   // the next scene brings its own splash object
            Hooks.ClearPromptHideState();   // drop CanvasGroup refs without touching freed objects
            Marks.Reset();                  // medic positions (the tent cache is per scene, see Tick)
            Supply.Reset();
            // Every per-soldier cache: wounds, Lua/inventory, views, kits, markers, AI heals, snapshots. On a
            // scene change the new map's soldiers have already run Start (spawn kits and the marker list
            // queued), so what belongs to the new scene stays.
            SoldierRegistry.ClearAll(sceneChanged ? Interop.TickScene : 0);
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo("[session] flushed all soldier state (scene change / mission teardown)");
        }

        private static bool _wasOpen;
        private static bool _camFrozen;
        private static Quaternion _frozenRot;

        /// <summary>Release the cursor and camera lock WITHOUT running the rest of the tick. Used
        /// when the mod is switched off mid-session while the overlay is still up.</summary>
        public static void ReleaseOverlay() { ManageOverlay(); }

        /// <summary>
        /// While the overlay is open: free the cursor and FREEZE the camera transform. Freezing
        /// the transform after the game has posed the camera (this runs in PlayerController's
        /// LateUpdate postfix) blocks turning regardless of which input path drives it — more
        /// robust than trying to patch every camera-input method. Movement is zeroed in the Move
        /// prefix and fire is blocked in the Shoot prefix. Restores on close.
        /// </summary>
        private static void ManageOverlay()
        {
            // Panel visible (Open and not mid-procedure): free the cursor and freeze the camera.
            if (MedicUI.Open && !MedicUI.Hidden)
            {
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
                var cam = PainFX.Cam();
                if (cam != null)
                {
                    try
                    {
                        if (!_camFrozen) { _frozenRot = cam.transform.rotation; _camFrozen = true; }
                        else cam.transform.rotation = _frozenRot;
                    }
                    catch { }
                }
                _wasOpen = true;
            }
            // Procedure running (Open but Hidden): keep fire/move blocked (Open stays true) but
            // DON'T freeze the camera or grab the cursor, so the heal animation plays naturally.
            else if (MedicUI.Open && MedicUI.Hidden)
            {
                _camFrozen = false;
                try { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } catch { }
                _wasOpen = true;
            }
            else
            {
                _camFrozen = false;
                if (_wasOpen)
                {
                    try { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } catch { }
                    _wasOpen = false;
                }
            }
        }

        /// <summary>While the knockdown lock is active, hold the knockdown pose so input can't
        /// immediately pop the soldier back up. After it expires the player stands normally.</summary>
        private static void EnforcePoseLock(Soldier s, SoldierWounds w)
        {
            if (Time.time >= w.PoseLockUntil) return;
            if (Interop.TryGetPose(s, out var pose) && pose != w.PoseLockPose) Interop.SetPose(s, w.PoseLockPose);
        }

        /// <summary>Global pain intensity — a CONTINUOUS value driven by the soldier's raw PainValue,
        /// so the screen flash fades smoothly as pain declines (linearly, or faster under painkillers)
        /// instead of stepping down tier-by-tier and popping to nothing. Piecewise-linear through the
        /// tier band-tops, so each tier's peak still equals its configured intensity.</summary>
        public static float ComputeIntensity(SoldierWounds w)
        {
            var c = Plugin.C;
            float v = w.PerceivedPain;   // painkillers mask it
            if (v <= 0f) return 0f;

            float light = c.LightIntensity.Value, med = c.MedIntensity.Value, high = c.HighIntensity.Value;
            if (v <= Pain.LightVal) return Mathf.Lerp(0f,    light, v / Pain.LightVal);
            if (v <= Pain.MedVal)   return Mathf.Lerp(light, med,   (v - Pain.LightVal) / (Pain.MedVal - Pain.LightVal));
            if (v <= Pain.HighVal)  return Mathf.Lerp(med,   high,  (v - Pain.MedVal)   / (Pain.HighVal - Pain.MedVal));
            return high;
        }

        /// <summary>Movement multiplier from the legs (chest drives stamina instead). Applied to walk AND
        /// sprint by scaling delta time in the Move prefix, so it multiplies cleanly with ImmersiveEffects'
        /// own load-penalty / sprint-ramp multiplier on the same lever:
        ///   - actively bleeding leg: LegBleedWalk (-20%)
        ///   - else a tourniqueted leg: TourniquetDebuffFraction of that (-10% at 0.5)
        ///   - each leg still carrying an UNSTITCHED wound or a fracture: LimpWalk (-4%, limping)
        ///   - unsplinted broken leg: LegFractureWalk, plus LegFractureSprint while sprinting</summary>
        public static float ComputeWalkFactor(SoldierWounds w, bool sprinting = false)
        {
            var c = Plugin.C;
            float f = 1f;
            if (w.LegBleeding()) f *= c.LegBleedWalk.Value;
            else if (w.LegTourniquet()) f *= TourniquetShare(c.LegBleedWalk.Value);
            foreach (var leg in Legs)
            {
                if (w.Limping(leg)) f *= c.LimpWalk.Value;
                if (w.FractureState(leg) == 1)
                {
                    f *= c.LegFractureWalk.Value;
                    if (sprinting) f *= c.LegFractureSprint.Value;
                }
            }
            return Mathf.Clamp(f, 0.1f, 1f);
        }

        private static readonly BodyPartType[] Legs = { BodyPartType.leg_l, BodyPartType.leg_r };

        /// <summary>A walk multiplier reduced to the tourniquet's share of its penalty: 0.80 at 0.5 → 0.90.</summary>
        private static float TourniquetShare(float bleedMult)
        {
            float frac = Mathf.Clamp(Plugin.C.TourniquetDebuffFraction.Value, 0f, 0.5f);
            return 1f - (1f - Mathf.Clamp01(bleedMult)) * frac;
        }

        // Largest player stamina seen while NOT capped — the "max" we scale the chest/head cut from
        // (the game exposes no max-stamina value, so it's learned, like ImmersiveEffects does). Learned per
        // soldier: it was never reset, so one soldier's larger pool capped the next one (another class,
        // another mission) against the wrong max for the rest of the session.
        private static float _maxStamina = 1f;
        private static IntPtr _staminaFor;

        /// <summary>
        /// Local-player only. While bleeding, pin an ImmersiveEffects suppression floor:
        /// base (any bleed) + arm bonus (arm bleed, or its tourniquet share for a tourniqueted arm) +
        /// pain bonus (whenever in pain, even after the bleed is bandaged) + broken-arm sway.
        /// The floor is pushed every frame so it releases to 0 the moment bleeding/pain clears.
        /// </summary>
        private static void UpdateSuppression(SoldierWounds w)
        {
            var c = Plugin.C;

            // --- suppression floor into ImmersiveEffects ---
            float floor = 0f;
            if (w != null)
            {
                if (w.AnyBleeding()) floor += c.BleedSuppression.Value;      // base while bleeding
                if (w.ArmBleeding()) floor += c.ArmBleedSuppression.Value;   // +35 on top for arms
                else if (w.ArmTourniquet())                                  // a tourniquet's share of it
                    floor += c.ArmBleedSuppression.Value * Mathf.Clamp(c.TourniquetDebuffFraction.Value, 0f, 0.5f);
                floor += PainSuppression(w.Pain);                           // persists while in pain
                // Broken arms shake the aim, less once splinted.
                foreach (var arm in Arms)
                {
                    int f = w.FractureState(arm);
                    if (f == 1) floor += c.ArmFractureSway.Value;
                    else if (f == -1) floor += c.ArmSplintSway.Value;
                }
            }
            Interop.SetExternalStressFloor(floor);
        }

        /// <summary>Chest/head bleeding caps total stamina to a fraction of its max. Its own method:
        /// staminaCount is a game member (see the class comment).</summary>
        private static void CapStamina(Soldier player, SoldierWounds w)
        {
            if (player == null) return;
            var p = Interop.PtrOf(player);
            if (p != _staminaFor) { _staminaFor = p; _maxStamina = 1f; }   // a new soldier learns its own max
            float cur = player.staminaCount;
            if (w != null && w.ChestHeadBleeding())
            {
                float cap = _maxStamina * Mathf.Clamp01(Plugin.C.ChestHeadBleedStaminaFactor.Value);
                if (cur > cap) player.staminaCount = cap;
            }
            else if (cur > _maxStamina)
            {
                _maxStamina = cur;   // learn the true max only while unclamped
            }
        }

        // Last-seen player HP for the damage watchdog (-1 = not yet read; reset by big jumps/respawn).
        private static int _lastPlayerHp = -1;

        /// <summary>
        /// A safety net for "got hit but no wound": our wound sensor only fires on LOCAL damage hooks,
        /// but as an MP client your own damage is computed on the host and only your HP is synced, so
        /// no hook runs. Watch the player's HP; if it drops and our hooks didn't just record a wound,
        /// infer a bleeding wound so there's always something to treat. Local player only.
        /// </summary>
        private static void WatchPlayerDamage(Soldier player)
        {
            // Only a non-host MP client loses HP without our hooks seeing it. In SP and on the host every
            // real hit goes through them, and the watchdog only turned unhooked HP loss (falls, fire
            // ticks, the game's own bleed) into phantom Large bullet wounds.
            if (!Interop.MpNonHost()) { _lastPlayerHp = -1; _inferSince = -1f; return; }

            int hp;
            try { var l = Items.Lua(player); hp = l != null ? l.getHealth() : -1; } catch { hp = -1; }
            if (hp < 0) return;

            // The window has to cover the HP sync arriving AFTER the hit-flinch that already made the
            // wound (an MP client gets the flinch and the new life value separately): at 0.4 s a late
            // sync inferred a second wound for the same hit.
            float now = Time.time;
            if (_lastPlayerHp >= 0 && hp < _lastPlayerHp - 1 &&
                now - WoundLogic.LastPlayerWoundTime > WatchdogWindow)
            {
                // A modded host: the hit's wound is made on the shooter's machine and arrives as a snapshot,
                // usually just AFTER the life update (the flinch no longer makes one - see Hooks). Wait for
                // it, and only infer if none came (the shooter doesn't run the mod).
                if (MpSync.HostSendsWounds) { if (_inferSince < 0f) _inferSince = now; }
                else WoundLogic.InferPlayerWound(player);
            }
            if (_inferSince >= 0f && now - _inferSince > WatchdogWindow)
            {
                bool arrived = WoundLogic.LastPlayerWoundTime >= _inferSince;
                if (Plugin.C.DebugLogging.Value)
                    Plugin.L.LogInfo($"[mp] HP-drop watchdog: {(arrived ? "the shooter's wound arrived - nothing to infer" : "no wound arrived - inferring one")}");
                if (!arrived) WoundLogic.InferPlayerWound(player);
                _inferSince = -1f;
            }
            _lastPlayerHp = hp;
        }

        private const float WatchdogWindow = 1.5f;
        private static float _inferSince = -1f;   // an unexplained HP drop waiting for the shooter's snapshot

        private static readonly BodyPartType[] Arms = { BodyPartType.arm_l, BodyPartType.arm_r };

        private static float PainSuppression(PainTier tier)
        {
            var c = Plugin.C;
            switch (tier)
            {
                case PainTier.Light:  return c.PainSuppLight.Value;
                case PainTier.Medium: return c.PainSuppMed.Value;
                case PainTier.High:   return c.PainSuppHigh.Value;
                default:              return 0f;
            }
        }
    }
}
