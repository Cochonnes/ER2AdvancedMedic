using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

namespace AdvancedMedic
{
    /// <summary>
    /// First-cut multiplayer sync of the wound model over Photon. When a soldier's wounds change on
    /// one client (a hit or a heal), that client broadcasts a compact snapshot of the soldier's
    /// ACTIVE wounds, keyed by PhotonView.ViewID; every other client rebuilds that soldier's wound
    /// set to match. Snapshots are idempotent, so it doesn't matter which client processed the hit
    /// and heals converge (two player-medics treating one downed AI end at the same state).
    ///
    /// Everything is gated behind PhotonNetwork.InRoom, so single-player is completely untouched.
    /// Syncs WOUNDS, tourniquets, the vanilla bleeding flag, cardiac arrest and the downed bit.
    /// Vitals (blood/pulse/BP) are not synced; each machine simulates them for its UI.
    ///
    /// AUTHORITY: wound data converges from anyone (a medic on any client can treat any soldier), but
    /// the DOWNED bit is only ever taken from the soldier's authority (Interop.HasAuthority: the host
    /// for AI, the owner for a player) and only applied by machines that are NOT the authority. A
    /// directly called SetIncapacitated isn't networked by the game, so a client that applied a stale
    /// downed bit - or anyone's view but the authority's - left its copy of a host-run AI lying in the
    /// downed animation while the host ran it around shooting, with nothing to ever correct it.
    /// A cardiac arrest can likewise only START from the authority; ending one (CPR) comes from anyone.
    ///
    /// BATCHED: Send() only marks a soldier dirty; Tick() flushes one snapshot per dirty soldier every
    /// FlushEvery seconds. A burst of hits (or a hit plus its knock-out plus its treatment) used to be a
    /// reliable RaiseEvent - and a fresh buffer - each.
    ///
    /// FRESH WOUNDS WIN: snapshots are whole-set replacements, so one built before a hit landed here
    /// (another machine's older view) erased the new wound. A wound this machine made or learned in the
    /// last FreshKeepSeconds that a snapshot doesn't mention is kept and re-broadcast instead.
    ///
    /// HELLO: a host running the mod says so explicitly (MSG_HELLO, on a timer and whenever a client
    /// asks with MSG_HELLO_REQ on joining), so a client knows at once whether the host sends AI wound
    /// data - it used to guess from "a snapshot arrived from the master in the last 600 s".
    /// Protocol 3: the hello also carries the host's DAMAGE RULES (DamageRules), which a client applies to
    /// the hits it computes instead of its own config (older receivers read only the first two ints).
    ///
    /// TREAT (protocol 3): the drugs and fluids a medic gives a soldier it doesn't own (syringe =
    /// epinephrine, aspirin, blood IV) live only in the vitals model, which the snapshot doesn't carry.
    /// They go to the soldier's authority, which applies them to ITS record - that is what the host's
    /// wake-up roll for a downed AI reads, so a client medic's syringe really helps the patient up.
    ///
    /// INFECTION (protocol 4): each wound's infection level (0..15) rides in spare bits of its packed int.
    /// Only the soldier's authority grows it (Vitals.TickWoundTimers) and it broadcasts each level step;
    /// everyone else takes the level from the snapshot. Stitching closes the wound, which removes it.
    /// </summary>
    internal static class MpSync
    {
        private const byte EVENT = 177;         // custom Photon event code (user range is 0-199)
        private const int MSG_SNAPSHOT = 1;
        private const int MSG_HELLO = 2;        // [MSG_HELLO, Protocol]: "I host this room and run the mod"
        private const int MSG_HELLO_REQ = 3;    // [MSG_HELLO_REQ, Protocol]: a client asking the host to say hello
        private const int MSG_TREAT = 4;        // [MSG_TREAT, Protocol, viewId, TreatKind]: a drug/fluid for the soldier's authority
        // 3: hello = [MSG_HELLO, 3, n, damage rules x n] + MSG_TREAT.
        // 4: a wound's infection level rides in bits 12-15 of its packed int (unused before). Older
        //    receivers mask those bits away and older senders leave them 0 (= not infected), so the
        //    snapshot layout and every older build stay compatible; receivers only compare Protocol >= 3.
        private const int Protocol = 4;

