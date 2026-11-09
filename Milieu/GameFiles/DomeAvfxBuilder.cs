using System;
using System.IO;
using System.Linq;

namespace Milieu.GameFiles;

/// <summary>
/// What gets baked into the dome VFX. In local mode yaw and height are applied at spawn, not here. <see cref="FloorY"/> is
/// the ground-projection floor in unit-radius space (null = plain sphere). Synced delivery (the game places an item's VFX,
/// not Milieu) bakes height and yaw into the particle's Pos/Rot Y curves and binds the emitter to its owner
/// (<see cref="AttachToOwner"/>). All defaults give byte-identical local output.
/// </summary>
internal sealed record DomeAvfxParams(
    string TextureGamePath,
    float Radius,
    float VerticalScale,
    float URepeat,
    float VRepeat,
    bool Flip,
    float Brightness,
    float? FloorY = null,
    float BakedHeight = 0f,
    float BakedYawRadians = 0f,
    bool AttachToOwner = false,
    int TestLight = 0);

/// <summary>
/// Authors the dome <c>.avfx</c> in code: one scheduler item → one timeline → one emitter → one model particle →
/// one texture and the sphere model. Every chunk, its order and its value mirror the element chain that draws the sky
/// in the reference ("Pickles Venue" ve0002.avfx: Schd, TmLn#2, Emit#5, Ptcl#11, Tex#12, Modl#7), except the deliberate
/// differences listed in docs/adr/0003-code-authored-dome-avfx.md. Chunk tags and nesting follow VFXEditor's
/// AVFX reader (Formats/AvfxFormat/**); no VFXEditor code is copied.
/// </summary>
internal static class DomeAvfxBuilder
{
    public const int Version = 0x20110913;
    public const int TriggerCount = 12;

    /// <summary>
    /// Synced delivery's emitter parent influence: InitialPosition_WithOptions (PICd 1) with position on and rotation and
    /// scale off, so the dome follows its carrier's position but not its rotation (VFXEditor's own child-emitter default).
    /// The alternative AllAlways (PICd 2) is recorded in docs/adr/0006. Local mode keeps the reference's 3/1/1/1.
    /// </summary>
    public static readonly (int PICd, int ICbS, int ICbR, int ICbP) SyncedAttach = (1, 0, 0, 1);

    public static readonly (int PICd, int ICbS, int ICbR, int ICbP) LocalAttach = (3, 1, 1, 1);

    /// <summary>
    /// Experiment (testing builds): a test light baked into the next dome VFX, to find out whether VFX effector lights can carry
    /// scene lighting to synced viewers. 0 off (the default; output byte-identical), 1 point light set up like the venue's (with shadows;
    /// red, A 1 and eye-height placement are ours),
    /// 2 the same with 10x the range and 3x the brightness (so a dark mode 1 isn't mistaken for "VFX lights don't work"),
    /// 3 directional light set up like the game's own world-lighting directional lights (see <see cref="Effector"/>), 4 the same
    /// but red, brighter and tilted so its shadows fall clearly to one side. Set only by "/milieu vfxlight"; not saved.
    /// </summary>
    public static int TestLightMode;

    private const int Pi = 1078530011; // the bit pattern of (float)Math.PI, as the reference stores AFXi etc.

    public static byte[] Build(DomeAvfxParams p)
    {
        Validate(p);
        var mesh = SphereMesh.Build(floorY: p.FloorY);
        var w = new AvfxChunkWriter();
        w.Begin("AVFX");
        var light = IsLight(p.TestLight);
        var directional = IsDirectional(p.TestLight);
        Root(w, light, directional);
        Scheduler(w);
        Timeline(w, directional);
        Emitter(w, p, light && !directional);
        if (directional) LightEmitter(w);
        Particle(w, p);
        if (light) Effector(w, p.TestLight);
        w.CString("Tex", p.TextureGamePath);
        w.Begin("Modl").Bytes("VDrw", mesh.VertexBytes()).Bytes("VIdx", mesh.IndexBytes()).End();
        w.End();
        var bytes = w.ToArray();
        SelfCheck(bytes, p, mesh);
        return bytes;
    }

