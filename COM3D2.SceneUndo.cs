using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace COM3D2SceneUndo
{
    // Ctrl+Z / Ctrl+Y undo-redo for the ORIGINAL edit scene (non-CR editor).
    // Replaces the third-party COM3D2.EditSceneUndo 0.2.2.
    //
    // Version robustness (compiled against the 2.0-era OH assembly so all
    // hard memberrefs bind on every game build; verified identical on
    // OH 2.0-era and 3.38):
    //   - Maid.SetProp(MPN,String,Int32,Boolean,Boolean) / (MPN,Int32,Boolean)
    //   - Maid.DelProp(MPN,Boolean) / AllProcProp() / IsAllProcPropBusy / GetProp
    //   - MaidParts.GetPartsColor/SetPartsColor (PartsColor = pure 10-field
    //     value struct in every build - array copy IS a complete snapshot)
    //   - CharacterMgr.GetMaid(Int32), GameMain.Instance/CharacterMgr
    //   - SceneEdit.UpdateSliders()
    // Version-variable members go through reflection with graceful fallback:
    //   - SceneEdit.UpdateCurrentItemPanel(Boolean)  (missing on CM3D2 1.x)
    // MPN/PARTS_COLOR are enumerated AT RUNTIME (enum sizes differ per build:
    // MPN 88/100/234, PARTS_COLOR 11/15) and GetProp returns null for
    // unfilled slots (subset pattern - never direct-index).
    //
    // Body 2.0<->3.0 switch handling (v1.0.4):
    //   A body-type switch is a HARD history boundary - undo/redo NEVER
    //   crosses it and NEVER flips the body type. Cross-type per-prop
    //   SetProp replay corrupts the body (TMorph vs TMorphSkin
    //   InvalidCastException spam, 2.0 filenames get redirected to crx_)
    //   and kills the 2.0/3.0 button UI until restart.
    //   Three layers (the v1.0.4 event hook is the primary detector):
    //   1) EVENT hook: Harmony prefix on Maid.SwapNewMaidProp /
    //      CharacterMgr.SwapNewMaidBody flags the swap; the first idle
    //      frame after the async rebuild clears the history BEFORE any
    //      capture/undo handling. 2026-09-18 in-game log proof: the
    //      editor swap changes NEITHER the body filename NOR
    //      MaidProp.isCrcParts, so the v1.0.3 state-marker layers were
    //      blind and the switch got recorded as a normal entry (undo/
    //      redo then flipped the body back and forth).
    //   2) boundary detection: a body file change clears the history;
    //   3) restore guard: a target entry whose isCrcParts-derived body
    //      type differs from the live body is skipped outright.
    //   Layers 2/3 stay as defense in depth for other paths; the hook
    //   targets are resolved BY NAME (AccessTools) and simply not
    //   patched when absent (OH 2.0-era builds have no body switching).
    //   DIAG (v1.0.5): optional detailed body-state dump behind the
    //   "Debug/DiagLog" config entry (default OFF) - all MaidProp fields,
    //   filtered Maid/TBody members, Morph* component types; plus a
    //   changed-slot list on every record (always on, cheap).
    //   v1.0.4 postmortem: its diag code compared Type objects with !=
    //   (compiles to Type::op_Inequality, .NET 4.0-only, missing on the
    //   old Mono CLR 2.0) which killed HandleTrigger/Apply at JIT time.
    //   Type checks now go through type-NAME strings (the established
    //   pattern in this file), and the build script runs the Cecil BCL
    //   audit as a hard gate after every compile (bad DLL is deleted).
    [BepInPlugin("org.com3d2.sceneundo", "COM3D2 SceneUndo", "1.0.5")]
    public class SceneUndoPlugin : BaseUnityPlugin
    {
        // ---- history (same tested invariants as CRE.SceneUndo) ----
        private sealed class History
        {
            private readonly List<Snap> list = new List<Snap>();
            private int cursor = -1;
            public int MaxEntries = 50;

            public int Count { get { return list.Count; } }
            public int Cursor { get { return cursor; } }

            public void Record(Snap s)
            {
                if (cursor >= 0 && cursor + 1 < list.Count)
                    list.RemoveRange(cursor + 1, list.Count - cursor - 1);
                cursor = -1;
                list.Add(s);
                if (list.Count > MaxEntries)
                    list.RemoveRange(1, list.Count - MaxEntries);
            }

            public void Clear()
            {
                list.Clear();
                cursor = -1;
            }

            public bool Undo(out Snap s)
            {
                s = null;
                if (cursor < 0)
                {
                    if (list.Count == 0) return false;
                    cursor = list.Count - 1;
                }
                cursor--;
                if (cursor < 0)
                {
                    cursor = 0;
                    return false;
                }
                s = list[cursor];
                return true;
            }

            public bool Redo(out Snap s)
            {
                s = null;
                if (cursor < 0) return false;
                cursor++;
                if (cursor >= list.Count)
                {
                    cursor = list.Count - 1;
                    return false;
                }
                s = list[cursor];
                return true;
            }
        }

        private sealed class PropSnap
        {
            public string FileName;
            public int Rid;
            public int Value;
        }

        private sealed class Snap
        {
            public Dictionary<int, PropSnap> Props;   // runtime MPN value -> prop data
            public MaidParts.PartsColor[] Colors;     // runtime PARTS_COLOR slot count
            public string BodyFile;                   // MPN.body filename (boundary detect)
            public bool BodyIsCrc;                    // body type (2.0=false / 3.0=true, restore guard)
        }

        private const string Version = "1.0.5";

        // MaidParts' backing colors array field (enum may be LARGER than the
        // array: 3.38 PARTS_COLOR has 15 members but the array has 13 slots -
        // never trust the enum size). Located by type name string compare
        // (never use == on Type objects: compiles to Type::op_Equality which
        // does not exist on the old Mono BCL).
        private static readonly FieldInfo fiPartsColors = FindPartsColorsField();

        private static FieldInfo FindPartsColorsField()
        {
            try
            {
                FieldInfo[] fs = typeof(MaidParts).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fallback = null;
                for (int i = 0; i < fs.Length; i++)
                {
                    if (fs[i].FieldType.ToString() != "MaidParts+PartsColor[]") continue;
                    // prefer m_aryPartsColor (m_aryPartsColorDefault also exists on 3.38)
                    if (fs[i].Name == "m_aryPartsColor") return fs[i];
                    if (object.ReferenceEquals(fallback, null)) fallback = fs[i];
                }
                return fallback;
            }
            catch { }
            return null;
        }

        // SceneEdit::UpdateCurrentItemPanel(Boolean) - public since 2.x-era OH,
        // missing on CM3D2 1.x: resolve once, skip when absent
        private static readonly MethodInfo miUpdateCurrentItemPanel =
            typeof(SceneEdit).GetMethod("UpdateCurrentItemPanel",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                new Type[] { typeof(bool) }, null);

        // ---- body-type marker (restore guard) ----

        // MaidProp::isCrcParts - the game's own body-type marker
        // (Maid.IsCrcBody for maids is exactly GetProp(MPN.body).isCrcParts;
        // 3.38+ only - missing on OH 2.0-era / CM3D2 1.x, which have no
        // body switching at all)
        private static readonly FieldInfo fiIsCrcParts = FindIsCrcPartsField();

        private static FieldInfo FindIsCrcPartsField()
        {
            try
            {
                return typeof(MaidProp).GetField("isCrcParts",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { return null; }
        }

        // body type of a body prop: isCrcParts field (3.38+) with a
        // filename-prefix fallback mirroring the game's own CRC detection
        internal static bool ReadBodyIsCrc(MaidProp body)
        {
            if (body == null) return false;
            if (!object.ReferenceEquals(fiIsCrcParts, null))
            {
                try
                {
                    object v = fiIsCrcParts.GetValue(body);
                    if (v is bool) return (bool)v;
                }
                catch { }
            }
            string f = body.strFileName;
            if (string.IsNullOrEmpty(f)) return false;
            return f.StartsWith("crc_") || f.StartsWith("crx_") || f.StartsWith("gp03_");
        }

        private ManualLogSource log;
        private readonly History hist = new History();
        private SceneEdit sceneEdit;      // cached "__SceneEdit__" component
        private Maid prevMaid;
        private Snap prev;
        private bool mouseWasDown;
        private int frameCounter;
        private int[] mpnValues;
        private int lastErrFrame = -1000;

        private ConfigEntry<KeyCode> cfgUndoKey;
        private ConfigEntry<KeyCode> cfgRedoKey;
        private ConfigEntry<bool> cfgRequireCtrl;
        private ConfigEntry<int> cfgMaxHistory;
        private ConfigEntry<int> cfgPollFrames;
        private ConfigEntry<bool> cfgDiag;

        // ---- v1.0.4: body-swap event hook ----
        // set by the static Harmony prefix on Maid.SwapNewMaidProp /
        // CharacterMgr.SwapNewMaidBody; consumed on the first idle frame
        // after the async body rebuild (main thread only - no locking)
        private static bool s_swapPending;
        private static ManualLogSource s_log;   // logger ref for the static prefix

        private void Awake()
        {
            log = Logger;
            cfgUndoKey = Config.Bind("Keys", "Undo", KeyCode.Z, "Undo key (combined with Control)");
            cfgRedoKey = Config.Bind("Keys", "Redo", KeyCode.Y, "Redo key (combined with Control)");
            cfgRequireCtrl = Config.Bind("Keys", "RequireControl", true, "Require Control modifier for undo/redo");
            cfgMaxHistory = Config.Bind("History", "MaxEntries", 50, "Max recorded undo entries");
            cfgPollFrames = Config.Bind("General", "PollFrames", 180,
                "Periodic deep-check interval in frames (0 = only mouse-release/light-sensor triggers)");
            cfgDiag = Config.Bind("Debug", "DiagLog", false, "Log detailed body-state diagnostics (development)");
            hist.MaxEntries = Mathf.Max(2, cfgMaxHistory.Value);
            mpnValues = (int[])Enum.GetValues(typeof(MPN));            // runtime enum (88/100/234...)
            // NOTE: never use ==/!= on MethodInfo/FieldInfo etc. - the compiler
            // emits System.Reflection.MethodInfo::op_Inequality which does NOT
            // exist on Unity 5.6's .NET 3.5-era Mono BCL (added in .NET 4.0),
            // killing the whole method at JIT time (MissingMethodException).
            log.LogInfo("COM3D2 SceneUndo " + Version + " loaded. Ctrl+Z = undo, Ctrl+Y = redo. " +
                "UpdateCurrentItemPanel: " + (object.ReferenceEquals(miUpdateCurrentItemPanel, null) ? "missing (older build)" : "available"));
            s_log = log;
            TryHookSwap();
        }

        private void Update()
        {
            try { UpdateCore(); }
            catch (Exception e) { ThrottledError("Update: " + e); }
        }

        private void UpdateCore()
        {
            if (sceneEdit == null)
            {
                sceneEdit = FindSceneEdit();
                if (sceneEdit == null) { ResetAll(); return; }
            }
            Maid maid = GameMain.Instance.CharacterMgr.GetMaid(0);
            if (maid == null) { ResetAll(); return; }

            if (!ReferenceEquals(prevMaid, maid))
            {
                hist.Clear();
                prev = null;
                prevMaid = maid;
            }

            if (maid.IsAllProcPropBusy) return;

            // v1.0.4: consume a pending body-type swap (flag set by the
            // Harmony prefix) on the first idle frame - BEFORE any capture/
            // undo handling, so a Ctrl+Z right after the switch sees an
            // empty history instead of pre-switch entries (the v1.0.3
            // blind spot that let undo flip the body back).
            if (s_swapPending)
            {
                s_swapPending = false;
                hist.Clear();
                prev = null;
                log.LogInfo("Body swap detected (swap API) - history cleared.");
            }

            bool mouseDown = Input.GetMouseButton(0);
            bool releaseEdge = mouseWasDown && !mouseDown;
            mouseWasDown = mouseDown;
            if (mouseDown) return;

            bool needBaseline = prev == null;

            bool lightChanged = false;
            if (!needBaseline && prev != null)
            {
                try { lightChanged = !LightEqualsInst(maid, prev); }
                catch (Exception e) { ThrottledError("light: " + e); }
            }

            frameCounter++;
            int poll = cfgPollFrames.Value;
            bool periodic = poll > 0 && (frameCounter % poll) == 0;

            if (needBaseline || lightChanged || releaseEdge || periodic)
            {
                try { HandleTrigger(maid); }
                catch (Exception e) { ThrottledError("capture: " + e); }
            }

            if (UndoPressed())
            {
                try { DoUndo(maid); } catch (Exception e) { ThrottledError("undo: " + e); }
                return;
            }
            if (RedoPressed())
            {
                try { DoRedo(maid); } catch (Exception e) { ThrottledError("redo: " + e); }
            }
        }

        private void HandleTrigger(Maid maid)
        {
            Snap snap = Capture(maid);
            if (snap.Props.Count == 0) return;

            // body 2.0 <-> 3.0 switch = history boundary (cross-body restore
            // corrupts TMorph/TMorphSkin - EditSceneUndo v3 lesson)
            if (prev != null && snap.BodyFile != prev.BodyFile)
            {
                hist.Clear();
                prev = null;
                log.LogInfo("Body type changed - history cleared.");
            }

            if (prev == null)
            {
                prev = snap;
                hist.Record(snap);   // baseline = edit-entry state
                if (cfgDiag.Value) log.LogInfo("DIAG baseline: " + DiagBodyState(maid));
                return;
            }
            if (!SnapEquals(snap, prev))
            {
                string diff = ChangedSlots(snap, prev);
                hist.Record(snap);
                prev = snap;
                log.LogInfo("Recorded entry " + (hist.Count - 1) + " (" + snap.Props.Count + " props, changed " + diff + ").");
                if (cfgDiag.Value) log.LogInfo("DIAG record: " + DiagBodyState(maid));
            }
        }

        private bool UndoPressed()
        {
            if (!Input.GetKeyDown(cfgUndoKey.Value)) return false;
            if (cfgRequireCtrl.Value && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return false;
            return true;
        }

        private bool RedoPressed()
        {
            if (!Input.GetKeyDown(cfgRedoKey.Value)) return false;
            if (cfgRequireCtrl.Value && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return false;
            return true;
        }

        private void DoUndo(Maid maid)
        {
            Snap s;
            if (!hist.Undo(out s)) { log.LogInfo("Undo: nothing to undo."); return; }
            log.LogInfo("Undo -> entry " + hist.Cursor + " / " + (hist.Count - 1));
            Apply(maid, s);
        }

        private void DoRedo(Maid maid)
        {
            Snap s;
            if (!hist.Redo(out s)) { log.LogInfo("Redo: nothing to redo."); return; }
            log.LogInfo("Redo -> entry " + hist.Cursor + " / " + (hist.Count - 1));
            Apply(maid, s);
        }

        // ---------------- capture ----------------

        private Snap Capture(Maid maid)
        {
            Snap s = new Snap();
            Dictionary<int, PropSnap> props = new Dictionary<int, PropSnap>(mpnValues.Length);
            int[] vals = mpnValues;
            for (int i = 0; i < vals.Length; i++)
            {
                int v = vals[i];
                if (v == 0) continue; // null_mpn
                MaidProp mp = maid.GetProp((MPN)v);
                if (mp == null) continue; // unfilled slot (subset pattern)
                PropSnap ps = new PropSnap();
                ps.FileName = mp.strFileName;
                ps.Rid = mp.nFileNameRID;
                ps.Value = mp.value;
                props[v] = ps;
            }
            s.Props = props;
            s.Colors = CaptureColors(maid);
            MaidProp body = maid.GetProp(MPN.body);
            s.BodyFile = body == null ? null : body.strFileName;
            s.BodyIsCrc = ReadBodyIsCrc(body);
            return s;
        }

        private MaidParts.PartsColor[] CaptureColors(Maid maid)
        {
            if (maid.Parts == null) return new MaidParts.PartsColor[0];
            int n = DetectColorSlots(maid.Parts);
            MaidParts.PartsColor[] arr = new MaidParts.PartsColor[n];
            for (int i = 0; i < n; i++)
                arr[i] = maid.Parts.GetPartsColor((MaidParts.PARTS_COLOR)i);  // pure value struct: copy = snapshot
            return arr;
        }

        // actual slot count = backing array length (enum may be larger);
        // probe fallback covers builds where the field cannot be located
        private int DetectColorSlots(MaidParts parts)
        {
            if (!object.ReferenceEquals(fiPartsColors, null))
            {
                try
                {
                    MaidParts.PartsColor[] arr = (MaidParts.PartsColor[])fiPartsColors.GetValue(parts);
                    if (!object.ReferenceEquals(arr, null) && arr.Length > 0)
                        return arr.Length;
                }
                catch { }
            }
            int n = 0;
            while (n < 64)
            {
                try { parts.GetPartsColor((MaidParts.PARTS_COLOR)n); n++; }
                catch (Exception) { break; }
            }
            return n;
        }

        // ---------------- comparison ----------------

        // per-frame light sensor: props + color scalars (pure reads)
        private bool LightEqualsInst(Maid maid, Snap prev)
        {
            if (prev == null) return false;
            Dictionary<int, PropSnap> b = prev.Props;
            int seen = 0;
            int[] vals = mpnValues;
            for (int i = 0; i < vals.Length; i++)
            {
                int v = vals[i];
                if (v == 0) continue;
                MaidProp mp = maid.GetProp((MPN)v);
                PropSnap o;
                if (mp == null)
                {
                    if (b.ContainsKey(v)) return false;
                    continue;
                }
                seen++;
                if (!b.TryGetValue(v, out o)) return false;
                if (mp.strFileName != o.FileName) return false;
                if (mp.nFileNameRID != o.Rid) return false;
                if (mp.value != o.Value) return false;
            }
            if (seen != b.Count) return false;
            // colors (bounded by the snapshot's detected slot count)
            if (maid.Parts != null && prev.Colors != null)
            {
                for (int i = 0; i < prev.Colors.Length; i++)
                {
                    MaidParts.PartsColor c = maid.Parts.GetPartsColor((MaidParts.PARTS_COLOR)i);
                    if (!ColorEquals(ref c, ref prev.Colors[i])) return false;
                }
            }
            return true;
        }

        internal static bool ColorEquals(ref MaidParts.PartsColor a, ref MaidParts.PartsColor b)
        {
            if (a.m_bUse != b.m_bUse) return false;
            if (a.m_nMainHue != b.m_nMainHue) return false;
            if (a.m_nMainChroma != b.m_nMainChroma) return false;
            if (a.m_nMainBrightness != b.m_nMainBrightness) return false;
            if (a.m_nMainContrast != b.m_nMainContrast) return false;
            if (a.m_nShadowRate != b.m_nShadowRate) return false;
            if (a.m_nShadowHue != b.m_nShadowHue) return false;
            if (a.m_nShadowChroma != b.m_nShadowChroma) return false;
            if (a.m_nShadowBrightness != b.m_nShadowBrightness) return false;
            if (a.m_nShadowContrast != b.m_nShadowContrast) return false;
            return true;
        }

        private static bool SnapEquals(Snap a, Snap b)
        {
            if (a.Props.Count != b.Props.Count) return false;
            foreach (KeyValuePair<int, PropSnap> kv in a.Props)
            {
                PropSnap o;
                if (!b.Props.TryGetValue(kv.Key, out o)) return false;
                if (kv.Value.FileName != o.FileName) return false;
                if (kv.Value.Rid != o.Rid) return false;
                if (kv.Value.Value != o.Value) return false;
            }
            int n = a.Colors == null ? 0 : a.Colors.Length;
            int m = b.Colors == null ? 0 : b.Colors.Length;
            int len = n < m ? n : m;
            for (int i = 0; i < len; i++)
            {
                if (!ColorEquals(ref a.Colors[i], ref b.Colors[i])) return false;
            }
            return true;
        }

        // ---------------- apply ----------------

        private void Apply(Maid maid, Snap s)
        {
            // NEVER restore across the 2.0/3.0 body-type boundary (v3
            // lesson: cross-type SetProp replay corrupts TMorph/TMorphSkin
            // and kills the 2.0/3.0 button UI). Second line of defense for
            // boundary-detection lag: if the target entry is from the other
            // body type, skip the restore and re-baseline the history.
            MaidProp body = maid.GetProp(MPN.body);
            if (ReadBodyIsCrc(body) != s.BodyIsCrc)
            {
                log.LogInfo("Restore skipped - target entry is from the other body type. History re-baselined.");
                hist.Clear();
                Snap cur = Capture(maid);
                prev = cur;
                hist.Record(cur);   // fresh baseline = current actual state
                return;
            }
            string tgtBody = string.IsNullOrEmpty(s.BodyFile) ? "<null>" : s.BodyFile;
            log.LogInfo("Apply: targetBodyFile=" + tgtBody + " targetIsCrc=" + s.BodyIsCrc);
            if (cfgDiag.Value) log.LogInfo("DIAG apply: " + DiagBodyState(maid));
            // 1) remove items equipped after the snapshot ("wear new item" undo)
            int[] vals = mpnValues;
            for (int i = 0; i < vals.Length; i++)
            {
                int v = vals[i];
                if (v == 0) continue;
                if (s.Props.ContainsKey(v)) continue;
                MaidProp live = maid.GetProp((MPN)v);
                if (live == null) continue;
                if (!string.IsNullOrEmpty(live.strFileName))
                    maid.DelProp((MPN)v, false);
            }
            // 2) replay props (file items by filename+rid, value props by value)
            foreach (KeyValuePair<int, PropSnap> kv in s.Props)
            {
                PropSnap ps = kv.Value;
                if (!string.IsNullOrEmpty(ps.FileName))
                    maid.SetProp((MPN)kv.Key, ps.FileName, ps.Rid, false, false);
                else
                    maid.SetProp((MPN)kv.Key, ps.Value, false);
            }
            // 3) colors (snapshot length = actual slot count at capture time)
            if (maid.Parts != null && s.Colors != null)
            {
                for (int i = 0; i < s.Colors.Length; i++)
                    maid.Parts.SetPartsColor((MaidParts.PARTS_COLOR)i, s.Colors[i]);
            }
            // 4) apply to model (synchronous in the original editor)
            maid.AllProcProp();
            // 5) UI refresh: sliders (all builds) + current item panel (2.x+/3.38)
            try { sceneEdit.UpdateSliders(); }
            catch (Exception e) { ThrottledError("sliders: " + e); }
            if (!object.ReferenceEquals(miUpdateCurrentItemPanel, null))
            {
                try { miUpdateCurrentItemPanel.Invoke(sceneEdit, new object[] { true }); }
                catch (Exception e) { ThrottledError("itempanel: " + e); }
            }
            // 6) re-baseline to actual state (no record - keep redo tail)
            prev = Capture(maid);
        }

        private SceneEdit FindSceneEdit()
        {
            try
            {
                GameObject go = GameObject.Find("__SceneEdit__");
                if (go == null) return null;
                return go.GetComponent<SceneEdit>();
            }
            catch { return null; }
        }

        // ---------------- v1.0.4: body-swap event hook ----------------

        // Patch the game's own body-swap entry points so the boundary is
        // EVENT-driven instead of guessed from MaidProp markers (proven
        // constant across the editor's 2.0/3.0 switch). Absent on OH
        // 2.0-era builds -> nothing patched, no behavior change (those
        // builds have no body switching at all).
        private void TryHookSwap()
        {
            try
            {
                MethodInfo pre = typeof(SceneUndoPlugin).GetMethod("SwapPrefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (object.ReferenceEquals(pre, null))
                {
                    log.LogWarning("Swap hook: prefix method missing - boundary falls back to file/flag detection.");
                    return;
                }
                Harmony h = new Harmony("org.com3d2.sceneundo");
                HarmonyMethod hm = new HarmonyMethod(pre);
                // v1.0.5: patch EVERY overload with the target name.
                // v1.0.4 used AccessTools.Method(Type,name) which threw
                // "Ambiguous match" on 3.38's multiple SwapNewMaidBody
                // overloads (only the inner Maid.SwapNewMaidProp hook
                // survived - enough for the editor path, but patch all
                // overloads now so no caller escapes).
                int hooked = PatchAll(h, hm, typeof(Maid), "SwapNewMaidProp");
                hooked += PatchAll(h, hm, typeof(CharacterMgr), "SwapNewMaidBody");
                log.LogInfo("Body swap hook: " + hooked + " target method(s) patched" +
                    (hooked == 0 ? " (older build - no body switching present)." : "."));
            }
            catch (Exception e)
            {
                log.LogWarning("Body swap hook unavailable - boundary falls back to file/flag detection: " + e.Message);
            }
        }

        private static int PatchAll(Harmony h, HarmonyMethod hm, Type t, string name)
        {
            int n = 0;
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != name) continue;
                try { h.Patch(ms[i], prefix: hm); n++; }
                catch { }   // one unpatchable overload (abstract etc.) never kills the rest
            }
            return n;
        }

        // Harmony prefix for Maid.SwapNewMaidProp / CharacterMgr.SwapNewMaidBody.
        // Parameterless prefix (allowed by Harmony): just flags the boundary;
        // the original method always executes - no game behavior is modified.
        public static void SwapPrefix()
        {
            s_swapPending = true;
            ManualLogSource l = s_log;
            if (!object.ReferenceEquals(l, null)) l.LogInfo("Swap API intercepted - body-type switch in progress.");
        }

        // ---------------- v1.0.4+ diagnostics (config-gated: Debug/DiagLog) ----------------

        // Dump every plausibly body-related marker so the game log shows
        // WHICH one actually changes across a 2.0<->3.0 switch (the v1.0.3
        // markers - filename + isCrcParts - were proven constant on 3.38).
        // Called only on baseline/record/apply frames (never per frame).
        internal static string DiagBodyState(Maid maid)
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(640);
                MaidProp body = maid.GetProp(MPN.body);
                sb.Append("bodyFile=").Append(object.ReferenceEquals(body, null) ? "<null>" : body.strFileName);
                sb.Append(" isCrc=").Append(ReadBodyIsCrc(body));
                if (!object.ReferenceEquals(body, null))
                {
                    sb.Append(" prop#").Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(body).ToString("X"));
                    FieldInfo[] fs = typeof(MaidProp).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    for (int i = 0; i < fs.Length; i++)
                    {
                        Type ft = fs[i].FieldType;
                        // NEVER compare Type objects with ==/!= (Type::op_Equality/
                        // op_Inequality are .NET 4.0-only, missing on old Mono -
                        // v1.0.4 shipped exactly this bug and died at JIT)
                        if (!ft.IsPrimitive && !ft.IsEnum && ft.ToString() != "System.String") continue;
                        try { sb.Append(' ').Append(fs[i].Name).Append('=').Append(fs[i].GetValue(body)); }
                        catch { }
                    }
                }
                DumpFiltered(maid, typeof(Maid), sb, " | maid.");
                FieldInfo fb = typeof(Maid).GetField("body", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object tb = object.ReferenceEquals(fb, null) ? null : fb.GetValue(maid);
                if (!object.ReferenceEquals(tb, null))
                {
                    DumpFiltered(tb, tb.GetType(), sb, " | tbody.");
                    Component c = tb as Component;
                    if (c != null)
                    {
                        try
                        {
                            Component[] all = c.GetComponentsInChildren(typeof(Component), true);
                            Dictionary<string, bool> seen = new Dictionary<string, bool>();
                            for (int i = 0; i < all.Length; i++)
                            {
                                string n = all[i].GetType().Name;
                                if (n.ToLowerInvariant().IndexOf("morph") < 0) continue;
                                seen[n] = true;
                            }
                            sb.Append(" | morphTypes=").Append(string.Join(",", new List<string>(seen.Keys).ToArray()));
                        }
                        catch { }
                    }
                }
                return sb.ToString();
            }
            catch (Exception e) { return "diag-error: " + e.Message; }
        }

        private static void DumpFiltered(object o, Type t, System.Text.StringBuilder sb, string pfx)
        {
            try
            {
                FieldInfo[] fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < fs.Length; i++)
                {
                    if (!NameHits(fs[i].Name)) continue;
                    Type ft = fs[i].FieldType;
                    // type-NAME string compare only (see note in DiagBodyState)
                    if (!ft.IsPrimitive && !ft.IsEnum && ft.ToString() != "System.String") continue;
                    try { sb.Append(pfx).Append(fs[i].Name).Append('=').Append(fs[i].GetValue(o)); }
                    catch { }
                }
                PropertyInfo[] ps = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < ps.Length; i++)
                {
                    if (!NameHits(ps[i].Name)) continue;
                    if (ps[i].GetIndexParameters().Length != 0) continue;
                    Type pt = ps[i].PropertyType;
                    if (!pt.IsPrimitive && !pt.IsEnum && pt.ToString() != "System.String") continue;
                    try { sb.Append(pfx).Append(ps[i].Name).Append('=').Append(ps[i].GetValue(o, null)); }
                    catch { }
                }
            }
            catch { }
        }

        private static bool NameHits(string name)
        {
            string n = name.ToLowerInvariant();
            return n.IndexOf("crc") >= 0 || n.IndexOf("ver") >= 0 || n.IndexOf("body") >= 0 ||
                n.IndexOf("morph") >= 0 || n.IndexOf("skin") >= 0;
        }

        // count + sample of MPN slots that differ between two snapshots
        // (switch-vs-normal-edit fingerprint for the log)
        private static string ChangedSlots(Snap a, Snap b)
        {
            int n = 0;
            List<string> names = new List<string>();
            if (a != null && b != null)
            {
                foreach (KeyValuePair<int, PropSnap> kv in a.Props)
                {
                    PropSnap o;
                    bool same = b.Props.TryGetValue(kv.Key, out o) &&
                        kv.Value.FileName == o.FileName && kv.Value.Rid == o.Rid && kv.Value.Value == o.Value;
                    if (!same)
                    {
                        n++;
                        if (names.Count < 15) names.Add(((MPN)kv.Key).ToString());
                    }
                }
                foreach (KeyValuePair<int, PropSnap> kv in b.Props)
                {
                    if (!a.Props.ContainsKey(kv.Key))
                    {
                        n++;
                        if (names.Count < 15) names.Add("+" + ((MPN)kv.Key).ToString());
                    }
                }
            }
            return n + (names.Count > 0 ? " [" + string.Join(",", names.ToArray()) + "]" : " []");
        }

        private void ResetAll()
        {
            hist.Clear();
            prev = null;
            prevMaid = null;
            s_swapPending = false;
        }

        private void ThrottledError(string msg)
        {
            if (Time.frameCount - lastErrFrame < 300) return;
            lastErrFrame = Time.frameCount;
            log.LogError("SceneUndo " + msg);
        }
    }
}