        // Packed-wound bit layout (see SendNow). Infection is quantised to 0..InfLevels.
        private const int InfShift = 12, InfMask = 0xF;
        private const float InfLevels = 15f;

        /// <summary>A wound's infection as the 4-bit level the snapshot carries (0..15).</summary>
        public static int InfectionLevel(float infection) => Mathf.Clamp(Mathf.RoundToInt(infection * InfLevels), 0, InfMask);

        /// <summary>What a MSG_TREAT carries (only the vitals-model treatments the snapshot can't).</summary>
        public enum TreatKind { Epinephrine = 1, Aspirin = 2, BloodIV = 3 }

        private const float FlushEvery = 0.25f;       // batched snapshot cadence
        private const float HelloEvery = 30f;         // host re-announces (late joiners also ask)
        private const float FreshKeepSeconds = 2f;    // a wound younger than this survives a snapshot that lacks it

        private static bool _applyingRemote;    // guard so applying a received snapshot doesn't re-send

        // Actor number of the master client that said hello (or sent us a snapshot) - i.e. the host runs
        // this mod and is the authority for AI wounds. Clients then don't infer AI wounds themselves.
        private static int _helloActor = -1;
        private static float _nextHello;
        private static bool _wasMaster;
        private static int _askedMaster = -1;   // the master we last sent a HELLO_REQ to

        private static int _hswFrame = -1;
        private static bool _hsw;

        // The damage rules the current host published in its hello (protocol 3+), and who sent them.
        private static DamageRules _hostRules;
        private static int _hostRulesActor = -1;

        /// <summary>The host's damage rules, while a modded host is known and has published them; null
        /// otherwise (the caller then stays vanilla - see DamageRules.For).</summary>
        public static DamageRules HostRules
            => HostSendsWounds && _hostRules != null && _hostRulesActor == _helloActor ? _hostRules : null;

        /// <summary>A REMOTE host runs this mod: we're a client in a room whose current master client has
        /// said hello (or sent a snapshot). False in single player and on the host itself.</summary>
        public static bool HostSendsWounds
        {
            get
            {
                int f = Time.frameCount;
                if (f != _hswFrame) { _hswFrame = f; _hsw = ComputeHostSendsWounds(); }
                return _hsw;
            }
        }

        private static bool ComputeHostSendsWounds()
        {
            try
            {
                if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) return false;
                var room = PhotonNetwork.CurrentRoom;
                return room != null && _helloActor > 0 && _helloActor == room.MasterClientId;
            }
            catch { return false; }
        }

        /// <summary>Forget the previous match's host (session flush). The Photon subscription itself is
        /// kept: Tick re-checks it every frame.</summary>
        public static void ResetSession()
        {
            _helloActor = -1; _askedMaster = -1; _nextHello = 0f; _wasMaster = false; _hswFrame = -1;
            _hostRules = null; _hostRulesActor = -1;
            DamageRules.ResetLog();
            ClearPending();
        }

        // ---- batched sends ----
        private struct Pending { public Soldier S; public bool Cpr; }
        private static readonly Dictionary<IntPtr, Pending> _dirty = new Dictionary<IntPtr, Pending>();
        private static readonly List<Pending> _flushList = new List<Pending>();
        private static float _nextFlush;

        /// <summary>Drop every queued snapshot (scene flush: the soldiers are about to be freed).</summary>
        public static void ClearPending() { _dirty.Clear(); _flushList.Clear(); }

        /// <summary>A soldier is gone: forget its queued snapshot.</summary>
        public static void Prune(IntPtr p) { if (p != IntPtr.Zero) _dirty.Remove(p); }