    private static bool IsLight(int mode) => mode is >= 1 and <= 4;

    /// <summary>Modes 3 and 4: a directional light on its own particle-less emitter (the game's pattern), not the dome's.</summary>
    private static bool IsDirectional(int mode) => mode is 3 or 4;

    private static void Validate(DomeAvfxParams p)
    {
        if (!float.IsFinite(p.BakedHeight) || !float.IsFinite(p.BakedYawRadians))
            throw new ArgumentOutOfRangeException(nameof(p), "Baked height and yaw must be finite.");
        if (string.IsNullOrEmpty(p.TextureGamePath) || p.TextureGamePath.Any(c => c is < (char)0x20 or > (char)0x7E))
            throw new ArgumentException("Texture game path must be non-empty printable ASCII.", nameof(p));
        foreach (var (name, v) in new[] { ("Radius", p.Radius), ("VerticalScale", p.VerticalScale), ("URepeat", p.URepeat),
                                          ("VRepeat", p.VRepeat), ("Brightness", p.Brightness) })
            if (!float.IsFinite(v) || v < 0 || (v == 0 && name != "Brightness"))
                throw new ArgumentOutOfRangeException(nameof(p), $"{name} must be finite and positive (got {v}).");
    }

    private static void Root(AvfxChunkWriter w, bool light, bool directional)
    {
        w.Int("Ver", Version).Int("bDFP", 1).Int("bFG", 0).Int("bTS", 0).Int("bASH", 0)
         // Deliberate difference: the reference culls with a 25-unit clip box (bCBC/bCul 1), which would cull a dome
         // thousands of units across as soon as the box leaves the view.
         .Int("bCBC", 0).Int("bCul", 0)
         .Float("CBPx", 0).Float("CBPy", 24).Float("CBPz", 0).Float("CBSx", 25).Float("CBSy", 25).Float("CBSz", 25)
         .Float("ZBMs", 1).Float("ZBMd", 0).Int("bCmS", 0).Int("bFEL", 0).Int("bOSE", 0).Int("bOSt", 0)
         .Float("SPFR", 1).Int("SKO", 0).Int("DwLy", 2).Int("DwOT", 0).Int("DLST", 2).Int("PL1S", 0).Int("PL2S", 0)
         .Float("RvPx", 0).Float("RvPy", 0).Float("RvPz", 0).Float("RvRx", 0).Float("RvRy", 0).Float("RvRz", 0)
         .Float("RvSx", 1).Float("RvSy", 1).Float("RvSz", 1).Float("RvR", 1).Float("RvG", 1).Float("RvB", 1)
         .Int("AFXe", 0).Int("AFXi", Pi).Int("AFXo", Pi).Int("AFYe", 0).Int("AFYi", Pi).Int("AFYo", Pi)
         .Int("AFZe", 0).Int("AFZi", Pi).Int("AFZo", Pi)
         .Int("bGFE", 0).Float("GFIM", 1).Int("bLTS", 0).Int("bAGS", 0).Int("APri", 5).Int("DPri", 5)
         .Int("ScCn", 1).Int("TlCn", 1).Int("EmCn", directional ? 2 : 1).Int("PrCn", 1).Int("EfCn", light ? 1 : 0).Int("BdCn", 0)
         .Int("TxCn", 1).Int("MdCn", 1);
    }

    /// <summary>
    /// The scheduler: ItCn, TrCn, then Item chunks (chunk i holds items[0..i]) and Trgr chunks (chunk j holds every item
    /// followed by triggers[0..j]), exactly as the reference and VFXEditor's AvfxScheduler write them.
    /// </summary>
    private static void Scheduler(AvfxChunkWriter w)
    {
        w.Begin("Schd").Int("ItCn", 1).Int("TrCn", TriggerCount);
        w.Begin("Item");
        SchedulerItem(w, 0);
        w.End();
        for (var j = 0; j < TriggerCount; j++)
        {
            w.Begin("Trgr");
            SchedulerItem(w, 0);
            for (var t = 0; t <= j; t++) SchedulerItem(w, -1);
            w.End();
        }
        w.End();
    }

