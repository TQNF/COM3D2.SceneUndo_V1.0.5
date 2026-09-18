using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class Driver
{
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static string OhManaged, MainManaged;

    static Assembly Resolve(object s, ResolveEventArgs e)
    {
        string n = new AssemblyName(e.Name).Name;
        foreach (string d in new string[] { OhManaged, MainManaged, BepCore })
        {
            string f = Path.Combine(d, n + ".dll");
            if (File.Exists(f)) return Assembly.LoadFrom(f);
        }
        return null;
    }

    static string BepCore;

    static void FSet(Type t, object o, string n, object v) { t.GetField(n, BF).SetValue(o, v); }
    static object F(Type t, object o, string n) { return t.GetField(n, BF).GetValue(o); }

    class R { public List<string> L = new List<string>(); public void C(string name, bool ok, string detail) { L.Add((ok ? "PASS" : "FAIL") + " | " + name + (detail.Length > 0 ? " | " + detail : "")); } }

    static Type pt, histT, propT, snapT, pcT;

    static object MkColor(int hue, int chroma)
    {
        object c = Activator.CreateInstance(pcT);
        FSet(pcT, c, "m_bUse", true);
        FSet(pcT, c, "m_nMainHue", hue);
        FSet(pcT, c, "m_nMainChroma", chroma);
        FSet(pcT, c, "m_nMainBrightness", 20);
        FSet(pcT, c, "m_nMainContrast", 30);
        FSet(pcT, c, "m_nShadowRate", 40);
        FSet(pcT, c, "m_nShadowHue", 50);
        FSet(pcT, c, "m_nShadowChroma", 60);
        FSet(pcT, c, "m_nShadowBrightness", 70);
        FSet(pcT, c, "m_nShadowContrast", 80);
        return c;
    }

    static object MkProp(string file, int rid, int val)
    {
        object p = Activator.CreateInstance(propT);
        FSet(propT, p, "FileName", file);
        FSet(propT, p, "Rid", rid);
        FSet(propT, p, "Value", val);
        return p;
    }

    static Dictionary<int, object> Dict2(params object[] kv)
    {
        Dictionary<int, object> d = new Dictionary<int, object>();
        for (int i = 0; i < kv.Length; i += 2) d[(int)kv[i]] = kv[i + 1];
        return d;
    }

    static Type mpRealT;

    static object MkBodyProp(string fn)
    {
        object p = Activator.CreateInstance(mpRealT);
        FSet(mpRealT, p, "strFileName", fn);
        return p;
    }

    static object MkSnap(Dictionary<int, object> props, object[] colors, string bodyFile, bool bodyIsCrc = false)
    {
        object s = Activator.CreateInstance(snapT);
        // translate to the real Dictionary<int,PropSnap>
        Type dictT = typeof(Dictionary<,>).MakeGenericType(typeof(int), propT);
        object real = Activator.CreateInstance(dictT);
        foreach (KeyValuePair<int, object> kv in props)
            dictT.GetMethod("Add").Invoke(real, new object[] { kv.Key, kv.Value });
        FSet(snapT, s, "Props", real);
        Array arr = Array.CreateInstance(pcT, colors.Length);
        for (int i = 0; i < colors.Length; i++) arr.SetValue(colors[i], i);
        FSet(snapT, s, "Colors", arr);
        FSet(snapT, s, "BodyFile", bodyFile);
        FSet(snapT, s, "BodyIsCrc", bodyIsCrc);
        return s;
    }

    public static string[] Run(string pluginDll, string ohManaged, string mainManaged, string bepCore)
    {
        OhManaged = ohManaged; MainManaged = mainManaged; BepCore = bepCore;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        R r = new R();

        Assembly asmCS = Assembly.LoadFrom(Path.Combine(ohManaged, "Assembly-CSharp.dll"));
        Assembly plugin = Assembly.LoadFrom(pluginDll);
        pt = plugin.GetType("COM3D2SceneUndo.SceneUndoPlugin");
        histT = pt.GetNestedType("History", BF);
        propT = pt.GetNestedType("PropSnap", BF);
        snapT = pt.GetNestedType("Snap", BF);
        pcT = asmCS.GetType("MaidParts+PartsColor");
        r.C("types resolved", pt != null && histT != null && propT != null && snapT != null && pcT != null, "");

        MethodInfo colorEqM = pt.GetMethod("ColorEquals", BF);
        MethodInfo snapEqM = pt.GetMethod("SnapEquals", BF);
        r.C("methods resolved", colorEqM != null && snapEqM != null, "");

        object cA = MkColor(100, 10);
        object cB = MkColor(100, 10);
        object cC = MkColor(200, 10);
        object cD = MkColor(100, 11);
        r.C("T1 color equal -> true", (bool)colorEqM.Invoke(null, new object[] { cA, cB }), "");
        r.C("T2 color hue diff -> false", !(bool)colorEqM.Invoke(null, new object[] { cA, cC }), "");
        r.C("T3 color chroma diff -> false", !(bool)colorEqM.Invoke(null, new object[] { cA, cD }), "");

        // SnapEquals
        object sA = MkSnap(Dict2(1, MkProp("a.menu", 42, 5), 7, MkProp(null, 0, 9)), new object[] { cA, cB }, "body_3d.menu");
        object sB = MkSnap(Dict2(1, MkProp("a.menu", 42, 5), 7, MkProp(null, 0, 9)), new object[] { cB, cA }, "body_3d.menu");
        r.C("T4 snap equal -> true", (bool)snapEqM.Invoke(null, new object[] { sA, sB }), "");

        object sC = MkSnap(Dict2(1, MkProp("b.menu", 42, 5), 7, MkProp(null, 0, 9)), new object[] { cA, cB }, "body_3d.menu");
        r.C("T5 snap file diff -> false", !(bool)snapEqM.Invoke(null, new object[] { sA, sC }), "");

        object sD = MkSnap(Dict2(1, MkProp("a.menu", 43, 5), 7, MkProp(null, 0, 9)), new object[] { cA, cB }, "body_3d.menu");
        r.C("T6 snap rid diff -> false", !(bool)snapEqM.Invoke(null, new object[] { sA, sD }), "");

        object sE = MkSnap(Dict2(1, MkProp("a.menu", 42, 5), 7, MkProp(null, 0, 10)), new object[] { cA, cB }, "body_3d.menu");
        r.C("T7 snap value diff -> false", !(bool)snapEqM.Invoke(null, new object[] { sA, sE }), "");

        object sF = MkSnap(Dict2(1, MkProp("a.menu", 42, 5)), new object[] { cA, cB }, "body_3d.menu");
        r.C("T8 snap prop count diff -> false", !(bool)snapEqM.Invoke(null, new object[] { sA, sF }), "");

        object sG = MkSnap(Dict2(1, MkProp("a.menu", 42, 5), 7, MkProp(null, 0, 9)), new object[] { cA, cC }, "body_3d.menu");
        r.C("T9 snap color diff -> false", !(bool)snapEqM.Invoke(null, new object[] { sA, sG }), "");

        object sH = MkSnap(Dict2(1, MkProp("a.menu", 42, 5), 7, MkProp(null, 0, 9)), new object[] { cA, cB }, "body_2d.menu");
        r.C("T10 SnapEquals ignores BodyFile (boundary compared separately in HandleTrigger)", (bool)snapEqM.Invoke(null, new object[] { sA, sH }), "");

        // v1.0.3: BodyIsCrc semantics (restore guard: cross-type targets are
        // skipped in Apply; detection via boundary clear in HandleTrigger)
        object sI = MkSnap(Dict2(1, MkProp("a.menu", 42, 5), 7, MkProp(null, 0, 9)), new object[] { cA, cB }, "body_2d.menu", true);
        r.C("T23 SnapEquals ignores BodyIsCrc (guard decision lives in Apply)", (bool)snapEqM.Invoke(null, new object[] { sA, sI }), "");
        FieldInfo crcF = snapT.GetField("BodyIsCrc", BF);
        r.C("T24 Snap.BodyIsCrc field set by MkSnap", crcF != null && (bool)crcF.GetValue(sI) && !(bool)crcF.GetValue(sA), "");

        // v1.0.3: ReadBodyIsCrc prefix fallback matrix (OH MaidProp has no
        // isCrcParts field -> exercises exactly the fallback path)
        MethodInfo readCrcM = pt.GetMethod("ReadBodyIsCrc", BF);
        r.C("T25 ReadBodyIsCrc resolved", readCrcM != null, "");
        if (readCrcM != null)
        {
            mpRealT = asmCS.GetType("MaidProp");
            r.C("T26 crc_ prefix -> true", (bool)readCrcM.Invoke(null, new object[] { MkBodyProp("crc_body001.menu") }), "");
            r.C("T27 crx_ prefix -> true", (bool)readCrcM.Invoke(null, new object[] { MkBodyProp("crx_body001.menu") }), "");
            r.C("T28 gp03_ prefix -> true", (bool)readCrcM.Invoke(null, new object[] { MkBodyProp("gp03_body001.menu") }), "");
            r.C("T29 plain name -> false", !(bool)readCrcM.Invoke(null, new object[] { MkBodyProp("body001.menu") }), "");
            r.C("T30 null prop -> false", !(bool)readCrcM.Invoke(null, new object[] { null }), "");
            r.C("T31 empty filename -> false", !(bool)readCrcM.Invoke(null, new object[] { MkBodyProp("") }), "");
        }

        // History core invariants
        FieldInfo listF = histT.GetField("list", BF);
        object h = Activator.CreateInstance(histT);
        MethodInfo recM = histT.GetMethod("Record", BF);
        MethodInfo clearM = histT.GetMethod("Clear", BF);
        MethodInfo undoM = histT.GetMethod("Undo", BF);
        MethodInfo redoM = histT.GetMethod("Redo", BF);
        object hA = MkSnap(Dict2(1, MkProp("a", 1, 1)), new object[0], "b.menu");
        object hB = MkSnap(Dict2(1, MkProp("b", 1, 1)), new object[0], "b.menu");
        object hC = MkSnap(Dict2(1, MkProp("c", 1, 1)), new object[0], "b.menu");
        object hD = MkSnap(Dict2(1, MkProp("d", 1, 1)), new object[0], "b.menu");
        recM.Invoke(h, new object[] { hA });
        recM.Invoke(h, new object[] { hB });
        recM.Invoke(h, new object[] { hC });
        r.C("T11 record x3", ((System.Collections.ICollection)listF.GetValue(h)).Count == 3, "");
        object[] aOut = new object[1];
        bool ur = (bool)undoM.Invoke(h, aOut);
        r.C("T12 undo1 -> B", ur && object.ReferenceEquals(aOut[0], hB), "");
        ur = (bool)undoM.Invoke(h, aOut);
        r.C("T13 undo2 -> A (baseline)", ur && object.ReferenceEquals(aOut[0], hA), "");
        ur = (bool)undoM.Invoke(h, aOut);
        r.C("T14 undo3 -> false", !ur, "");
        ur = (bool)redoM.Invoke(h, aOut);
        r.C("T15 redo -> B", ur && object.ReferenceEquals(aOut[0], hB), "");
        ur = (bool)undoM.Invoke(h, aOut);
        r.C("T16 undo -> A again", ur && object.ReferenceEquals(aOut[0], hA), "");
        recM.Invoke(h, new object[] { hD });
        r.C("T17 record trims redo tail", ((System.Collections.ICollection)listF.GetValue(h)).Count == 2, "");
        ur = (bool)redoM.Invoke(h, aOut);
        r.C("T18 redo after trim -> false", !ur, "");
        clearM.Invoke(h, null);
        r.C("T19 clear", ((System.Collections.ICollection)listF.GetValue(h)).Count == 0, "");
        Exception threw = null;
        try { recM.Invoke(h, new object[] { hD }); }
        catch (TargetInvocationException tie) { threw = tie.InnerException; }
        catch (Exception ex) { threw = ex; }
        r.C("T20 record after clear no throw (problem-4 scenario)", threw == null, threw == null ? "" : threw.ToString());
        ur = (bool)undoM.Invoke(h, aOut);
        r.C("T21 single entry undo false", !ur, "");

        histT.GetField("MaxEntries", BF).SetValue(h, 3);
        for (int i = 0; i < 5; i++) recM.Invoke(h, new object[] { MkSnap(Dict2(1, MkProp("x" + i, i, i)), new object[0], "b.menu") });
        r.C("T22 MaxEntries trim", ((System.Collections.ICollection)listF.GetValue(h)).Count == 3, "");

        // v1.0.3: cross body-type history entries (in-game the Apply guard
        // skips cross-type restores; here we verify History walk semantics
        // are unchanged when such entries exist)
        {
            object h2 = Activator.CreateInstance(histT);
            object xA = MkSnap(Dict2(1, MkProp("a", 1, 1)), new object[0], "b.menu", false);   // 2.0
            object xD = MkSnap(Dict2(1, MkProp("d", 1, 1)), new object[0], "crc_b.menu", true); // 3.0
            object xA2 = MkSnap(Dict2(1, MkProp("a2", 1, 1)), new object[0], "b.menu", false);  // 2.0 again
            recM.Invoke(h2, new object[] { xA });
            recM.Invoke(h2, new object[] { xD });
            recM.Invoke(h2, new object[] { xA2 });
            object[] o = new object[1];
            bool ok1 = (bool)undoM.Invoke(h2, o);   // from tip xA2 -> xD (guard skips replay in-game)
            bool ok2 = (bool)undoM.Invoke(h2, o);   // xD -> xA (guard skips replay in-game)
            bool ok3 = (bool)undoM.Invoke(h2, o);   // xA is the baseline -> false
            bool ok4 = (bool)redoM.Invoke(h2, o);   // xA -> xD
            bool ok5 = (bool)redoM.Invoke(h2, o);   // xD -> xA2
            r.C("T32 cross-type walk undo/redo order", ok1 && ok2 && !ok3 && ok4 && ok5, "");
        }

        return r.L.ToArray();
    }
}
