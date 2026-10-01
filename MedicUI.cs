using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdvancedMedic
{
    public enum ActionKind { Bandage, Forceps, Scissors, Splint, Syringe, Aspirin, Diagnose, CPR, Stitch, BloodIV, Tourniquet }

    /// <summary>
    /// The medical overlay: the body composite (background + per-limb masks), an
    /// alpha-accurate limb hit-test, a per-part wound list, and the treatment card. Called from the
    /// OnGUI hook on every event so the hand-rolled MouseDown hit-tests work; every fill/label draws
    /// on Repaint only (Gui helpers), and the per-event work that isn't drawing - the treatment rows
    /// (inventory walks, the medic test) and the patient's name (Photon / Lua) - is computed once per
    /// frame / once per patient and reused by the other passes.
    /// </summary>
    internal static class MedicUI
    {
        public static bool Open { get; private set; }
        public static bool Hidden;   // open (input still blocked) but panel not drawn — during a procedure
        public static Soldier Patient;
        public static BodyPartType Selected = BodyPartType.chest;

        private static string _status;
        private static float _statusUntil;
        private const float StatusSeconds = 3.5f;
        public static void SetStatus(string msg) { _status = msg; _statusUntil = Time.time + StatusSeconds; }

        // Layer: which mask maps to which body part, plus rough fallback rect (normalized, GUI
        // y-down) used only if a mask texture isn't readable for alpha sampling. The mask names (base,
        // "_s" selected, "_t" tourniquet) are built once here, not per draw.
        private struct Layer { public BodyPartType Part; public string Mask; public string Sel; public string Tq; public Rect Fallback; }

        // Fallback rects are the actual mask alpha bounding boxes (measured), used only if a mask
        // texture isn't readable for per-pixel sampling. Note the game's arm_l/leg_l masks sit on
        // the VIEWER'S RIGHT (anatomically the figure's left), so display + click stay consistent.
        private static readonly Layer[] Layers =
        {
            L(BodyPartType.head,  new Rect(0.45f, 0.06f, 0.10f, 0.15f)),
            L(BodyPartType.arm_l, new Rect(0.56f, 0.19f, 0.12f, 0.37f)),
            L(BodyPartType.arm_r, new Rect(0.32f, 0.19f, 0.13f, 0.37f)),
            L(BodyPartType.leg_l, new Rect(0.50f, 0.44f, 0.11f, 0.50f)),
            L(BodyPartType.leg_r, new Rect(0.39f, 0.43f, 0.11f, 0.51f)),
            L(BodyPartType.chest, new Rect(0.42f, 0.19f, 0.16f, 0.31f)),
        };

        // Mask names come from the one shared table (Body.Masks), which the hit peek draws from too.
        private static Layer L(BodyPartType p, Rect fallback)
        {
            string m = Body.Mask(p);
            return new Layer { Part = p, Mask = m, Sel = m + "_s", Tq = m + "_t", Fallback = fallback };
        }

        public static void Toggle()
        {
            Open = !Open;
            ClearCaches();
            if (Open)
            {
                Selected = BodyPartType.chest;
                Patient = Targeting.PickPatient();
                if (Targeting.LastReject != null) SetStatus("Treating yourself: " + Targeting.LastReject);
                Items.LogCarried(Interop.Player());   // one-time: discover real medic item ids
            }
            else Patient = null;
        }

        public static void Close() { Open = false; Patient = null; ClearCaches(); }

        /// <summary>Drop the per-patient/per-frame caches (panel toggled, session flush).</summary>
        public static void ClearCaches()
        {
            _rowsFrame = -1; _rowsPatient = IntPtr.Zero;
            _namePtr = IntPtr.Zero; _name = null;
            _dxAgoSec = -1; _dxAgoText = null;
        }

        /// <summary>The overlay may open only when not paused/in a menu, not chatting, and not downed.
        /// (Vehicles ARE allowed.) The pause/chat reads are isolated in Interop (JIT trap), so a
        /// missing game member never blocks it outright.</summary>
        public static bool CanOpen(Soldier player)
        {
            if (Interop.GamePaused()) return false;
            if (Interop.ChatOpen()) return false;
            if (player == null) return false;
            if (Interop.IsDowned(player)) return false;                          // downed / incapacitated
            return true;
        }

        // Panel geometry. The middle panel is the patient (body + state); the ACTION card sits to its
        // right and holds the treatments you can actually perform, one per row; the LEFT column carries
        // the vitals box (once diagnosed) and the wound card for the selected part.
        private const float PanelW = 380f, TitleH = 38f, BodyH = 380f, StatusH = 44f;
        private const float ActionW = 300f, WoundW = 286f, CardGap = 10f, RowH = 42f, HeadH = 30f, Accent = 3f;

        // Flat card palette (no GUI.Button chrome anywhere — rows are drawn by hand).
        private static readonly Color CardBg = new Color(0.160f, 0.165f, 0.200f, 0.97f);
        private static readonly Color CardHead = new Color(0.235f, 0.243f, 0.290f, 1f);
        private static readonly Color RowBg = new Color(0.255f, 0.263f, 0.310f, 1f);
        private static readonly Color RowHover = new Color(0.345f, 0.360f, 0.420f, 1f);
        private static readonly Color IconChip = new Color(1f, 1f, 1f, 0.16f);   // lifts dark item icons off the row
        private static readonly Color Dim = new Color(0.78f, 0.80f, 0.85f, 1f);
        private static readonly Color AccentBlue = new Color(0.216f, 0.541f, 0.867f, 1f);

        // Wound row tints, by what the wound needs (concept colours).
        private static readonly Color BleedBg = new Color(0.290f, 0.180f, 0.180f, 1f), BleedFg = new Color(1f, 0.66f, 0.66f, 1f);
        private static readonly Color DoneBg  = new Color(0.180f, 0.230f, 0.290f, 1f), DoneFg  = new Color(0.62f, 0.82f, 1f, 1f);
        private static readonly Color WarnBg  = new Color(0.290f, 0.235f, 0.145f, 1f), WarnFg  = new Color(1f, 0.72f, 0.25f, 1f);
        private static readonly Color CalmBg  = new Color(0.235f, 0.235f, 0.250f, 1f), CalmFg  = new Color(0.88f, 0.85f, 0.78f, 1f);
        private static readonly Color InfectBg = new Color(0.215f, 0.255f, 0.130f, 1f), InfectFg = new Color(0.80f, 0.92f, 0.30f, 1f);   // sickly green

        public static void Draw()
        {
            if (!Open || Hidden) return;   // Hidden: a procedure animation is playing
            var s = Patient;
            var w = s != null ? WoundState.Peek(s) : null;

            float panelH = TitleH + BodyH + StatusH;
            float px = (Screen.width - PanelW) * 0.5f;
            float py = (Screen.height - panelH) * 0.5f;

            // Panel + title.
            Gui.Rect(new Rect(px, py, PanelW, panelH), CardBg);
            Gui.Rect(new Rect(px, py, PanelW, TitleH), CardHead);
            Gui.Label(new Rect(px + 12, py + 8, PanelW - 24, 23), PatientName(s), Color.white, 15, centered: true);

            var bodyRect = new Rect(px + (PanelW - BodyH) * 0.5f, py + TitleH, BodyH, BodyH);
            DrawBody(s, bodyRect);
            HandleClick(bodyRect);

            DrawStatus(s, w, new Rect(px + 8, py + TitleH + BodyH, PanelW - 16, StatusH - 8));
            DrawActionCard(s, w, new Rect(px + PanelW + CardGap, py, ActionW, panelH));

            // Left column: vitals box (only once diagnosed), then the wound card under it.
            float ly = py;
            float lx = px - CardGap - WoundW;
            if (w != null && w.HasDiagnosis) ly += DrawVitalsCard(w, new Rect(lx, ly, WoundW, panelH)) + CardGap;
            if (HasWoundInfo(w)) DrawWoundCard(w, new Rect(lx, ly, WoundW, py + panelH - ly));

            // Result of the last treatment (CPR outcome, syringe on an unstable patient, ...).
            if (_status != null && Time.time < _statusUntil)
            {
                var sr = new Rect(px, py + panelH + 4, PanelW, 26);
                Gui.Rect(sr, CardBg);
                Gui.Label(new Rect(sr.x + 10, sr.y + 5, sr.width - 20, 18), _status, new Color(1f, 0.85f, 0.4f, 1f), 13);
            }
        }

        /// <summary>Card background, blue accent stripe down its leading edge, and a title strip.</summary>
        private static void Card(Rect r, string title, bool accent = true)
        {
            Gui.Rect(r, CardBg);
            if (accent) Gui.Rect(new Rect(r.x, r.y, Accent, r.height), AccentBlue);
            Gui.Rect(new Rect(r.x + (accent ? Accent : 0f), r.y, r.width - (accent ? Accent : 0f), HeadH), CardHead);
            Gui.Label(new Rect(r.x + 12, r.y + 5, r.width - 24, 23), title, Color.white, 15, centered: true);
        }

        /// <summary>A flat clickable row: our own background with a hover tint, and a hand-rolled click
        /// test — GUI.Button would draw the grey box of the game's default skin over it.</summary>
        private static bool RowHit(Rect r)
        {
            var e = Event.current;
            bool hover = e != null && r.Contains(e.mousePosition);
            Gui.Rect(r, hover ? RowHover : RowBg);
            if (e != null && e.type == EventType.MouseDown && e.button == 0 && hover) { e.Use(); return true; }
            return false;
        }

        private static bool Repainting => Event.current != null && Event.current.type == EventType.Repaint;

        private static void DrawBody(Soldier s, Rect r)
        {
            // Draw-only (clicks are HandleClick's): skip the texture lookups and wound scans on the
            // Layout / input passes, where every fill would be dropped anyway.
            if (!Repainting) return;
            var bg = Assets.Body("background");
            if (bg != null) Gui.Tex(r, bg, Color.white);

            var w = s != null ? WoundState.Peek(s) : null;

            foreach (var layer in Layers)
            {
                var mask = Assets.Body(layer.Mask);
                if (mask == null) continue;

                float rate = Physiology.PartBleeding(w, layer.Part);
                int bandaged = CountBandaged(w, layer.Part);

                if (rate > 0f)
                    Gui.Tex(r, mask, BleedColor(rate));                                   // 10 shades by bleed rate
                else if (w != null && w.FractureState(layer.Part) == 1)
                    Gui.Tex(r, mask, new Color(0.95f, 0.55f, 0.10f, 0.55f));              // broken, unsplinted
                else if (bandaged > 0)
                    Gui.Tex(r, mask, new Color(0.30f, 0.55f, 0.95f, 0.55f));            // bandaged = blue-ish

                // Tourniquet band on the limb (*_t mask).
                if (w != null && w.HasTourniquet(layer.Part))
                {
                    var tq = Assets.Body(layer.Tq);
                    if (tq != null) Gui.Tex(r, tq, new Color(0.05f, 0.05f, 0.05f, 0.95f));
                }
            }

            // Selected part: bright BLUE highlight on top.
            foreach (var layer in Layers)
            {
                if (layer.Part != Selected) continue;
                var sel = Assets.Body(layer.Sel) ?? Assets.Body(layer.Mask);
                // The shipped "_s" highlight, tinted blue. Drawn once per offset in the SAME shade to
                // thicken the line outwards (a second pass in a DIFFERENT shade is what made it read as
                // two colours). Offsetting the whole sprite grows the line in every direction.
                if (sel != null)
                {
                    var selCol = new Color(0.25f, 0.60f, 1f, 0.55f);
                    const float t = 0.25f;   // outward growth in pixels; barely thicker than the shipped line
                    const float d = t * 0.7f;
                    Gui.Tex(new Rect(r.x - t, r.y, r.width, r.height), sel, selCol);
                    Gui.Tex(new Rect(r.x + t, r.y, r.width, r.height), sel, selCol);
                    Gui.Tex(new Rect(r.x, r.y - t, r.width, r.height), sel, selCol);
                    Gui.Tex(new Rect(r.x, r.y + t, r.width, r.height), sel, selCol);
                    Gui.Tex(new Rect(r.x - d, r.y - d, r.width, r.height), sel, selCol);
                    Gui.Tex(new Rect(r.x + d, r.y - d, r.width, r.height), sel, selCol);
                    Gui.Tex(new Rect(r.x - d, r.y + d, r.width, r.height), sel, selCol);
                    Gui.Tex(new Rect(r.x + d, r.y + d, r.width, r.height), sel, selCol);
                    Gui.Tex(r, sel, selCol);
                }
            }

            // Shrapnel = small amber CIRCLE on the limb (distinct from bleeding red).
            foreach (var layer in Layers)
            {
                if (CountType(w, layer.Part, WoundType.Shrapnel) <= 0) continue;
                var c = layer.Fallback.center;
                Gui.Dot(r.x + c.x * r.width, r.y + c.y * r.height, 8f, new Color(1f, 0.62f, 0.05f, 1f), new Color(0f, 0f, 0f, 0.85f));
            }
        }

        /// <summary>10 steps from pale to deep red by the part's bleed rate.</summary>
        public static Color BleedColor(float rate)
        {
            float t = Mathf.Ceil(Mathf.Clamp01(rate / 0.2f) * 10f) / 10f;
            return new Color(Mathf.Lerp(0.95f, 0.65f, t), Mathf.Lerp(0.55f, 0.02f, t), Mathf.Lerp(0.55f, 0.02f, t), Mathf.Lerp(0.45f, 0.95f, t));
        }

        // Null-safe fronts for the shared SoldierWounds counts.
        private static int CountBandaged(SoldierWounds w, BodyPartType part) => w != null ? w.CountBandaged(part) : 0;
        private static int CountType(SoldierWounds w, BodyPartType part, WoundType type) => w != null ? w.CountActive(part, type) : 0;

        // The header's name, resolved once per patient (MpNick is Photon + GetComponent, CharName a Lua
        // call - they were paid on every OnGUI pass).
        private static IntPtr _namePtr;
        private static string _name;

        private static string PatientName(Soldier s)
        {
            var p = Interop.PtrOf(s);
            if (_name != null && p == _namePtr) return _name;
            _namePtr = p;
            _name = Names.Display(s);
            if (s != null && Interop.IsPlayer(s)) _name += "  (You)";
            return _name;
        }

        private static void HandleClick(Rect bodyRect)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 0) return;
            var m = e.mousePosition;
            if (!bodyRect.Contains(m)) return;

            float u = (m.x - bodyRect.x) / bodyRect.width;
            float v = (m.y - bodyRect.y) / bodyRect.height;   // GUI y-down

            // Priority order: extremities first, torso last (torso mask can overlap).
            foreach (var layer in Layers)
            {
                if (HitPart(layer, u, v))
                {
                    Selected = layer.Part;
                    if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[click] u={u:0.00} v={v:0.00} -> {layer.Part}");
                    e.Use();
                    return;
                }
            }
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[click] u={u:0.00} v={v:0.00} -> (none)");
        }

        private static bool HitPart(Layer layer, float u, float v)
        {
            var mask = Assets.Body(layer.Mask);
            if (mask != null)
            {
                try
                {
                    // Texture v is y-up; GUI v is y-down.
                    if (mask.isReadable)
                        return mask.GetPixelBilinear(u, 1f - v).a > 0.2f;
                }
                catch { /* unreadable texture — fall through to rect */ }
            }
            return layer.Fallback.Contains(new Vector2(u, v));
        }

        /// <summary>Does the selected part have anything worth popping the wound card out for?</summary>
        private static bool HasWoundInfo(SoldierWounds w)
        {
            if (w == null) return false;
            if (w.HasTourniquet(Selected)) return true;
            return w.CountActive(Selected) > 0;
        }

        /// <summary>Vitals box (top left): the readings from the last diagnosis, one per line, and how
        /// old they are. Only drawn once the patient has been diagnosed at least once. Returns its height.</summary>
        // The vitals card shows a diagnosis SNAPSHOT, which only changes when someone re-diagnoses, so its
        // value strings are built once per snapshot (keyed on the patient's record + DxTime) instead of
        // on every OnGUI event.
        private static readonly string[] VitalsNames = { "Blood:", "Blood pressure:", "Pulse:", "Pain:" };
        private static readonly string[] _vitalsVals = new string[4];
        private static SoldierWounds _vitalsFor;
        private static float _vitalsDxTime = -1f;

        private static float DrawVitalsCard(SoldierWounds w, Rect r)
        {
            if (!ReferenceEquals(w, _vitalsFor) || w.DxTime != _vitalsDxTime || _vitalsVals[0] == null)
            {
                _vitalsFor = w; _vitalsDxTime = w.DxTime;
                _vitalsVals[0] = Mathf.RoundToInt(w.DxBlood * 100f) + "%";
                _vitalsVals[1] = Mathf.RoundToInt(w.DxBp) + "/" + Mathf.RoundToInt(w.DxBpLow);
                _vitalsVals[2] = Mathf.RoundToInt(w.DxPulse) + " bpm";
                _vitalsVals[3] = w.DxPain.ToString();
            }
            float lineH = 26f;
            float h = HeadH + VitalsNames.Length * lineH + 12f + AgoH;
            if (!Repainting) return h;   // draw-only; the height is all the other passes need
            var card = new Rect(r.x, r.y, r.width, h);
            Card(card, "Vitals");

            float y = card.y + HeadH + 6f;
            for (int i = 0; i < VitalsNames.Length; i++)
            {
                Gui.Label(new Rect(card.x + 14, y, 130, 22), VitalsNames[i], Dim, 14);
                Gui.Label(new Rect(card.x + 150, y, card.width - 164, 22), _vitalsVals[i], Color.white, 14);
                y += lineH;
            }
            // The readings are a snapshot: say how old, so a stale one reads as stale.
            Gui.Label(new Rect(card.x + 14, y, card.width - 28, 20), DiagnosedAgo(w), Dim, 12);
            return h;
        }

        private const float AgoH = 20f;

        // "diagnosed 12s ago", rebuilt only when the whole second changes.
        private static int _dxAgoSec = -1;
        private static string _dxAgoText;

        private static string DiagnosedAgo(SoldierWounds w)
        {
            int sec = Mathf.Max(0, Mathf.FloorToInt(Time.time - w.DxTime));
            if (sec != _dxAgoSec || _dxAgoText == null)
            {
                _dxAgoSec = sec;
                _dxAgoText = sec < 60 ? "diagnosed " + sec + "s ago"
                                      : "diagnosed " + (sec / 60) + "m " + (sec % 60).ToString("00") + "s ago";
            }
            return _dxAgoText;
        }

        private static readonly System.Collections.Generic.List<Wound> NoWounds = new System.Collections.Generic.List<Wound>();

        // Wound-list scroll: a hand-rolled offset (GUI.BeginScrollView is stripped in this game and throws
        // with a clip already pushed), the rows clipped to the list by a GUI group. Reset when the patient
        // or the selected part changes; clamped every pass, so wounds closing never leave it past the end.
        private static float _woundScroll;
        private static IntPtr _woundScrollPatient;
        private static BodyPartType _woundScrollPart;
        private const float WoundRowH = 46f, WoundGap = 5f;
        private static readonly Color ScrollTrack = new Color(1f, 1f, 1f, 0.08f), ScrollThumb = new Color(1f, 1f, 1f, 0.35f);

        /// <summary>The wound card (left, under the vitals): every wound on the selected part, with just
        /// what it takes to fix it. More wounds than fit scroll with the mouse wheel over the list.</summary>
        private static void DrawWoundCard(SoldierWounds w, Rect r)
        {
            System.Collections.Generic.List<Wound> list;
            if (!w.Parts.TryGetValue(Selected, out list)) list = NoWounds;
            int total = 0; foreach (var x in list) if (x.Active) total++;

            float rowH = WoundRowH, gap = WoundGap;
            bool tq = w.HasTourniquet(Selected);
            int count = total + (tq ? 1 : 0);
            float needed = HeadH + 8f + count * (rowH + gap) + 4f;
            var card = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, needed));

            // The list area under the title, and how far it can scroll.
            var view = new Rect(card.x, card.y + HeadH + 8f, card.width, Mathf.Max(0f, card.height - HeadH - 12f));
            float contentH = count > 0 ? count * (rowH + gap) - gap : 0f;
            float maxScroll = Mathf.Max(0f, contentH - view.height);

            var p = Interop.PtrOf(Patient);
            if (p != _woundScrollPatient || Selected != _woundScrollPart) { _woundScrollPatient = p; _woundScrollPart = Selected; _woundScroll = 0f; }

            // The wheel, on its own event pass (not Repaint): only over the list, and eaten there so
            // nothing else in the GUI scrolls with it.
            var e = Event.current;
            if (e != null && e.type == EventType.ScrollWheel && maxScroll > 0f && view.Contains(e.mousePosition))
            {
                _woundScroll += Mathf.Sign(e.delta.y) * (rowH + gap);
                e.Use();
            }
            _woundScroll = Mathf.Clamp(_woundScroll, 0f, maxScroll);

            // Draw-only from here: nothing on the card is clickable, so the name / state strings are only
            // worth building on the Repaint pass.
            if (!Repainting) return;
            Card(card, PartName(Selected));

            // Rows in the group's own coordinates, shifted by the scroll; the group clips them to the list.
            var local = new Rect(0f, 0f, card.width, view.height);
            float y = -_woundScroll;
            GUI.BeginGroup(view);
            try
            {
                if (tq)
                {
                    if (y + rowH > 0f && y < view.height) WoundRow(local, y, rowH, "Tourniquet", "Applied", DoneBg, DoneFg);
                    y += rowH + gap;
                }

                foreach (var wd in list)
                {
                    if (!wd.Active) continue;
                    if (y >= view.height) break;                                 // below the list: done
                    if (y + rowH <= 0f) { y += rowH + gap; continue; }          // scrolled off the top
                    DrawWoundRow(w, wd, local, y, rowH);
                    y += rowH + gap;
                }
            }
            finally { GUI.EndGroup(); }

            // A thin position indicator down the right edge, only when the list overflows.
            if (maxScroll > 0f)
            {
                var track = new Rect(card.xMax - 6f, view.y, 3f, view.height);
                Gui.Rect(track, ScrollTrack);
                float thumbH = Mathf.Max(12f, view.height * view.height / contentH);
                float thumbY = track.y + (track.height - thumbH) * (_woundScroll / maxScroll);
                Gui.Rect(new Rect(track.x, thumbY, track.width, thumbH), ScrollThumb);
            }
        }

        /// <summary>One wound's row on the wound card.</summary>
        private static void DrawWoundRow(SoldierWounds w, Wound wd, Rect card, float y, float rowH)
        {
            // Second line is the wound's STATE, not an instruction — the action card already says
            // what can be done about it.
            string name, state; Color bg, fg;
            switch (wd.Type)
            {
                case WoundType.Shrapnel:
                    name = "Shrapnel";
                    state = w.Exposed.Contains(Selected) ? "Exposed" : "Embedded";
                    bg = WarnBg; fg = WarnFg;
                    break;
                case WoundType.Fracture:
                    name = "Fracture";
                    state = wd.Stabilised ? "Splinted" : "Broken";
                    if (wd.Stabilised) { bg = DoneBg; fg = DoneFg; } else { bg = WarnBg; fg = WarnFg; }
                    break;
                case WoundType.Bruise:
                    name = Physiology.WoundName(wd.Size, wd.Kind);
                    state = "Bruised";
                    bg = CalmBg; fg = CalmFg;
                    break;
                default:
                    name = Physiology.WoundName(wd.Size, wd.Kind);
                    if (wd.Bandaged)                  { state = "Bandaged";   bg = DoneBg;  fg = DoneFg; }
                    else if (w.HasTourniquet(Selected)) { state = "Tourniquet"; bg = DoneBg;  fg = DoneFg; }
                    else                              { state = "Bleeding";   bg = BleedBg; fg = BleedFg; }
                    // Infection overrides the colour: a sickly tint so it stands out on the card.
                    int inf = InfectionStage(wd);
                    if (inf > 0)
                    {
                        state = wd.Bandaged ? (inf == 2 ? "Bandaged - infected" : "Bandaged - infection setting in")
                                            : (inf == 2 ? "Bleeding - infected" : "Bleeding - infection setting in");
                        bg = InfectBg; fg = InfectFg;
                    }
                    break;
            }
            WoundRow(card, y, rowH, name, state, bg, fg);
        }

        // Infection shown from this level on (a trace below it isn't worth a tag).
        private const float InfectionShowFrom = 0.05f, InfectionFullFrom = 0.5f;

        /// <summary>0 = nothing to show, 1 = infection setting in, 2 = infected.</summary>
        private static int InfectionStage(Wound wd)
        {
            if (!Plugin.C.InfectionEnabled.Value || wd.Infection < InfectionShowFrom) return 0;
            return wd.Infection >= InfectionFullFrom ? 2 : 1;
        }

        private static void WoundRow(Rect card, float y, float h, string name, string todo, Color bg, Color fg)
        {
            Gui.Rect(new Rect(card.x + 8, y, card.width - 16, h), bg);
            Gui.Label(new Rect(card.x + 16, y + 4, card.width - 32, 22), name, fg, 14);
            Gui.Label(new Rect(card.x + 16, y + 24, card.width - 32, 20), todo, Dim, 12);
        }

        /// <summary>Middle panel, under the body: the patient's state and the live IV indicator.</summary>
        private static readonly Color ArrestCol = new Color(1f, 0.3f, 0.3f, 1f), StateCol = new Color(1f, 0.85f, 0.4f, 1f);
        private static readonly Color IvCol = new Color(0.9f, 0.4f, 0.4f, 1f);

        private static void DrawStatus(Soldier s, SoldierWounds w, Rect r)
        {
            if (!Repainting) return;   // draw-only
            float y = r.y + 4;

            string st = StateLine(s, w);
            if (st != null)
            {
                Gui.Label(new Rect(r.x + 12, y, r.width - 24, 20), st, w != null && w.Arrested ? ArrestCol : StateCol, 14);
                y += 22;
            }

            if (w != null && w.IvRemaining > 0.001f)
                Gui.Label(new Rect(r.x + 12, y, r.width - 24, 20), IvText(w.IvCount), IvCol, 14);
        }

        // Cached status strings: the arrest countdown per whole second, the IV line per bag count.
        private static int _arrestSec = -1, _ivCount = -1;
        private static string _arrestText, _ivText;

        private static string IvText(int count)
        {
            if (count != _ivCount || _ivText == null)
            {
                _ivCount = count;
                _ivText = count > 1 ? "transfusing blood... (" + count + " IVs)" : "transfusing blood...";
            }
            return _ivText;
        }

        private static string StateLine(Soldier s, SoldierWounds w)
        {
            if (w == null) return null;
            if (w.Arrested)
            {
                int t = Mathf.Max(0, Mathf.CeilToInt(w.ArrestTimeLeft));
                if (t != _arrestSec || _arrestText == null)
                {
                    _arrestSec = t;
                    _arrestText = "CARDIAC ARREST - CPR needed (" + (t / 60) + ":" + (t % 60).ToString("00") + " left)";
                }
                return _arrestText;
            }
            if (!Interop.IsDowned(s)) return null;
            if (!Physiology.Stable(w)) return "Unconscious - unstable: stop bleeding / restore blood";
            // The tier the wake-up roll reads (infection pain doesn't keep anyone down).
            if (Pain.KnockoutTier(w) == PainTier.High) return "Unconscious - stable, but in severe pain (aspirin)";
            return "Unconscious - stable, should come round (syringe helps)";
        }

        /// <summary>Configured item id for an action's in-game icon (blank ⇒ use the bundled PNG).</summary>
        private static string ItemId(ActionKind k)
        {
            var c = Plugin.C;
            switch (k)
            {
                case ActionKind.Bandage:  return c.IdBandage.Value;
                case ActionKind.Forceps:  return c.IdForceps.Value;
                case ActionKind.Scissors: return c.IdScissors.Value;
                case ActionKind.Splint:   return c.IdSplint.Value;
                case ActionKind.Syringe:  return c.IdSyringe.Value;
                case ActionKind.Aspirin:  return c.IdAspirin.Value;
                default: return "";
            }
        }

        private static bool IsSelf(Soldier patient)
        {
            try { return Interop.IsPlayer(patient); } catch { return false; }
        }

        /// <summary>One row of the action card: what it is and what it costs.</summary>
        private struct Row
        {
            public ActionKind Kind; public string Label, Icon;
        }

        private static readonly System.Collections.Generic.List<Row> _rows = new System.Collections.Generic.List<Row>();

        // The rows are rebuilt once per FRAME (first OnGUI pass) and reused by the other passes: each
        // build walks the medic's inventory 3-5 times (Items.CountOf) and runs the medic test, and OnGUI
        // runs at least twice a frame (Layout + Repaint) plus once per input event.
        private static int _rowsFrame = -1;
        private static IntPtr _rowsPatient;
        private static BodyPartType _rowsSel;
        private static int _rowsLimbCount;

        private static int Carried(Soldier medic, string id) => Items.CountOf(medic, id);

        private static void EnsureRows(Soldier s, SoldierWounds w, out int limbCount)
        {
            int f = Time.frameCount;
            var p = Interop.PtrOf(s);
            if (f != _rowsFrame || p != _rowsPatient || Selected != _rowsSel)
            {
                _rowsFrame = f; _rowsPatient = p; _rowsSel = Selected;
                BuildRows(s, w, out _rowsLimbCount);
            }
            limbCount = _rowsLimbCount;
        }

        /// <summary>
        /// Build the action card's rows: what can be done to the SELECTED part first, then what applies to
        /// the patient as a whole. ONLY actions that can actually be performed right now appear — no item,
        /// not a medic, wrong body part or nothing to treat means the row is simply absent.
        /// </summary>
        private static void BuildRows(Soldier s, SoldierWounds w, out int limbCount)
        {
            _rows.Clear();
            var c = Plugin.C;
            var actor = Interop.Player();
            bool medic = Interop.IsClassMedic(actor), self = IsSelf(s);
            bool limb = Body.IsLimb(Selected);
            float bleed = Physiology.PartBleeding(w, Selected);
            int shrapnel = CountType(w, Selected, WoundType.Shrapnel);
            int bandaged = CountBandaged(w, Selected);
            bool exposed = w != null && w.Exposed.Contains(Selected);
            bool tq = w != null && w.HasTourniquet(Selected);
            int fracture = w != null ? w.FractureState(Selected) : 0;

            // ---- the selected body part ----
            int bandages = Carried(actor, c.IdBandage.Value);
            if (bandages > 0 && w != null && w.CountActive(Selected, WoundType.Bleeding, includeBandaged: false) > 0)
                _rows.Add(new Row { Kind = ActionKind.Bandage, Label = CountLabel(_bandageLabels, "Bandage (x", bandages), Icon = "ui/bandage.png" });

            if (limb && (bleed > 0f || tq))
                _rows.Add(new Row { Kind = ActionKind.Tourniquet, Label = tq ? "Remove tourniquet" : "Tourniquet",
                                    Icon = "ui/tourniquet.png" });

            if (limb && fracture == 1)
                _rows.Add(new Row { Kind = ActionKind.Splint, Label = "Splint", Icon = "ui/splint.png" });

            // Shrapnel: cut it open first, then pull it out. Only ever one of the two, and only with the tool.
            if (shrapnel > 0)
            {
                if (!exposed && Carried(actor, c.IdScissors.Value) > 0)
                    _rows.Add(new Row { Kind = ActionKind.Scissors, Label = "Cut open", Icon = "ui/triage_card.png" });
                if (exposed && Carried(actor, c.IdForceps.Value) > 0)
                    _rows.Add(new Row { Kind = ActionKind.Forceps, Label = "Forceps", Icon = "ui/surgical_kit.png" });
            }

            if (medic && bandaged > 0)
                _rows.Add(new Row { Kind = ActionKind.Stitch, Label = "Stitch", Icon = "ui/surgical_kit.png" });

            limbCount = _rows.Count;

            // ---- the patient as a whole ----
            _rows.Add(new Row { Kind = ActionKind.Diagnose, Label = "Diagnose", Icon = "categories/examine_patient.png" });

            if (!self && w != null && w.Arrested)
                _rows.Add(new Row { Kind = ActionKind.CPR, Label = "CPR", Icon = "categories/advanced_treatment.png" });

            int syringes = Carried(actor, c.IdSyringe.Value);
            if (syringes > 0)
                _rows.Add(new Row { Kind = ActionKind.Syringe, Label = syringes > 1 ? CountLabel(_syringeLabels, "Syringe (x", syringes) : "Syringe",
                                    Icon = "ui/iv.png" });

            if (medic && limb)
                _rows.Add(new Row { Kind = ActionKind.BloodIV, Label = "Blood IV", Icon = "ui/iv.png" });

            int aspirin = Carried(actor, c.IdAspirin.Value);
            if (aspirin > 0)
                _rows.Add(new Row { Kind = ActionKind.Aspirin, Label = CountLabel(_aspirinLabels, "Aspirin (x", aspirin), Icon = "ui/painkillers.png" });
        }

        // "Bandage (x3)" etc., one string per count, built once: the rows are rebuilt every frame the panel
        // is open, and each rebuild concatenated these afresh.
        private static readonly Dictionary<int, string> _bandageLabels = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _syringeLabels = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _aspirinLabels = new Dictionary<int, string>();

        private static string CountLabel(Dictionary<int, string> cache, string prefix, int n)
        {
            if (!cache.TryGetValue(n, out var t)) { t = prefix + n + ")"; if (cache.Count < 256) cache[n] = t; }
            return t;
        }

        /// <summary>The action card (right): the treatments you can perform, grouped part-first.</summary>
        private static void DrawActionCard(Soldier s, SoldierWounds w, Rect r)
        {
            EnsureRows(s, w, out int limbCount);

            float needed = HeadH + 10f + _rows.Count * (RowH + 5f) + (limbCount > 0 ? 24f : 0f) + 24f + 6f;
            var card = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, needed));
            Card(card, "Treatment");

            float y = card.y + HeadH + 10f;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (i == 0 && limbCount > 0) y = Header(card, y, PartName(Selected));
                if (i == limbCount)          y = Header(card, y, "Patient");
                if (y + RowH > card.y + card.height - 4f) break;
                ActionRow(s, card, y, _rows[i]);
                y += RowH + 5f;
            }
        }

        private static readonly Color HeaderCol = new Color(0.55f, 0.58f, 0.66f, 1f);

        // Upper-cased section titles, cached: the action card runs on every OnGUI pass (its rows take
        // clicks), and ToUpperInvariant allocated a fresh string per header per pass.
        private static readonly Dictionary<string, string> _upper = new Dictionary<string, string>(StringComparer.Ordinal);

        private static float Header(Rect r, float y, string text)
        {
            if (Repainting)
            {
                if (!_upper.TryGetValue(text, out var up)) { up = text.ToUpperInvariant(); _upper[text] = up; }
                Gui.Label(new Rect(r.x + 14, y, r.width - 28, 18), up, HeaderCol, 12);
            }
            return y + 22f;
        }

        private static void ActionRow(Soldier s, Rect card, float y, Row row)
        {
            var rect = new Rect(card.x + 8, y, card.width - 16, RowH);
            bool clicked = RowHit(rect);

            // Item icons are dark artwork on transparency: a light chip behind them makes them readable.
            var chip = new Rect(rect.x + 5, rect.y + 4, RowH - 8, RowH - 8);
            Gui.Rect(chip, IconChip);
            var iconRect = new Rect(chip.x + 3, chip.y + 3, chip.width - 6, chip.height - 6);
            var gameIcon = Icons.ItemTex(ItemId(row.Kind));
            if (gameIcon != null) Gui.TexFit(iconRect, gameIcon, Color.white);
            else { var fb = Assets.Load(row.Icon); if (fb != null) Gui.Tex(iconRect, fb, Color.white); }

            Gui.Label(new Rect(chip.xMax + 12, rect.y + 10, rect.width - RowH - 24, 22), row.Label, Color.white, 14);

            if (clicked) { Procedure.Start(s, Selected, row.Kind); _rowsFrame = -1; }   // inventory/wounds change
        }

        private static string PartName(BodyPartType p)
        {
            switch (p)
            {
                case BodyPartType.head: return "Head";
                case BodyPartType.chest: return "Chest";
                case BodyPartType.arm_l: return "Left Arm";
                case BodyPartType.arm_r: return "Right Arm";
                case BodyPartType.leg_l: return "Left Leg";
                case BodyPartType.leg_r: return "Right Leg";
                default: return p.ToString();
            }
        }

        public static void DrawDebug()
        {
            var c = Plugin.C;
            float x = 12f, y = 12f, w = 380f;
            Gui.Rect(new Rect(x, y, w, 82f), new Color(0f, 0f, 0f, 0.6f));
            Gui.Label(new Rect(x + 6, y + 4, w - 12, 18), "Advanced Medic — loaded", new Color(0.6f, 1f, 0.6f, 1f), 13);
            var player = Interop.Player();
            var pw = player != null ? WoundState.Peek(player) : null;
            float walk = pw != null ? Effects.ComputeWalkFactor(pw) : 1f;
            Gui.Label(new Rect(x + 6, y + 24, w - 12, 18),
                "player:" + (player != null ? "ok" : "null") + "  tracked:" + WoundState.TrackedCount +
                "  IS:" + (Interop.ImmersiveShakePresent ? "yes" : "no") +
                "  pain:" + Effects.PainIntensity.ToString("0") + "  walk:" + walk.ToString("0.00"),
                Color.white, 12);
            Gui.Label(new Rect(x + 6, y + 44, w - 12, 18),
                "open(" + c.OpenKey.Value + "):" + (Open ? "OPEN" : "closed") +
                "  dbg:" + c.DebugOverlayKey.Value, new Color(1f, 1f, 1f, 0.75f), 12);
        }
    }
}