    private static void SchedulerItem(AvfxChunkWriter w, int timeline) =>
        w.Int("bEna", 1).Int("StTm", 0).Int("TlNo", timeline);

    /// <summary>
    /// One item for the dome emitter; with a directional test light a second item runs the light's emitter. Item chunk i holds
    /// items[0..i], as VFXEditor's AvfxTimeline writes them (and as the game's own files with several items do).
    /// </summary>
    private static void Timeline(AvfxChunkWriter w, bool directional)
    {
        w.Begin("TmLn").Int("LpSt", 0).Int("LpEd", 0).Int("BnNo", -1).Int("TICn", directional ? 2 : 1).Int("CpCn", 0);
        w.Begin("Item");
        TimelineItem(w, 0);
        w.End();
        if (directional)
        {
            w.Begin("Item");
            TimelineItem(w, 0);
            TimelineItem(w, 1);
            w.End();
        }
        w.End();
    }

    private static void TimelineItem(AvfxChunkWriter w, int emitter) =>
        w.Int("bEna", 1).Int("StTm", 0).Int("EdTm", -1).Int("BdNo", -1).Int("EfNo", -1).Int("EmNo", emitter).Int("Plfm", 0);

    /// <summary>
    /// The directional light's own emitter, copied from the game's world-lighting directional lights (e.g. the emitter carrying
    /// the light in game file hash 2965227F157E314F): a plain emitter with no particles (PrCn 0) and no child emitters, linked to
    /// effector 0. It has no ItPr (nothing to emit), so it follows the VFX root.
    /// </summary>
    private static void LightEmitter(AvfxChunkWriter w)
    {
        w.Begin("Emit").Int("SdNo", -1).Int("LpSt", 0).Int("LpEd", 0).Int("ClCn", 30).Int("EfNo", 0).Byte("bAD", 0)
         .Int("EVT", 0).Int("RBDT", 2).Int("CCOT", 0).Int("ROT", 2).Int("PrCn", 0).Int("EmCn", 0);
        w.Begin("Life").Float("Val", 60).Float("ValR", 0).Int("Type", 0).End();
        Curve(w, "CrC", 1);
        Curve(w, "CrI", 30);
        Curve(w, "ARs", 1);
        w.Begin("Col");
        CurveRgb(w, "RGB", 1, 1, 1);
        w.End();
        Axes(w, "Pos", 0);
        Axes(w, "Rot", 0);
        Axes(w, "Scl", 0);
        w.End();
    }