        // ---- Photon event subscription --------------------------------------------------------
        // CRASH HAZARD, learned from a user crash report: handing a managed delegate to native code
        // via DelegateSupport.ConvertDelegate requires keeping BOTH halves rooted for the life of the
        // process. ConvertDelegate wraps the managed delegate in an injected
        // Il2CppToMonoDelegateReference; rooting only the returned Il2CppSystem.Action keeps the
        // DELEGATE alive but not that TARGET object. Once the GC takes the target, the next native
        // invocation resolves a dead pointer and the process dies instantly with
        //   AccessViolationException at ClassInjectorBase.GetMonoObjectFromIl2CppPointer
        // inside the il2cpp delegate trampoline - no managed stack from our code, and no warning.
        // It only bites in multiplayer, and only after a GC happens to collect it, which is why it
        // presents as "crashes after a while". The icon loaders in this codebase get this right
        // (they root both); this one used to root only the converted half.
        //
        // The pair is created ONCE and reused for every room, so re-subscribing cannot grow _roots.
        // _roots is never cleared, even on unsubscribe - an in-flight native call can still hold it.
        private static readonly List<object> _roots = new List<object>();
        private static Action<EventData> _managed;
        private static Il2CppSystem.Action<EventData> _handler;

        private static bool _subscribed;
        private static LoadBalancingClient _client;   // the client we attached to, for the matching remove
        private static IntPtr _clientPtr;             // native identity: wrapper identity is unreliable

        /// <summary>In a Photon room (frame-cached in Interop: it is asked per tracked soldier per vitals step).</summary>
        public static bool Active => Interop.InMpRoom();

        /// <summary>Build the handler pair once and root both halves permanently.</summary>
        private static void EnsureHandler()
        {
            if (_handler != null) return;
            _managed = new Action<EventData>(OnEvent);
            _handler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<EventData>>(_managed);
            _roots.Add(_managed);
            _roots.Add(_handler);
        }

        /// <summary>Called each in-mission tick: keep the Photon subscription in step with the room.
        /// Subscribes on entering a room, drops on leaving one, and re-attaches if PUN swaps the
        /// client out from under us.</summary>
        public static void Tick()
        {
            try
            {
                if (!Active) { Drop(); return; }

                var client = PhotonNetwork.NetworkingClient;
                if (client == null) { Drop(); return; }

                if (!_subscribed || client.Pointer != _clientPtr)
                {
                    if (_subscribed) Drop();   // different client instance - re-attach to the new one

                    EnsureHandler();
                    client.add_EventReceived(_handler);
                    _client = client;
                    _clientPtr = client.Pointer;
                    _subscribed = true;
                    Plugin.L.LogInfo("[mp] wound-sync subscribed to Photon events");
                }

                TickHello();
                FlushDirty();
            }
            catch (Exception e) { Guard.Once("mp tick", e); }
        }

        /// <summary>The host announces itself; a client asks the (current) host once per master.</summary>
        private static void TickHello()
        {
            bool master = PhotonNetwork.IsMasterClient;
            if (master != _wasMaster) { _wasMaster = master; if (master) _nextHello = 0f; }   // incl. host migration
            float now = Time.unscaledTime;
            if (master)
            {
                if (now >= _nextHello)
                {
                    _nextHello = now + HelloEvery;
                    SendControl(MSG_HELLO, ReceiverGroup.Others);
                }
                return;
            }
            var room = PhotonNetwork.CurrentRoom;
            int mid = room != null ? room.MasterClientId : -1;
            if (mid > 0 && mid != _askedMaster)
            {
                _askedMaster = mid;
                SendControl(MSG_HELLO_REQ, ReceiverGroup.MasterClient);
            }
        }

        private static readonly List<int> _ctl = new List<int>();

