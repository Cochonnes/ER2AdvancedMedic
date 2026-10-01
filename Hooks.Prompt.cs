using System;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// The "[B]" that shows DURING the bandage animation is a hold-key indicator drawn onto
    /// InteractionGUI2's shared inputDisplayer (NOT an interaction — it never comes through
    /// SetInteraction). Hide it only while OUR heal animation runs (Procedure/MedicCall active — a
    /// tight window that ends by itself), by pinning a CanvasGroup on it to alpha 0, and restore the
    /// alpha the instant the heal ends. This never deactivates the shared object, so every other prompt
    /// (Mount, Select, …) is untouched. Called each frame from Effects.Tick.
    /// </summary>
    public static partial class Hooks
    {
        private static bool _idpHidden;
        private static float _idpNextScan;
        internal static float BandageClipUntil;   // set on local-player UseBandages: covers a clip that outlasts Procedure
        // CanvasGroups on the "bleeding-out" prompt(s) we pin transparent, keyed by GameObject id. Kept for
        // the scene: it is the same shared object every time, so it is searched for once, not every frame.
        private static readonly System.Collections.Generic.Dictionary<int, CanvasGroup> _idpGroups
            = new System.Collections.Generic.Dictionary<int, CanvasGroup>();
        private static readonly System.Collections.Generic.List<int> _idpDead = new System.Collections.Generic.List<int>();

        // A scene-wide FindObjectsOfType is not cheap. While the prompt isn't found, each miss doubles the
        // wait before the next search (0.1 s → … → 10 s), so a build or a HUD mod that has no such prompt
        // stops paying for the search almost at once. A hit, or the next scene, resets it.
        private const float ScanFirst = 0.1f, ScanMax = 10f;
        private static float _idpScanGap = ScanFirst;

        // Cleared on scene flush WITHOUT touching the objects (they may be freed) — see Effects.FlushSession.
        public static void ClearPromptHideState()
        {
            _idpGroups.Clear(); _idpHidden = false; _idpNextScan = 0f; _idpScanGap = ScanFirst;
        }

        public static void HideHealHoldPrompt()
        {
            try
            {
                bool hide = Procedure.Active || MedicCall.Active || Time.time < BandageClipUntil;
                if (hide)
                {
                    // Search when a hide window opens, and again (backing off) only while no prompt is
                    // known yet.
                    if (!_idpHidden) DropDeadPromptGroups();
                    if ((!_idpHidden || _idpGroups.Count == 0) && Time.unscaledTime >= _idpNextScan)
                    {
                        ScanPromptGroups();
                        if (_idpGroups.Count > 0) _idpScanGap = ScanFirst;
                        else _idpScanGap = Mathf.Min(ScanMax, _idpScanGap * 2f);
                        _idpNextScan = Time.unscaledTime + _idpScanGap;
                    }
                    foreach (var kv in _idpGroups) { var cg = kv.Value; try { if (cg != null) cg.alpha = 0f; } catch { } }
                    _idpHidden = true;
                }
                else if (_idpHidden)
                {
                    foreach (var kv in _idpGroups) { var cg = kv.Value; try { if (cg != null) cg.alpha = 1f; } catch { } }
                    _idpHidden = false;   // groups kept: the same object shows up on the next bandage
                }
            }
            catch (Exception e) { Fail("HideHealHoldPrompt", e); }
        }

        private static void ScanPromptGroups()
        {
            // The "[B]" during a bandage is the 'UI_Binding_horizontal_bleeding_out' InputDisplayer.
            // Target ONLY bleeding-out binding displays (never the interact/vehicle ones).
            var all = UnityEngine.Object.FindObjectsOfType<InputDisplayer>();
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
            {
                var idp = all[i]; if (idp == null) continue;
                var go = idp.gameObject;
                if (go.name.IndexOf("bleed", StringComparison.OrdinalIgnoreCase) < 0) continue;   // bleeding-out prompt only
                int id = go.GetInstanceID();
                if (_idpGroups.ContainsKey(id)) continue;
                CanvasGroup cg = null;
                try { cg = go.GetComponent<CanvasGroup>(); } catch { }
                if (cg == null) { try { cg = go.AddComponent<CanvasGroup>(); } catch { } }
                if (cg != null) _idpGroups[id] = cg;
            }
        }

        private static void DropDeadPromptGroups()
        {
            if (_idpGroups.Count == 0) return;
            _idpDead.Clear();
            foreach (var kv in _idpGroups) if (kv.Value == null) _idpDead.Add(kv.Key);   // Unity-destroyed
            for (int i = 0; i < _idpDead.Count; i++) _idpGroups.Remove(_idpDead[i]);
        }
    }
}