    /// <summary>
    /// The experiment's one effector light (VFXEditor Effector/AvfxEffector.cs + Data/AvfxEffectorData{PointLight,DirectionalLight}.cs),
    /// set up like the lights in the "Pickles Venue" ve0002.avfx (effectors 0 and 1): linked from the emitter (Emit EfNo), not a
    /// timeline item; header RoOT 2, bAOV 1, bAGm 1; colour as RGB + A with no Bri key; DstS 1; Attn 1; shadows with near 0.0001
    /// and far 20 (the near/far range Pickles says shadows depend on). Red so it stands out, offset 2.5 units ahead of and 1.5
    /// above the emitter (the dome origin, at eye height). bAOV 1 as the venue: the dome particle itself is unlit (bLgt 0).
    /// Directional (modes 3, 4): set up like the 19 of the game's 506 directional effectors that light the world (bAGm 1; the
    /// rest light only their own effect): bAOV 0, black ambient, colour as RGB + Bri (the point lights' A is not used), Pow 1,
    /// on its own particle-less emitter (<see cref="LightEmitter"/>). Mode 3 copies the most common one exactly (warm
    /// 1, 0.714, 0.388, Bri 3, Rot X 90 degrees); mode 4 is red, Bri 10, tilted.
    /// </summary>
    private static void Effector(AvfxChunkWriter w, int mode)
    {
        var point = !IsDirectional(mode);
        w.Begin("Efct").Int("EfVT", point ? 0 : 1).Int("RoOT", 2).Int("CCOT", 0).Int("bAOV", point ? 1 : 0).Int("bAGm", 1)
         .Int("LpSt", 0).Int("LpEd", 0);
        w.Begin("Data");
        if (point)
        {
            w.Begin("Col");
            CurveRgb(w, "RGB", 1f, 0.15f, 0.15f);
            Curve(w, "A", mode == 2 ? 3f : 1f);
            w.End();
            Curve(w, "DstS", mode == 2 ? 10f : 1f);
            Axes(w, "Rot", 0);
            w.Begin("Pos").Int("ACT", 0).Int("ACTR", 0);
            Curve(w, "X", 0f);
            Curve(w, "Y", 1.5f);
            Curve(w, "Z", 2.5f);
            w.End();
            w.Int("Attn", 1).Int("bSdw", 1).Int("bChS", 1).Int("bMpS", 1).Int("bMvS", 1)
             .Float("SCDN", 0.0001f).Float("SCDF", 20f);
        }
        else
        {
            w.Begin("Amb");
            CurveRgb(w, "RGB", 0f, 0f, 0f);
            w.End();
            w.Begin("Col");
            if (mode == 3) CurveRgb(w, "RGB", 1f, 0.714f, 0.388f);
            else CurveRgb(w, "RGB", 1f, 0.15f, 0.15f);
            Curve(w, "Bri", mode == 3 ? 3f : 10f);
            w.End();
            Curve(w, "Pow", 1f);
            w.Begin("Rot").Int("ACT", 0).Int("ACTR", 0);
            if (mode == 3) Curve(w, "X", 1.5707964f);
            else
            {
                Curve(w, "X", 0.8f);
                Curve(w, "Y", 0.6f);
            }
            w.End();
        }
        w.End();
        w.End();
    }

    private static void Emitter(AvfxChunkWriter w, DomeAvfxParams p, bool light)
    {
        var a = p.AttachToOwner ? SyncedAttach : LocalAttach;
        w.Begin("Emit").Int("SdNo", -1).Int("LpSt", 0).Int("LpEd", 0).Int("ClCn", 30).Int("EfNo", light ? 0 : -1).Byte("bAD", 0)
         .Int("EVT", 0).Int("RBDT", 2).Int("CCOT", 0).Int("ROT", 2).Int("PrCn", 1).Int("EmCn", 0);
        w.Begin("Life").Float("Val", 60).Float("ValR", 0).Int("Type", 0).End();
        Curve(w, "CrC", 1);
        Curve(w, "CrI", 30);
        Curve(w, "ARs", 1);
        w.Begin("Col");
        CurveRgb(w, "RGB", 1, 1, 1);
        w.End();
        Axes(w, "Pos", 0);
        Axes(w, "Rot", 0);
        Axes(w, "Scl", 1);
        w.Begin("ItPr").Int("bEnb", 1).Int("TgtB", 0).Int("LoDr", 0).Int("CrTm", 1).Int("CrCn", 1).Int("CrPr", 100)
         .Int("PICd", a.PICd).Int("PICo", 0).Int("ICbS", a.ICbS).Int("ICbR", a.ICbR).Int("ICbP", a.ICbP).Int("ICbB", 0).Int("ICSK", 0)
         .Int("IPbV", 0).Int("IPbL", 0).Int("bOvr", 0).Int("OvrV", 60).Int("OvrR", 0).Int("PrLk", -1).Int("StFr", 0)
         .Int("bStN", 0).Int("BIAX", 0).Int("BIAY", 0).Int("BIAZ", 0).Int("GenD", 0).Int("bGD", 0).End();
        w.End();
    }