        private static void SendControl(int msg, ReceiverGroup to)
        {
            try
            {
                var l = _ctl;
                l.Clear();
                l.Add(msg); l.Add(Protocol);
                // The host's hello publishes its damage rules (the clients apply them to the hits they
                // compute). A pre-3 receiver only reads [0] and [1].
                if (msg == MSG_HELLO) DamageRules.Local.WriteTo(l);
                var arr = new Il2CppStructArray<int>(l.Count);
                for (int i = 0; i < l.Count; i++) arr[i] = l[i];
                l.Clear();
                PhotonNetwork.RaiseEvent(EVENT, arr.Cast<Il2CppSystem.Object>(), new RaiseEventOptions { Receivers = to }, SendOptions.SendReliable);
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] sent {(msg == MSG_HELLO ? "hello + damage rules" : "hello request")}");
            }
            catch (Exception e) { Guard.Once("mp control send", e); }
        }

        /// <summary>A medic here gave a soldier this machine doesn't own a drug/fluid that only lives in the
        /// vitals model: tell its authority, which applies it to its own record (the one its wake-up roll
        /// and death checks read). No-op in single player and for our own soldiers.</summary>
        public static void SendTreat(Soldier s, TreatKind kind)
        {
            if (s == null || !Active || Interop.HasAuthority(s)) return;
            try
            {
                int id = ViewId(s);
                if (id == 0) return;
                var arr = new Il2CppStructArray<int>(4);
                arr[0] = MSG_TREAT; arr[1] = Protocol; arr[2] = id; arr[3] = (int)kind;
                // To everyone: only the soldier's authority acts on it (the host for AI, the owner for a player).
                PhotonNetwork.RaiseEvent(EVENT, arr.Cast<Il2CppSystem.Object>(), new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] sent treat {kind} view={id} to its authority");
            }
            catch (Exception e) { Guard.Once("mp treat send", e); }
        }

        private static void ApplyTreat(int viewId, int kind, int sender)
        {
            var s = FromViewId(viewId);
            if (s == null || Interop.IsDead(s)) return;
            if (!Interop.HasAuthority(s)) return;   // not ours: its authority applies it
            var w = WoundState.Get(s);
            if (w == null) return;
            Vitals.Init(w);
            switch ((TreatKind)kind)
            {
                case TreatKind.Epinephrine: Meds.Give(w, MedKind.Epinephrine); break;
                case TreatKind.Aspirin:     Pain.ApplyAspirin(w); break;
                case TreatKind.BloodIV:     Vitals.StartIv(w); break;
                default: return;
            }
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] applied treat {(TreatKind)kind} from actor {sender} to view={viewId} (authority)");
        }

        /// <summary>Send one snapshot per soldier marked dirty since the last flush.</summary>
        private static void FlushDirty()
        {
            if (_dirty.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextFlush) return;
            _nextFlush = now + FlushEvery;

            _flushList.Clear();
            foreach (var kv in _dirty) _flushList.Add(kv.Value);
            _dirty.Clear();
            for (int i = 0; i < _flushList.Count; i++)
            {
                var p = _flushList[i];
                if (p.S == null) continue;   // destroyed since it was queued (Unity null)
                SendNow(p.S, p.Cpr);
            }
            _flushList.Clear();
        }

        /// <summary>Detach from the Photon client. Idempotent and safe to call every frame. The
        /// handler itself stays rooted - only the subscription goes.</summary>
        public static void Drop()
        {
            if (!_subscribed) return;
            _subscribed = false;                       // cleared first, so a throw below cannot wedge us
            var client = _client;
            _client = null;
            _clientPtr = IntPtr.Zero;
            try { if (client != null && _handler != null) client.remove_EventReceived(_handler); }
            catch (Exception e) { Guard.Once("mp unsubscribe", e); }
        }

        /// <summary>Queue a soldier's current active-wound set for the other clients (sent on the next
        /// flush, at most FlushEvery later - the state it sends is the one at flush time). cprRestart marks
        /// the snapshot sent right after a successful CPR, so the arrest ends everywhere even when this
        /// machine isn't the soldier's authority (a plain "not arrested" from a non-authority may just be
        /// a stale view and is ignored).</summary>
        ///
        /// immediate: send right now instead (a player's treatment - the change someone is waiting to see,
        /// and the one a crossing snapshot from another machine would otherwise overwrite during the wait).
        public static void Send(Soldier s, bool cprRestart = false, bool immediate = false)
        {
            if (_applyingRemote || s == null || !Active) return;
            var p = Interop.PtrOf(s);
            if (p == IntPtr.Zero) return;
            if (_dirty.TryGetValue(p, out var old)) { cprRestart |= old.Cpr; if (immediate) _dirty.Remove(p); }   // never lose a CPR restart
            if (immediate) { SendNow(s, cprRestart); return; }
            _dirty[p] = new Pending { S = s, Cpr = cprRestart };
        }

        private static readonly List<int> _flat = new List<int>();

        private static void SendNow(Soldier s, bool cprRestart)
        {
            try
            {
                int id = ViewId(s);
                if (id == 0) return;
                var w = WoundState.Peek(s);
                if (w == null) return;

                // 2 ints per active wound: (part, packed). packed = type(4) | bandaged | stabilised |
                // size(2) << 6 | kind(4) << 8 | infection(4) << 12 | bleed rate x10000 << 16.
                var flat = _flat;
                flat.Clear();
                foreach (var kv in w.Parts)
                    foreach (var wd in kv.Value)
                        if (wd.Active)
                        {
                            int packed = ((int)wd.Type & 0xF) | (wd.Bandaged ? 0x10 : 0) | (wd.Stabilised ? 0x20 : 0)
                                       | ((wd.Size & 0x3) << 6) | (((int)wd.Kind & 0xF) << 8)
                                       | (InfectionLevel(wd.Infection) << InfShift)
                                       | (Mathf.Clamp(Mathf.RoundToInt(wd.BleedRate * 10000f), 0, 0x7FFF) << 16);
                            flat.Add((int)kv.Key);
                            flat.Add(packed);
                        }

                int n = flat.Count / 2;
                int downed = Interop.IsDowned(s) ? 1 : 0;
                // Tourniquets ride in the upper bits of the downed slot: bit (8 + part).
                foreach (var tq in w.Tourniquets.Keys) downed |= 1 << (8 + ((int)tq & 0xF));
                if (w.Arrested) downed |= 2;   // cardiac arrest, so CPR works from any client
                if (cprRestart) downed |= 4;   // this snapshot reports a successful CPR
                var arr = new Il2CppStructArray<int>(4 + flat.Count);
                arr[0] = MSG_SNAPSHOT; arr[1] = id; arr[2] = downed; arr[3] = n;
                for (int i = 0; i < flat.Count; i++) arr[4 + i] = flat[i];
                flat.Clear();

                var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
                PhotonNetwork.RaiseEvent(EVENT, arr.Cast<Il2CppSystem.Object>(), opts, SendOptions.SendReliable);

                if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] sent snapshot view={id} wounds={n}");
            }
            catch (Exception e) { Guard.Once("mp snapshot send", e); }
        }

        private static void OnEvent(EventData e)
        {
            try
            {
                if (e.Code != EVENT) return;
                var arr = e.CustomData?.TryCast<Il2CppStructArray<int>>();
                if (arr == null || arr.Length < 2) return;
                int msg = arr[0];
                int sender = e.Sender;
                bool fromMaster = false;
                try { fromMaster = PhotonNetwork.CurrentRoom != null && sender == PhotonNetwork.CurrentRoom.MasterClientId; } catch { }

                if (msg == MSG_HELLO)
                {
                    if (fromMaster)
                    {
                        _helloActor = sender; _hswFrame = -1;
                        // Protocol 3+: the host's damage rules follow. An older host publishes none, and the
                        // client then stays vanilla for damage (DamageRules.For).
                        var r = arr[1] >= 3 ? DamageRules.Read(arr, 2, "host") : null;
                        if (r != null) { _hostRules = r; _hostRulesActor = sender; }
                        if (Plugin.C.DebugLogging.Value)
                            Plugin.L.LogInfo($"[mp] hello from host actor {sender} (protocol {arr[1]}) rules={(r != null ? r.ToString() : "none")}");
                    }
                    else if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[mp] hello from non-master actor {sender} ignored (protocol {arr[1]})");
                    return;
                }
                if (msg == MSG_TREAT)
                {
                    if (arr.Length >= 4) ApplyTreat(arr[2], arr[3], sender);
                    return;
                }
                if (msg == MSG_HELLO_REQ)
                {
                    bool master = false; try { master = PhotonNetwork.IsMasterClient; } catch { }
                    if (master) _nextHello = 0f;   // answer on the next tick (broadcast: cheap, and covers everyone)
                    return;
                }
                if (msg != MSG_SNAPSHOT || arr.Length < 4) return;
                if (fromMaster) { _helloActor = sender; _hswFrame = -1; }   // a snapshot from the host proves it too
                ApplySnapshot(arr[1], arr[2], arr[3], arr, sender);
            }
            catch (Exception ex) { Guard.Once("mp receive", ex); }
        }

        // Previous wounds per part while a snapshot rebuilds them, so a wound we already knew keeps its
        // own timers (reopen / self-heal) instead of restarting them on every snapshot. The per-part
        // lists are reused across snapshots (cleared, never reallocated), like the tourniquet scratch.
        private static readonly Dictionary<BodyPartType, List<Wound>> _prev = new Dictionary<BodyPartType, List<Wound>>();
        private static readonly Dictionary<BodyPartType, float> _tqKeep = new Dictionary<BodyPartType, float>();

        private static void ApplySnapshot(int viewId, int downed, int n, Il2CppStructArray<int> arr, int sender)
        {
            var s = FromViewId(viewId);
            if (s == null) return;
            var w = WoundState.Get(s);
            if (w == null) return;

            // Who may change what (see the class comment).
            bool mine = Interop.HasAuthority(s);
            bool fromAuthority = sender > 0 && sender == Interop.AuthorityActor(s);

            int keptFresh = 0, added = 0;
            _applyingRemote = true;
            try
            {
                float now = Time.time;
                RebuildWounds(w, arr, n, Vitals.RunsWoundClocks(s, mine), now, out added, out keptFresh);
                ApplyTourniquets(w, downed, now);
                ApplyArrest(w, downed, mine, fromAuthority);
                Interop.SetBleeding(s, w.AnyBleeding());
                MirrorDowned(s, (downed & 1) != 0, mine, fromAuthority);
                w.InvalidateMove();
            }
            finally { _applyingRemote = false; _tqKeep.Clear(); }

            // A wound the sender made on OUR soldier that we didn't have: the hit was resolved (and wounded)
            // on the shooter's machine. Stamp it, so the HP watchdog and the grenade proximity check don't
            // add a second wound / a second insta-kill roll for the same hit.
            if (added > 0 && Interop.IsPlayer(s))
            {
                WoundLogic.LastPlayerWoundTime = Time.time;
                WoundLogic.LastRemotePlayerWoundTime = Time.time;
            }

            // We knew more than the sender: tell everyone, so the whole room converges on the full set.
            if (keptFresh > 0) Send(s);

            if (Plugin.C.DebugLogging.Value)
                Plugin.L.LogInfo($"[mp] applied snapshot view={viewId} from={sender} authority={fromAuthority} mine={mine} downed={downed & 1} wounds={n} new={added} keptFresh={keptFresh}");
        }

        /// <summary>Replace the soldier's active wound set with the snapshot's, keeping the old wounds aside
        /// so a wound we already knew keeps its timers (CarryTimers), and keeping any wound made or learned
        /// here in the last FreshKeepSeconds that the (older) snapshot doesn't mention. ownClocks: this
        /// machine runs the soldier's wound clocks (Vitals.RunsWoundClocks, asked once per snapshot).</summary>
        private static void RebuildWounds(SoldierWounds w, Il2CppStructArray<int> arr, int n, bool ownClocks, float now,
                                          out int added, out int keptFresh)
        {
            added = 0; keptFresh = 0;
            foreach (var l in _prev.Values) l.Clear();
            foreach (var kv in w.Parts)
            {
                if (!_prev.TryGetValue(kv.Key, out var l)) { l = new List<Wound>(); _prev[kv.Key] = l; }
                l.AddRange(kv.Value);
                kv.Value.Clear();
            }
            var c = Plugin.C;

            int idx = 4;
            for (int i = 0; i < n && idx + 1 < arr.Length; i++)
            {
                int part = arr[idx++]; int packed = arr[idx++];
                var bp = (BodyPartType)part;
                var nw = new Wound
                {
                    Type = (WoundType)(packed & 0xF),
                    Bandaged = (packed & 0x10) != 0,
                    Stabilised = (packed & 0x20) != 0,
                    Size = (packed >> 6) & 0x3,
                    Kind = (WoundKind)((packed >> 8) & 0xF),
                    Infection = ((packed >> InfShift) & InfMask) / InfLevels,
                    BleedRate = ((packed >> 16) & 0x7FFF) / 10000f,
                };
                if (!CarryTimers(nw, bp, ownClocks, now, c)) added++;
                w.Of(bp).Add(nw);
            }

            // Whatever of the old set the snapshot didn't match: a wound made (or learned) here in the
            // last FreshKeepSeconds is newer than the sender's view, not healed by it - keep it.
            foreach (var kv in _prev)
            {
                foreach (var o in kv.Value)
                    if (o.Active && now - o.Born < FreshKeepSeconds) { w.Of(kv.Key).Add(o); keptFresh++; }
                kv.Value.Clear();
            }
        }

        /// <summary>Tourniquets ride in the upper bits of the downed slot (bit 8 + part). One we already knew
        /// about keeps its original apply time (its pain clock).</summary>
        private static void ApplyTourniquets(SoldierWounds w, int downed, float now)
        {
            var keep = _tqKeep;
            keep.Clear();
            foreach (var kv in w.Tourniquets) keep[kv.Key] = kv.Value;
            w.Tourniquets.Clear();
            for (int b = 0; b < 16; b++)
                if ((downed & (1 << (8 + b))) != 0)
                {
                    var tp = (BodyPartType)b;
                    w.Tourniquets[tp] = keep.TryGetValue(tp, out var t0) ? t0 : now;
                }
        }

        /// <summary>An arrest starts only where the authority decided it (a stale view from another client
        /// must not stop a heart the host just restarted); it ends on the authority's word or on a snapshot
        /// flagged as a successful CPR done on any client.</summary>
        private static void ApplyArrest(SoldierWounds w, int downed, bool mine, bool fromAuthority)
        {
            bool arrested = (downed & 2) != 0;
            if (arrested && !w.Arrested && !mine && fromAuthority)
            {
                Vitals.Init(w);
                w.Arrested = true; w.Pulse = 0f; w.State = MedState.CardiacArrest;
                if (w.ArrestTimeLeft <= 0f) w.ArrestTimeLeft = Mathf.Max(1f, Plugin.C.ArrestTime.Value);
            }
            else if (!arrested && w.Arrested && (fromAuthority || (downed & 4) != 0))
                Vitals.CprSuccess(w);
        }

        /// <summary>Mirror the downed/standing state, but ONLY the authority's, and only on a machine that
        /// isn't the authority. The authority decides for itself; anyone else's bit is their local view,
        /// possibly stale. Our KnockOut/Wake call SetIncapacitated directly, which the game doesn't
        /// network, so the non-authority copies need this to show them.</summary>
        private static void MirrorDowned(Soldier s, bool downed, bool mine, bool fromAuthority)
        {
            if (mine || !fromAuthority) return;
            bool cur = Interop.IsDowned(s);
            if (downed && !cur) Interop.SetIncapacitated(s, true);
            else if (!downed && cur) { try { Vitals.Wake(s); } catch (Exception e) { Guard.Once("mp mirror wake", e); } }
        }

        /// <summary>Give a rebuilt wound its timers: those of the matching wound we already had (same part,
        /// type, kind and size - and the same dressing state first), else fresh ones. Only a machine that
        /// runs the soldier's wound clocks (Vitals.RunsWoundClocks: its authority, or anyone for AI on an
        /// unmodded host) starts a NEW reopen / self-heal clock - the authority broadcasts the change when
        /// it fires, so the other machines follow instead of running a divergent clock of their own. The
        /// same goes for infection: a clock-runner keeps the higher of its own level and the snapshot's (an
        /// older build sends 0), everyone else takes the snapshot's.</summary>
        /// Returns whether it matched a wound we already had (false = new to this machine).
        private static bool CarryTimers(Wound nw, BodyPartType part, bool ownClocks, float now, Cfg c)
        {
            bool matched = false;
            if (_prev.TryGetValue(part, out var old))
            {
                // Two wounds alike but for the dressing (one bandaged, one not): match the one in the same
                // state first. The first look-alike used to win, so the dressed wound could take the open
                // one's (absent) reopen clock and infection, and the open one the dressed one's.
                int pick = -1;
                for (int i = 0; i < old.Count; i++)
                {
                    var o = old[i];
                    if (o.Type != nw.Type || o.Kind != nw.Kind || o.Size != nw.Size) continue;
                    if (o.Bandaged == nw.Bandaged && o.Stabilised == nw.Stabilised) { pick = i; break; }
                    if (pick < 0) pick = i;   // a look-alike in another state: only if nothing better turns up
                }
                if (pick >= 0)
                {
                    var o = old[pick];
                    old.RemoveAt(pick);
                    nw.Damage = o.Damage;
                    nw.Born = o.Born;
                    if (o.Bandaged == nw.Bandaged) nw.ReopenAt = o.ReopenAt;
                    if (o.Stabilised == nw.Stabilised) nw.HealAt = o.HealAt;
                    if (ownClocks) nw.Infection = Mathf.Max(nw.Infection, o.Infection);
                    matched = true;
                }
            }
            if (nw.Born <= 0f) nw.Born = now;   // first heard of here

            if (!ownClocks) return matched;
            if (nw.Bandaged && nw.ReopenAt <= 0f && nw.Type == WoundType.Bleeding)
                nw.ReopenAt = now + UnityEngine.Random.Range(c.BandageReopenMin.Value, c.BandageReopenMax.Value);
            if (nw.HealAt <= 0f)
            {
                if (nw.Type == WoundType.Bruise) nw.HealAt = now + Mathf.Max(1f, c.BruiseHealTime.Value);
                else if (nw.Type == WoundType.Fracture && nw.Stabilised && c.FractureHealTime.Value > 0f)
                    nw.HealAt = now + c.FractureHealTime.Value;
            }
            return matched;
        }

        // ---- PhotonView identity ----
        private static int ViewId(Soldier s)
        {
            try { var pv = Interop.ViewOf(s); if (pv != null) return pv.ViewID; } catch { }
            return 0;
        }

        private static Soldier FromViewId(int id)
        {
            try
            {
                var pv = PhotonView.Find(id);
                if (pv == null) return null;
                var go = pv.gameObject;
                if (go == null) return null;
                var s = go.GetComponent<Soldier>();
                if (s == null) s = go.GetComponentInParent<Soldier>();
                if (s == null) s = go.GetComponentInChildren<Soldier>();
                return s;
            }
            catch { return null; }
        }
    }
}
