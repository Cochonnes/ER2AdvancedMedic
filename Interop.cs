using System;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace AdvancedMedic
{
    /// <summary>
    /// Thin helpers around the game/runtime: local-player resolution, input, native pointer
    /// identity, safe casts, multiplayer authority, the shared medic test, and ImmersiveEffects
    /// presence detection.
    /// </summary>
    internal static class Interop
    {
        // The most recently seen PlayerController instance (captured in the hooks).
        internal static PlayerController PC;

        public static bool ImmersiveShakePresent { get; private set; }

        private static bool _loggedResolveError;

        // Photon room state, read at most once per frame. InRoom / IsMasterClient are several native
        // calls each, and they were asked per tracked soldier per vitals step (via HasAuthority /
        // DownedApplies / OwnsState) - roughly 3k calls a second at 100 soldiers. Neither can change
        // in a way that matters inside one frame.
        private static int _roomFrame = -1;
        private static bool _inRoom, _master;

        private static void RefreshRoom()
        {
            int f = Time.frameCount;
            if (f == _roomFrame) return;
            _roomFrame = f;
            try
            {
                _inRoom = Photon.Pun.PhotonNetwork.InRoom;
                _master = _inRoom && Photon.Pun.PhotonNetwork.IsMasterClient;
            }
            catch { _inRoom = false; _master = false; }
        }

        /// <summary>In any Photon room (multiplayer). False in single player. Frame-cached.</summary>
        public static bool InMpRoom() { RefreshRoom(); return _inRoom; }

        /// <summary>A non-host MP client: the local player's damage is HOST-authoritative here, so our
        /// Damage/DamageWithAnimation hooks never fire for us — the wound/lethality has to be reconstructed
        /// from client-side signals (the PlayDamageAnimation flinch). False in SP and on the host, where the
        /// normal damage path handles everything. Frame-cached.</summary>
        public static bool MpNonHost() { RefreshRoom(); return _inRoom && !_master; }

        // The local player, resolved at most once per frame (per PlayerController). It was resolved
        // afresh about six times per OnGUI Repaint (flash, desaturation, blood screen, markers, panel
        // rows, the guard in the GUI hook) plus the tick's own calls - a native property read and an
        // interop wrapper lookup each time, for an answer that cannot change inside a frame.
        private static int _plFrame = -1;
        private static PlayerController _plPc;
        private static Soldier _pl;

        /// <summary>Local controlled Soldier, or null. Frame-cached.</summary>
        public static Soldier Player()
        {
            int f = Time.frameCount;
            var pc = PC;
            if (f == _plFrame && ReferenceEquals(pc, _plPc)) return _pl;
            Soldier s;
            try { s = pc != null ? pc.ControlledCharacter : null; }
            catch (Exception e)
            {
                if (!_loggedResolveError) { _loggedResolveError = true; Plugin.L.LogError("Could not resolve player Soldier: " + e.Message); }
                s = null;
            }
            _plFrame = f; _plPc = pc; _pl = s;
            return s;
        }

        // The local player's native pointer, resolved at most once per frame. Interop.Player() is a
        // native property read plus an interop cache lookup, and it was being called from inside
        // per-soldier loops and from the Move prefix - which the game calls for EVERY soldier, every
        // frame - purely to answer "is this me?". A pointer compare against this answers that for
        // free.
        private static int _ppFrame = -1;
        private static IntPtr _pp;

        /// <summary>The local player Soldier's native pointer, cached for the current frame.</summary>
        public static IntPtr PlayerPtr()
        {
            int f = Time.frameCount;
            if (f != _ppFrame) { _ppFrame = f; _pp = PtrOf(Player()); }
            return _pp;
        }

        /// <summary>Is this soldier the local player? Pointer compare against the frame cache.</summary>
        public static bool IsPlayer(Il2CppObjectBase o)
        {
            var p = PtrOf(o);
            return p != IntPtr.Zero && p == PlayerPtr();
        }

        /// <summary>Native IL2CPP identity of an object (survives wrapper base-type casts).</summary>
        public static IntPtr PtrOf(Il2CppObjectBase o)
        {
            try { return o == null ? IntPtr.Zero : o.Pointer; } catch { return IntPtr.Zero; }
        }

        public static bool SamePtr(Il2CppObjectBase a, Il2CppObjectBase b)
        {
            var pa = PtrOf(a); var pb = PtrOf(b);
            return pa != IntPtr.Zero && pa == pb;
        }

        /// <summary>
        /// Another HUMAN player's soldier (MP only), on ANY machine.
        ///
        /// Soldier.IsAI() can't decide this on a client: it is only true where the AI brain runs (the
        /// host), so on a client every host-run AI reads IsAI()=false. Photon ownership can't either: the
        /// host's own avatar and every AI are all master-owned, so a client took the host player for AI.
        ///
        /// What the game itself uses (BodyPart.HitPart's difficulty multiplier, disassembled 2026-09-27):
        /// `IsPlayer() || GetSyncher().controlled_by_player`. SyncSoldier.controlled_by_player (+0x135) is
        /// written by the "SetControlledByPlayer" RPC, which PlayerController.SetPlayer sends AllBuffered
        /// (true) whenever a player takes a soldier and UnsetAsControlledByPlayer sends AllBuffered (false)
        /// when they leave it - so every machine, late joiners included, holds the same flag. Only if that
        /// flag can't be read do we fall back to the old ownership heuristic. On the master, !IsAI() (known
        /// correct there) also still counts, so the host's view never gets narrower than before.
        /// Cached per soldier for HumanCacheSeconds: it is asked per hit and per drawn name.
        /// </summary>
        public static bool IsOtherHuman(Soldier s)
        {
            try
            {
                if (s == null || !InMpRoom()) return false;
                if (IsPlayer(s)) return false;
                var p = PtrOf(s);
                float now = Time.unscaledTime;
                if (p != IntPtr.Zero && _humanCache.TryGetValue(p, out var hc) && now < hc.Until) return hc.Human;

                bool master = IsMaster();
                bool human;
                if (TryPlayerControlled(s, out bool controlled))
                {
                    human = controlled;
                    if (!human && master) { try { human = !IsAIRaw(s); } catch { } }
                }
                else if (master) { try { human = !IsAIRaw(s); } catch { human = false; } }
                else
                {
                    // Flag unreadable (a future update renamed it): the old ownership heuristic, which
                    // misses only the host's own avatar.
                    var pv = ViewOf(s);
                    var owner = pv != null && !pv.IsMine ? pv.Owner : null;
                    human = owner != null && !owner.IsMasterClient;
                }
                if (p != IntPtr.Zero) _humanCache[p] = new HumanEntry { Until = now + HumanCacheSeconds, Human = human };
                return human;
            }
            catch { return false; }
        }

        private struct HumanEntry { public float Until; public bool Human; }
        private const float HumanCacheSeconds = 0.5f;
        private static readonly System.Collections.Generic.Dictionary<IntPtr, HumanEntry> _humanCache
            = new System.Collections.Generic.Dictionary<IntPtr, HumanEntry>();
        private static bool _controlledDead;

        /// <summary>The game's own "a player controls this soldier" flag (see IsOtherHuman). False with
        /// known=false when it can't be read (no syncher, or the member vanished in an update).</summary>
        public static bool TryPlayerControlled(Soldier s, out bool controlled)
        {
            controlled = false;
            if (s == null || _controlledDead) return false;
            try { return ControlledRaw(s, out controlled); }
            catch (Exception e)
            {
                _controlledDead = true;
                Plugin.L.LogWarning("SyncSoldier.IsControlledByAPlayer unavailable, using the ownership fallback: " + e.Message);
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ControlledRaw(Soldier s, out bool controlled)
        {
            controlled = false;
            var sy = s.GetSyncher();
            if (sy == null) return false;
            controlled = sy.IsControlledByAPlayer();
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static bool IsAIRaw(Soldier s) => s.IsAI();

        /// <summary>This machine is the Photon master client (the host). False in single player. Frame-cached.</summary>
        public static bool IsMaster() { RefreshRoom(); return _master; }

        /// <summary>A human-played soldier: the local player, or (MP) another player's avatar. The host
        /// has to know this for the soldiers of its CLIENTS too - their damage is computed there, and
        /// treating them as AI gave them the AI damage rules (no anti-oneshot scale, AI kill odds).</summary>
        public static bool IsHuman(Soldier s) => s != null && (IsPlayer(s) || IsOtherHuman(s));

        /// <summary>Really dead. NOT just !IsAlive: a DOWNED soldier sits at life 0, where the game's
        /// IsAlive already reads false while IsInjured is still true.</summary>
        public static bool IsDead(Creature s)
        {
            if (s == null) return true;
            if (IsDowned(s)) return false;
            try { return !IsAliveRaw(s); } catch { return true; }
        }

        /// <summary>Downed (the game's incapacitated state). Null reads false; an unreadable member
        /// reads <paramref name="fallback"/>. The one place that touches Creature.IsInjured (it used
        /// to be an inline try/read at ~19 sites).</summary>
        public static bool IsDowned(Creature s, bool fallback = false)
        {
            if (s == null) return false;
            try { return IsInjuredRaw(s); } catch { return fallback; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static bool IsInjuredRaw(Creature s) => s.IsInjured;
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool IsAliveRaw(Creature s) => s.IsAlive;

        /// <summary>An occupant of ANY vehicle (tank, plane, car, static gun). Unreadable reads false.
        /// The one copy (the damage rules, the grenade check and the bandage-clip gate each had their own).</summary>
        public static bool IsOnVehicle(Soldier s)
        {
            if (s == null) return false;
            try { return OnVehicleRaw(s); } catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static bool OnVehicleRaw(Soldier s) => s.IsOnVehicle();

        /// <summary>The game's own bleeding flag. Unreadable reads <paramref name="fallback"/>.</summary>
        public static bool IsBleeding(Soldier s, bool fallback = false)
        {
            if (s == null) return false;
            try { return IsBleedingRaw(s); } catch { return fallback; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static bool IsBleedingRaw(Soldier s) => s.isBleeding;

        /// <summary>Set the game's bleeding flag (drives its own indicators). Failures are swallowed: a
        /// missing member costs only this call, never the caller (JIT trap).</summary>
        public static void SetBleeding(Soldier s, bool on)
        {
            if (s == null) return;
            try { SetBleedingRaw(s, on); } catch { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static void SetBleedingRaw(Soldier s, bool on) => s.SetBleeding(on);

        /// <summary>Soldier.SetIncapacitated (down / stand up) - local only, the game doesn't network it
        /// (see NetLife). Our Harmony prefix/postfix on it still run.</summary>
        public static void SetIncapacitated(Soldier s, bool down)
        {
            if (s == null) return;
            try { SetIncapacitatedRaw(s, down); } catch { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static void SetIncapacitatedRaw(Soldier s, bool down) => s.SetIncapacitated(down);

        /// <summary>The soldier's current life value, or -1 if unreadable.</summary>
        public static int Life(Soldier s)
        {
            if (s == null) return -1;
            try { return LifeRaw(s); } catch { return -1; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static int LifeRaw(Soldier s) => s.life_total;

        // ---- positions / range (one copy; the medic call, the procedure and the targeting each had one) ----

        public static bool TryPos(Soldier s, out Vector3 pos)
        {
            try { if (s != null) { pos = s.transform.position; return true; } } catch { }
            pos = Vector3.zero;
            return false;
        }

        /// <summary>Metres between two soldiers, or float.MaxValue if either position can't be read.</summary>
        public static float Distance(Soldier a, Soldier b)
        {
            if (!TryPos(a, out var pa) || !TryPos(b, out var pb)) return float.MaxValue;
            return Vector3.Distance(pa, pb);
        }

        /// <summary>Within <paramref name="range"/> metres of each other. <paramref name="unknown"/> is the
        /// answer when a position can't be read.</summary>
        public static bool WithinRange(Soldier a, Soldier b, float range, bool unknown)
        {
            if (!TryPos(a, out var pa) || !TryPos(b, out var pb)) return unknown;
            return (pa - pb).sqrMagnitude <= range * range;
        }

        // ---- mission liveness ----------------------------------------------------------------
        private static bool _missionProbeDead;

        /// <summary>A battle is loaded and not over (BattleManager alive and not ended). This is what tells
        /// the respawn screen (player gone, battle still running: the world keeps ticking) apart from a
        /// mission being torn down (exit to menu, battle over: every soldier reference must go). An
        /// unreadable probe reads false, which keeps the old, safe behaviour of flushing on player loss.</summary>
        public static bool MissionLive()
        {
            if (_missionProbeDead) return false;
            try { return MissionLiveRaw(); }
            catch (Exception e)
            {
                _missionProbeDead = true;
                Plugin.L.LogError("BattleManager liveness probe unavailable - soldier state is flushed whenever the player is gone (older behaviour): " + e.Message);
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool MissionLiveRaw()
        {
            var bm = BattleManager.instance;
            return bm != null && !BattleManager.IsBattleEnded();
        }

        /// <summary>Kill a soldier this machine is the authority for. In a room the kill has to be the
        /// networked one: a plain Kill() only killed the local copy, so every client kept a downed AI
        /// that never died.</summary>
        public static void KillAuthoritative(Soldier s)
        {
            if (s == null) return;
            if (InMpRoom())
            {
                try { KillSynchedRaw(s); return; } catch { }
            }
            try { KillRaw(s); } catch { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static void KillSynchedRaw(Soldier s) => s.KillSynched();
        [MethodImpl(MethodImplOptions.NoInlining)] private static void KillRaw(Soldier s) => s.Kill();

        public static T CastTo<T>(Il2CppObjectBase o) where T : Il2CppObjectBase
        {
            try { return o == null ? null : o.TryCast<T>(); } catch { return null; }
        }

        // ---- multiplayer authority --------------------------------------------------------------
        // Who decides a soldier's downed/awake state. Photon: the machine whose PhotonView it is (the
        // host for every AI, each player for their own avatar). A client that downed or blocked the
        // revive of a host-run AI locally diverged from the host for good: the host kept running the
        // AI (moving, shooting - replicated to the client) while the client's copy lay in the downed
        // animation, and nothing ever re-synced it. Everything that changes downed state now asks this.

        // PhotonView per soldier pointer: GetComponent + a parent walk is a native search, and the
        // authority test runs per tracked soldier at the vitals rate. Pruned with the soldier.
        private static readonly System.Collections.Generic.Dictionary<IntPtr, Photon.Pun.PhotonView> _views
            = new System.Collections.Generic.Dictionary<IntPtr, Photon.Pun.PhotonView>();

        public static Photon.Pun.PhotonView ViewOf(Soldier s)
        {
            var p = PtrOf(s);
            if (p == IntPtr.Zero) return null;
            if (_views.TryGetValue(p, out var pv) && pv != null) return pv;
            pv = null;
            try { pv = s.GetComponent<Photon.Pun.PhotonView>(); } catch { }
            if (pv == null) { try { pv = s.GetComponentInParent<Photon.Pun.PhotonView>(); } catch { } }
            if (pv != null) _views[p] = pv;
            return pv;
        }

        public static void PruneView(IntPtr p) { if (p != IntPtr.Zero) { _views.Remove(p); _humanCache.Remove(p); } }
        public static void ClearViews() { _views.Clear(); _humanCache.Clear(); }

        /// <summary>This machine owns the soldier's state: always in single player; in a room, when the
        /// soldier's PhotonView is ours (no view: the master client decides).</summary>
        public static bool HasAuthority(Soldier s)
        {
            if (!InMpRoom()) return true;
            var pv = ViewOf(s);
            try
            {
                if (pv == null) return Photon.Pun.PhotonNetwork.IsMasterClient;
                return pv.IsMine;
            }
            catch { return false; }
        }

        /// <summary>Photon actor number of the soldier's authority (its view's controller; the master
        /// for a room object), or -1 if unknown. Used to tell whether a received snapshot came from it.</summary>
        public static int AuthorityActor(Soldier s)
        {
            try
            {
                var pv = ViewOf(s);
                int a = pv != null ? pv.ControllerActorNr : 0;
                if (a <= 0 && Photon.Pun.PhotonNetwork.CurrentRoom != null) a = Photon.Pun.PhotonNetwork.CurrentRoom.MasterClientId;
                return a > 0 ? a : -1;
            }
            catch { return -1; }
        }

        // ---- the medic test ---------------------------------------------------------------------
        /// <summary>"Is a medic" for the markers, the X-heal and the spawn kit: the class flag or the
        /// game's own syringe flag; checkInventory also counts a carried syringe item (needed just after
        /// spawn, before the class flags are set - an inventory walk, so not for map-wide scans).
        /// NOT for the overlay's medics-only actions - those use <see cref="IsClassMedic"/>.</summary>
        public static bool IsMedic(Soldier s, bool checkInventory = false)
        {
            if (s == null) return false;
            if (IsClassMedic(s)) return true;
            try { if (HasSyringeRaw(s)) return true; } catch { }
            if (checkInventory) { try { if (Items.CountOf(s, Plugin.C.IdSyringe.Value) > 0) return true; } catch { } }
            return false;
        }

        /// <summary>Medic by CLASS only. Stitching and the blood IV stay a medic's job: carrying a
        /// syringe does not make a rifleman a medic (the user's call, 2026-09-25).</summary>
        public static bool IsClassMedic(Soldier s)
        {
            if (s == null) return false;
            try { return IsMedicRaw(s); } catch { return false; }
        }

        /// <summary>The game's own "carries a syringe" flag. Unreadable reads false.</summary>
        public static bool HasSyringeFlag(Soldier s)
        {
            if (s == null) return false;
            try { return HasSyringeRaw(s); } catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static bool IsMedicRaw(Soldier s) => s.IsMedic();
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool HasSyringeRaw(Soldier s) => s.hasSyringe;

        // ---- pose (the hit knockdown and its lock) ----------------------------------------------
        // Game members in their own methods (JIT trap): a Pose/SetPose that vanished in an update used to
        // fail LimbEffects.OnHit at JIT time, and since ApplyHit called it bare, the rest of the hit - its
        // MP snapshot above all - went with it.

        /// <summary>The soldier's current pose; false if it can't be read.</summary>
        public static bool TryGetPose(Soldier s, out SoldierPose pose)
        {
            pose = default;
            if (s == null) return false;
            try { pose = PoseRaw(s); return true; } catch { return false; }
        }

        /// <summary>Force a pose (Soldier.SetPose(pose, true)). Failures cost only this call.</summary>
        public static void SetPose(Soldier s, SoldierPose pose)
        {
            if (s == null) return;
            try { SetPoseRaw(s, pose); } catch { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static SoldierPose PoseRaw(Soldier s) => s.Pose;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void SetPoseRaw(Soldier s, SoldierPose pose) => s.SetPose(pose, true);

        // ---- isolated UI-state reads (JIT trap) -------------------------------------------------
        // A removed game member doesn't throw where it is used: the JIT refuses the WHOLE method that
        // references it. So these UI members each live in their own tiny method, called through a
        // wrapper that catches: a future removal costs this one check (latched off after the first
        // failure), not the overlay's open gate or the per-frame tick.
        private static bool _pauseDead, _chatDead;

        public static bool GamePaused()
        {
            if (_pauseDead) return false;
            try { return PausedRaw(); }
            catch (Exception e) { _pauseDead = true; Plugin.L.LogWarning("Pause.IsPaused unavailable: " + e.Message); return false; }
        }

        public static bool ChatOpen()
        {
            if (_chatDead) return false;
            try { return ChatOpenRaw(); }
            catch (Exception e) { _chatDead = true; Plugin.L.LogWarning("PlayerGUI.IsChatOpen unavailable: " + e.Message); return false; }
        }

        /// <summary>Gameplay keys must not fire: the game is paused / in a menu, or the chat is open (typing
        /// an "x" in chat next to a medic used to start a heal).</summary>
        public static bool InputBlocked() => GamePaused() || ChatOpen();

        [MethodImpl(MethodImplOptions.NoInlining)] private static bool PausedRaw() => Pause.IsPaused();
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool ChatOpenRaw() => PlayerGUI.IsChatOpen;

        // Parsed key names, cached by the config string. Enum.Parse with ignoreCase is a reflection
        // call that walks the KeyCode name table and allocates, and this runs two or three times
        // EVERY frame (the open key, the debug key, the medic-call key), so the result is memoised
        // and only re-parsed when the config string itself changes.
        private static readonly System.Collections.Generic.Dictionary<string, KeyCode> _keyCache
            = new System.Collections.Generic.Dictionary<string, KeyCode>(StringComparer.Ordinal);

        /// <summary>The KeyCode a config string names, or KeyCode.None if it is not a key name.</summary>
        private static KeyCode KeyOf(string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return KeyCode.None;
            KeyCode kc;
            if (_keyCache.TryGetValue(keyName, out kc)) return kc;
            try { kc = (KeyCode)Enum.Parse(typeof(KeyCode), keyName, true); }
            catch { kc = KeyCode.None; }   // a bad config string is cached as None, so it parses once
            _keyCache[keyName] = kc;
            return kc;
        }

        public static bool GetKeyDown(string keyName)
        {
            try
            {
                var kc = KeyOf(keyName);
                return kc != KeyCode.None && Input.GetKeyDown(kc);
            }
            catch { return false; }
        }

        // ---- scene-transition guard ----------------------------------------------------------
        // A scene unload frees every mission Soldier's native object. Our static soldier maps keep
        // their pointers, so the next per-frame tick would call into freed memory (native crash the
        // try/catch can't catch). Detect the active-scene change so the tick can flush first.
        private static int _lastScene;
        private static bool _sceneInit;

        /// <summary>True on the first observation and whenever the active scene changes.
        ///
        /// Compares Scene.m_Handle rather than Scene.name: reading the name marshals a fresh managed
        /// string out of the runtime EVERY frame just to throw it away on a match. The handle is a
        /// plain int, it is stable for the life of a loaded scene, and a new load always gets a new
        /// one - so this is also slightly MORE sensitive than the name test was (a reload of the
        /// same map used to read as "no change", which would have left freed pointers in our
        /// maps).</summary>
        public static bool SceneChanged()
        {
            try
            {
                int cur = UnityEngine.SceneManagement.SceneManager.GetActiveScene().m_Handle;
                if (!_sceneInit) { _sceneInit = true; _lastScene = cur; return true; }
                if (cur != _lastScene) { _lastScene = cur; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>The scene the tick last saw (SceneChanged's value; 0 before the first tick).</summary>
        public static int TickScene => _lastScene;

        /// <summary>The active scene's handle, read NOW (0 if unreadable). Soldier.Start of a new map runs
        /// before the tick notices the scene change, so what a spawn hook queues is tagged with this, and
        /// the scene flush then keeps what belongs to the new scene instead of dropping it with the old.</summary>
        public static int ActiveSceneHandle()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().m_Handle; }
            catch { return 0; }
        }

        /// <summary>
        /// Detect a co-installed Immersive Effects (GUID cochonnes.er2.immersiveeffects). Used only to
        /// auto-suppress our self-contained pain shake so two shakes never stack (decision #6).
        /// </summary>
        public static void DetectImmersiveShake()
        {
            ImmersiveShakePresent = false;
            try
            {
                var plugins = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance?.Plugins;
                if (plugins != null && (plugins.ContainsKey("cochonnes.er2.immersiveeffects")
                                     || plugins.ContainsKey("er2.immersiveeffects")))
                {
                    ImmersiveShakePresent = true;
                    return;
                }
            }
            catch { }

            // Fallback: look for the deployed dll on disk.
            try
            {
                var root = Paths.PluginPath();
                if (root != null && (
                        File.Exists(Path.Combine(root, "ER2ImmersiveEffects", "ER2ImmersiveEffects.dll")) ||
                        File.Exists(Path.Combine(root, "ER2ImmersiveEffects.dll")) ||
                        File.Exists(Path.Combine(root, "ImmersiveEffects", "ImmersiveEffects.dll")) ||
                        File.Exists(Path.Combine(root, "ImmersiveEffects.dll"))))
                    ImmersiveShakePresent = true;
            }
            catch { }
        }

        // ---- ImmersiveEffects suppression bridge --------------------------------------------
        // Reflect into ImmersiveEffects.State.ExternalStressFloor (public static float) to pin a
        // minimum suppression while bleeding / in pain. No-op when the mod isn't installed.
        // A COMPILED setter, not FieldInfo.SetValue. The floor is pushed every frame, and SetValue
        // boxes the float and walks the reflection stack on each one; an Expression-compiled
        // Action<float> is a direct store (the same trick DynamicWeather's GameCall uses). Paired
        // with a change guard: ImmersiveEffects only ever READS this field (Mathf.Max against its
        // own stress), so nothing else moves it behind our back and a repeat write is pure waste.
        private static Action<float> _extStressSet;
        private static bool _extStressResolved;
        private static float _extStressLast = float.NaN;

        /// <summary>Pin the ImmersiveEffects suppression floor (0 releases it). No-op if absent.</summary>
        public static void SetExternalStressFloor(float value)
        {
            var set = ResolveExtStressFloor();
            if (set == null) return;
            if (value == _extStressLast) return;   // unchanged - the floor is re-pushed every frame
            try { set(value); _extStressLast = value; } catch { }
        }

        private static Action<float> ResolveExtStressFloor()
        {
            if (_extStressResolved) return _extStressSet;
            _extStressResolved = true;
            System.Reflection.FieldInfo _extStressFloor = null;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var an = asm.GetName().Name;
                    if (an != "ER2ImmersiveEffects" && an != "ImmersiveEffects") continue;   // assembly renamed; namespace unchanged
                    var t = asm.GetType("ImmersiveEffects.State");
                    if (t != null)
                        _extStressFloor = t.GetField("ExternalStressFloor",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    break;
                }
            }
            catch { }
            if (_extStressFloor == null)
            {
                Plugin.L.LogWarning("ImmersiveEffects State.ExternalStressFloor not found — bleed/pain suppression bridge disabled.");
                return null;
            }

            // Compile `value => ImmersiveEffects.State.ExternalStressFloor = value` once.
            try
            {
                var p = System.Linq.Expressions.Expression.Parameter(typeof(float), "v");
                var body = System.Linq.Expressions.Expression.Assign(
                    System.Linq.Expressions.Expression.Field(null, _extStressFloor), p);
                _extStressSet = System.Linq.Expressions.Expression.Lambda<Action<float>>(body, p).Compile();
                Plugin.L.LogInfo("ImmersiveEffects suppression bridge connected.");
            }
            catch (Exception e)
            {
                // Fall back to plain reflection rather than losing the feature outright.
                var fi = _extStressFloor;
                _extStressSet = v => { try { fi.SetValue(null, v); } catch { } };
                Plugin.L.LogWarning("ImmersiveEffects bridge: compiled setter failed, using reflection (" + e.Message + ")");
            }
            return _extStressSet;
        }
    }

    /// <summary>Resolves on-disk locations for asset loading.</summary>
    internal static class Paths
    {
        private static string _pluginDir;

        /// <summary>Root BepInEx/plugins directory.</summary>
        public static string PluginPath()
        {
            try { return BepInEx.Paths.PluginPath; } catch { return null; }
        }

        /// <summary>This plugin's own folder (…/plugins/ER2AdvancedMedic).</summary>
        public static string PluginDir()
        {
            if (_pluginDir != null) return _pluginDir;
            try
            {
                var root = PluginPath();
                if (root != null)
                {
                    var sub = Path.Combine(root, "ER2AdvancedMedic");
                    if (!Directory.Exists(sub)) sub = Path.Combine(root, "AdvancedMedic");   // folder name before the ER2 rename
                    _pluginDir = Directory.Exists(sub) ? sub : root;
                }
            }
            catch { }
            return _pluginDir;
        }
    }
}