    private static void Particle(AvfxChunkWriter w, DomeAvfxParams p)
    {
        w.Begin("Ptcl").Int("LpSt", 0).Int("LpEd", 0)
         .Int("PrVT", 5)          // Model
         .Int("RBDT", 2).Int("RoOT", 2).Int("CCOT", 0)
         .Int("RMT", 8)           // render mode as the reference (opaque)
         .Int("CulT", 0).Int("EnvT", 0).Int("DirT", 0).Int("UVPT", 0).Int("DwPr", 1).Int("DsDt", 1).Int("DsDw", 1)
         .Int("DsSp", 0).Int("Coll", -1).Int("bS11", 0).Int("bATM", 0).Int("bAFg", 0).Int("bNea", 0).Int("bFar", 0)
         .Float("NeSt", 0).Float("NeEd", 1).Float("FaSt", 50).Float("FaEd", 60).Int("FaBP", 0)
         .Int("UvSN", 2).Int("EvAR", 0).Int("DlAR", 0).Int("LBAR", 0).Int("DOTy", 0).Float("DpOf", 0).Int("bSCt", 0);
        w.Begin("Life").Float("Val", -1).Float("ValR", 0).Int("Type", 0).End();
        Curve(w, "ARs", 1);

        // Particle scale: X (and Z, via ACT 3) is the radius of the unit sphere; Y is radius × vertical scale.
        w.Begin("Scl").Int("ACT", 3).Int("ACTR", 0);
        Curve(w, "X", p.Radius);
        Curve(w, "Y", p.Radius * p.VerticalScale);
        w.End();
        AxesY(w, "Rot", p.BakedYawRadians);   // local: yaw is applied at spawn (0 here)
        AxesY(w, "Pos", p.BakedHeight);       // local: height is applied at spawn (0 here)
        w.Begin("Col");
        Curve(w, "Bri", p.Brightness);
        w.End();

        // UV set 0 feeds the colour texture. The reference scales U by 2 ("the UV doubling"); flip mirrors it.
        w.Begin("UvSt").Int("CUvT", 0);
        w.Begin("Scl").Int("ACT", 0).Int("ACTR", 0);
        Curve(w, "X", p.Flip ? -p.URepeat : p.URepeat);
        Curve(w, "Y", p.VRepeat);
        w.End();
        Axes(w, "Scr", 0);
        w.End();
        // UV set 1 is unused (its texture slot is disabled); kept static.
        w.Begin("UvSt").Int("CUvT", 0);
        w.Begin("Scl").Int("ACT", 0).Int("ACTR", 0);
        Curve(w, "X", 1);
        Curve(w, "Y", 1);
        w.End();
        Axes(w, "Scr", 0);
        w.End();

        w.Begin("Data").Int("MNRv", 0).Int("MNRt", 0).Int("MNRi", 0).Int("FrsT", 0).Int("DLT", 0).Int("PLT", 0)
         .Int("bLgt", 0).Int("bShp", 0).Byte("MdNo", 0);
        Curve(w, "FrC", 1);
        w.Begin("FrRt").Int("ACT", 0).Int("ACTR", 0);
        Curve(w, "Z", -0.05235988f);
        w.End();
        w.Begin("ColB");
        CurveRgb(w, "RGB", 1, 1, 1);
        w.End();
        w.Begin("ColE");
        CurveRgb(w, "RGB", 1, 1, 1);
        w.End();
        w.End();

        // TC1 references the texture through TLst (a byte list), as the reference does; it has no TxNo.
        w.Begin("TC1").Int("bEna", 1).Int("bC2A", 0).Int("bUSC", 0).Int("bPFC", 0).Int("UvSN", 0).Int("TFT", 1)
         .Int("TBUT", 0).Int("TBVT", 0).Int("TCCT", 3).Int("TCAT", 1).Byte("TLst", 0).End();
        w.Begin("TC2").Int("bEna", 0).Int("bC2A", 0).Int("bUSC", 0).Int("bPFC", 0).Int("UvSN", 1).Int("TFT", 1)
         .Int("TBUT", 0).Int("TBVT", 0).Int("TCCT", 0).Int("TCAT", 0).Int("TxNo", -1).End();
        w.Begin("TC3").Int("bEna", 0).Int("bC2A", 0).Int("bUSC", 0).Int("bPFC", 0).Int("UvSN", 0).Int("TFT", 1)
         .Int("TBUT", 1).Int("TBVT", 1).Int("TCCT", 0).Int("TCAT", 0).Int("TxNo", -1).End();
        w.Begin("TN").Int("bEna", 0).Int("UvSN", 0).Int("TFT", 1).Int("TBUT", 0).Int("TBVT", 0).Int("TxNo", -1);
        Curve(w, "NPow", 10);
        w.End();
        w.Begin("TR").Int("bEna", 0).Int("bUSC", 0).Int("TFT", 1).Int("TCCT", 0).Int("TxNo", -1).End();
        w.Begin("TD").Int("bEna", 0).Int("bT1", 0).Int("bT2", 1).Int("bT3", 0).Int("bT4", 0).Int("UvSN", 0).Int("TFT", 1)
         .Int("TBUT", 0).Int("TBVT", 0).Int("TxNo", -1);
        Curve(w, "DPow", 0.2f);
        w.End();
        w.Begin("TP").Int("bEna", 0).Int("TFT", 1).Int("TBT", 0).Int("TxNo", -1).End();
        w.End();
    }

