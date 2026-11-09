using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Milieu.Lighting;
using RenderLight = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Light;
using RenderLightFlags = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightFlags;
using RenderLightShape = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightShape;
using RenderFalloff = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightFalloffType;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;
using GameTransform = FFXIVClientStructs.FFXIV.Client.Graphics.Transform;

namespace Milieu.Interop;

/// <summary>
/// Creates, updates and destroys game lights (docs/adr/0009). Layouts and the create address come from FFXIVClientStructs
/// (<c>Scene.Light</c>, <c>Render.Light</c>, <c>Light.Addresses.Create</c>); the create function is called only through the
/// pointer scanned here, never the generated static, and virtual functions go through the instance's own vtable (the
/// generated <c>[VirtualFunction]</c> members). No hooks.
/// <para>
/// The lifecycle (create with a null pool name, bind the render light's transform to the object's own transform block,
/// per-frame UpdateRender/UpdateCulling/UpdateMaterials, CleanupRender then Dtor to destroy) is adapted from Brio
/// (GPL-3.0, https://github.com/Etheirys/Brio, <c>Brio/Game/World/LightingService.cs</c> and
/// <c>Interop/BrioLight.cs</c>), which credits Dynamis and LightsCameraAction; see THIRD_PARTY_NOTICES.txt.
/// </para>
/// Every call must come from the framework thread; calls from elsewhere are refused. The game may free a light by
/// itself with no notice (there is no destructor hook): each light's vtable is recorded at create and compared before
/// every later call, and a mismatch marks the handle dead with no further native call on it (a heuristic; the real
/// guards are GPose-only, the settle delay, same-tick teardown and the crash sentinel).
/// </summary>
public sealed unsafe class LightInterop : ILightSpawner
{
    private readonly IFramework framework;
    private readonly ILogSink log;
    private readonly delegate* unmanaged<uint, byte*, void*, void*> create;
    private readonly Dictionary<nint, nint> vtables = [];

    public bool Available { get; }

    public LightInterop(ISigScanner scanner, IFramework framework, ILogSink log)
    {
        this.framework = framework;
        this.log = log;
        try
        {
            if (!scanner.TryScanText(SceneLight.Addresses.Create.String, out var address) || address == 0)
            {
                log.Warning("[Milieu] The light signature was not found; scene lighting is disabled until Milieu is updated.");
                return;
            }
            create = (delegate* unmanaged<uint, byte*, void*, void*>)address;
            Available = true;
        }
        catch (Exception e)
        {
            log.Warning("[Milieu] Could not set up light interop: " + LogText.Describe(e));
            Available = false;
        }
    }

    private bool OnFrameworkThread(string what)
    {
        if (framework.IsInFrameworkUpdateThread) return true;
        log.Error($"[Milieu] Light {what} refused: not on the framework thread.");
        return false;
    }

    private static nint VtableOf(SceneLight* light) => *(nint*)light;

    public LightHandle? Create(LightParams p)
    {
        if (!Available || create == null || !OnFrameworkThread("Create")) return null;
        var light = (SceneLight*)create((uint)p.Shape, null, null);
        if (light == null)
        {
            log.Warning("[Milieu] The game did not create a light.");
            return null;
        }
        if (light->GetObjectType() != SceneObjectType.Light)
        {
            // Not what the function should return: touch nothing more on it.
            log.Warning("[Milieu] The game's light function returned something that is not a light.");
            return null;
        }
        if (light->RenderLight == null)
        {
            log.Warning("[Milieu] The game created a light without a render light.");
            light->CleanupRender();
            light->Dtor(1);
            return null;
        }
        vtables[(nint)light] = VtableOf(light);
        var render = light->RenderLight;
        render->LightShape = (RenderLightShape)(uint)p.Shape;
        render->Transform = (GameTransform*)&light->Position;   // the object's Position/Rotation/Scale block (0x50)
        render->Intensity = 1f;
        render->FalloffType = (RenderFalloff)(uint)LightRigLayout.Falloff;
        render->FalloffFactor = LightRigLayout.FalloffFactor;
        render->FlatLightSkewAngleDegrees = default;
        render->ShadowPlaneNear = LightRigLayout.ShadowPlaneNear;
        render->ShadowPlaneFar = LightRigLayout.ShadowPlaneFar;
        render->CharacterShadowRange = LightRigLayout.CharacterShadowRange;
        Write(light, p);
        return new LightHandle((nint)light);
    }

    private static void Write(SceneLight* light, LightParams p)
    {
        light->Position = p.Position;
        light->Rotation = p.Rotation;
        var render = light->RenderLight;
        if (render != null)
        {
            render->LightFlags = RenderLightFlags.SpecularHighlights | (p.CharacterShadows ? RenderLightFlags.CharacterShadows : 0);
            render->Color = p.Colour;
            render->Range = p.Range;
            render->SpotLightAngleDegrees = p.SpotAngleDegrees;
            render->AngularFalloffDegrees = p.AngularFalloffDegrees;
        }
        light->UpdateTransforms(false);
        light->UpdateCulling();
        light->UpdateMaterials();
    }

    /// <summary>The light is still ours and still a light (same vtable as at create); otherwise it is marked dead.</summary>
    private SceneLight* Live(LightHandle h)
    {
        if (h.IsDead || !vtables.TryGetValue(h.Pointer, out var vtable)) return null;
        var light = (SceneLight*)h.Pointer;
        if (VtableOf(light) == vtable) return light;
        h.MarkDead();
        vtables.Remove(h.Pointer);
        log.Warning("[Milieu] A scene light changed under Milieu (freed by the game?); it is no longer touched.");
        return null;
    }

    public bool Update(LightHandle h, LightParams p)
    {
        if (!OnFrameworkThread("Update")) return true;   // refused, not dead: nothing was done
        var light = Live(h);
        if (light == null) return false;
        Write(light, p);
        return true;
    }

    public bool Refresh(LightHandle h)
    {
        if (!OnFrameworkThread("Refresh")) return true;
        var light = Live(h);
        if (light == null) return false;
        light->UpdateRender();
        light->UpdateCulling();
        light->UpdateMaterials();
        return true;
    }

    public bool Destroy(LightHandle h)
    {
        if (!OnFrameworkThread("Destroy")) return false;
        var light = Live(h);
        h.MarkDead();
        if (light == null) return true;   // already gone, or no longer ours: never touched again
        vtables.Remove(h.Pointer);
        light->CleanupRender();
        light->Dtor(1);
        return true;
    }
}