    /// <summary>A one-key 1-axis curve: KeyC, BvPr, BvPo, RanT, Keys (16-byte key: u16 time, u16 type, 3 × f32).</summary>
    private static void Curve(AvfxChunkWriter w, string tag, float value)
    {
        w.Begin(tag).Int("KeyC", 1).Int("BvPr", 0).Int("BvPo", 0).Int("RanT", 0).Bytes("Keys", Key(1, 1, value)).End();
    }

    /// <summary>A one-key colour curve (no RanT), the three floats being R, G, B.</summary>
    private static void CurveRgb(AvfxChunkWriter w, string tag, float r, float g, float b)
    {
        w.Begin(tag).Int("KeyC", 1).Int("BvPr", 0).Int("BvPo", 0).Bytes("Keys", Key(r, g, b)).End();
    }

    private static byte[] Key(float a, float b, float c)
    {
        var key = new byte[16];
        BitConverter.TryWriteBytes(key.AsSpan(0, 2), (ushort)0);
        BitConverter.TryWriteBytes(key.AsSpan(2, 2), (ushort)1);
        BitConverter.TryWriteBytes(key.AsSpan(4, 4), a);
        BitConverter.TryWriteBytes(key.AsSpan(8, 4), b);
        BitConverter.TryWriteBytes(key.AsSpan(12, 4), c);
        return key;
    }

    private static void Axes(AvfxChunkWriter w, string tag, int act) =>
        w.Begin(tag).Int("ACT", act).Int("ACTR", 0).End();

    /// <summary>A 3-axis curve with no keys, or with a one-key Y curve when <paramref name="y"/> is non-zero.</summary>
    private static void AxesY(AvfxChunkWriter w, string tag, float y)
    {
        w.Begin(tag).Int("ACT", 0).Int("ACTR", 0);
        if (y != 0) Curve(w, "Y", y);
        w.End();
    }

    /// <summary>Parses the output back and throws on anything that would make the chain inconsistent.</summary>
    internal static void SelfCheck(byte[] bytes, DomeAvfxParams p, SphereMesh mesh)
    {
        var root = AvfxChunkReader.Parse(bytes);
        void Expect(bool ok, string what) { if (!ok) throw new InvalidDataException("Dome AVFX self-check failed: " + what); }

        Expect(root.Need("ScCn").AsInt() == 1 && root.All("Schd").Count() == 1, "one scheduler");
        Expect(root.Need("TlCn").AsInt() == 1 && root.All("TmLn").Count() == 1, "one timeline");
        var lights = IsLight(p.TestLight) ? 1 : 0;
        var directional = IsDirectional(p.TestLight);
        var emitters = directional ? 2 : 1;
        Expect(root.Need("EmCn").AsInt() == emitters && root.All("Emit").Count() == emitters, "emitter count");
        Expect(root.Need("PrCn").AsInt() == 1 && root.All("Ptcl").Count() == 1, "one particle");
        Expect(root.Need("TxCn").AsInt() == 1 && root.All("Tex").Count() == 1, "one texture");
        Expect(root.Need("MdCn").AsInt() == 1 && root.All("Modl").Count() == 1, "one model");
        Expect(root.Need("EfCn").AsInt() == lights && root.All("Efct").Count() == lights && root.Need("BdCn").AsInt() == 0,
               "effectors match, no binders");

        var schd = root.Need("Schd");
        Expect(schd.Need("Item").Children.Single(c => c.Tag == "TlNo").AsInt() == 0, "scheduler item → timeline 0");
        Expect(schd.All("Trgr").Count() == TriggerCount, "12 triggers");
        var tmln = root.Need("TmLn");
        var lastItems = tmln.All("Item").Last().Children;
        var emNos = lastItems.Where(c => c.Tag == "EmNo").Select(c => c.AsInt()).ToArray();
        var itemEfNos = lastItems.Where(c => c.Tag == "EfNo").Select(c => c.AsInt()).ToArray();
        Expect(tmln.Need("TICn").AsInt() == emitters && tmln.All("Item").Count() == emitters
               && tmln.Need("Item").Need("EmNo").AsInt() == 0 && tmln.Need("Item").Children.Count(c => c.Tag == "EmNo") == 1
               && emNos.SequenceEqual(Enumerable.Range(0, emitters)) && itemEfNos.All(e => e == -1),
               "timeline items → emitters in order, no effector");
        var emits = root.All("Emit").ToArray();
        Expect(emits[0].Need("EfNo").AsInt() == (lights == 1 && !directional ? 0 : -1), "emitter → effector link");
        Expect(!directional || (emits[1].Need("EfNo").AsInt() == 0 && emits[1].Need("PrCn").AsInt() == 0 && emits[1].Child("ItPr") == null),
               "light emitter → effector 0, no particles");
        var itpr = root.Need("Emit").Need("ItPr");
        Expect(itpr.Need("TgtB").AsInt() == 0, "emitter → particle 0");
        var attach = p.AttachToOwner ? SyncedAttach : LocalAttach;
        Expect((itpr.Need("PICd").AsInt(), itpr.Need("ICbS").AsInt(), itpr.Need("ICbR").AsInt(), itpr.Need("ICbP").AsInt()) == attach,
               "emitter parent influence");

        var ptcl = root.Need("Ptcl");
        Expect(ptcl.Need("PrVT").AsInt() == 5, "model particle");
        Expect(BakedY(ptcl.Need("Pos")) == p.BakedHeight, "baked height");
        Expect(BakedY(ptcl.Need("Rot")) == p.BakedYawRadians, "baked yaw");
        Expect(ptcl.Need("Data").Need("MdNo") is { Size: 1 } md && md.AsInt() == 0, "particle → model 0");
        var tc1 = ptcl.Need("TC1");
        Expect(tc1.Need("bEna").AsInt() == 1 && tc1.Need("TLst") is { Size: 1 } tl && tl.AsInt() == 0 && tc1.Child("TxNo") == null,
               "TC1 → texture 0 through TLst");
        foreach (var slot in new[] { "TC2", "TC3", "TN", "TR", "TD", "TP" })
            Expect(ptcl.Need(slot).Need("bEna").AsInt() == 0 && ptcl.Need(slot).Need("TxNo").AsInt() == -1, $"{slot} disabled");

        var tex = root.Need("Tex").Data;
        Expect(tex.Length == p.TextureGamePath.Length + 1 && tex[^1] == 0, "Tex holds the NUL-terminated path");

        var modl = root.Need("Modl");
        Expect(modl.Need("VDrw").Size == mesh.Vertices.Length * DomeVertex.Stride, "vertex buffer size");
        Expect(modl.Need("VIdx").Size == mesh.Indices.Length * 2, "index buffer size");
        Expect(mesh.Indices.All(i => i < mesh.Vertices.Length), "indices in range");
    }

    /// <summary>The value of an axis curve's one-key Y curve, or 0 when it has none.</summary>
    private static float BakedY(ChunkNode axes) => axes.Child("Y") is { } y ? y.CurveValue() : 0f;
}
