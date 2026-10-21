using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using UnityEngine;
using Verse;
using UnityStubs.Rendering;
using MeshDrawFrame = Waylith.Rendering.MeshDrawFrame;
using FinalDrawItem = Waylith.Rendering.FinalDrawItem;
using DrawSortKeyComparer = Waylith.Rendering.DrawSortKeyComparer;

namespace RimLayers.Presentation;

/// <summary>Manual navigation only. The logical switch remains synchronous and never interpolates storage coordinates.</summary>
internal static class LayerTransition
{
    private static LayerMapComponent? owner;
    private static Game? game;
    private static int pending = -1, displayedLayer, direction, width, height;
    private static bool waitingForTarget, hasPicture, blending, committing;
    private static float started, rootSize;
    private static Vector3 root;
    private static RenderTexture? outgoing, incoming, composite;
    private static Camera? captureCamera;
    private static Material? baseMaterial, fadeMaterial;
    private static TransitionSample sample;
    private static readonly List<FinalDrawItem> sceneItems = new();
    private static Action<RenderTexture, List<FinalDrawItem>, Camera, Rect, Color?, System.Numerics.Vector3?>? renderItems;
    private static int captureFrame = -1;
    internal static bool Active => owner != null;
    internal static bool HasPicture => Active && hasPicture;
    internal static bool Committing => committing;
    internal static int TargetLayer => pending >= 0 ? pending : displayedLayer;
    internal static int Direction => direction;
    internal static float Progress => blending ? LayerTransitionMath.Ease(Time.realtimeSinceStartup - started) : 0;
    internal static int LiveTextures => (outgoing != null ? 1 : 0) + (incoming != null ? 1 : 0) + (composite != null ? 1 : 0);
    internal static int SceneCaptures { get; private set; }
    internal static int Compositions { get; private set; }
    internal static double LastCaptureMs { get; private set; }

    internal static bool RequestRelative(LayerMapComponent component, int step) =>
        component.Layout != null && component.Layout.TryGetAdjacentLayer(
            owner == component ? TargetLayer : component.ViewedLayer, step, out int target) && Request(component, target);

    internal static bool Request(LayerMapComponent component, int target)
    {
        if (component != LayerView.Current || component.Layout == null || (uint)target >= (uint)component.Layout.LayerCount) return false;
        if (!RimLayersMod.Settings.LayerTransitions || WorldComponent_GravshipController.CutsceneInProgress)
            return LayerView.Switch(component, target);
        if (owner != null && owner != component) Cancel();
        if (owner == null && target == component.ViewedLayer) return true;
        if (owner == component && target == TargetLayer) return true;
        // A reversal before the first capture needs no animation at all.
        if (owner == component && !hasPicture && target == component.ViewedLayer) { Cancel(); return true; }
        if (owner == null)
        {
            owner = component;
            game = Current.Game;
            displayedLayer = component.ViewedLayer;
            width = Screen.width; height = Screen.height;
            root = LayerView.CameraRoot;
            rootSize = Find.CameraDriver.RootSize;
        }
        pending = target;
        LayerView.CancelCameraMotion();
        return true;
    }

    internal static void Cancel()
    {
        owner = null; game = null; pending = -1;
        hasPicture = blending = waitingForTarget = false;
        if (captureCamera != null) captureCamera.targetTexture = null;
        if (baseMaterial != null) baseMaterial.mainTexture = null;
        if (fadeMaterial != null) fadeMaterial.mainTexture = null;
        Release(ref outgoing); Release(ref incoming); Release(ref composite);
    }
    private static void Release(ref RenderTexture? texture)
    {
        if (texture == null) return;
        RenderTexture.ReleaseTemporary(texture);
        texture = null;
    }
    // Finish accepted manual navigation before handing the same event to native input.
    internal static void FinishForInput()
    {
        var component = owner; int target = TargetLayer;
        Cancel();
        if (component != null && component == LayerView.Current) LayerView.Switch(component, target);
    }
    private static bool ContextValid() => owner != null && game == Current.Game &&
        owner == LayerView.Current && !owner.map.Disposed &&
        !WorldComponent_GravshipController.CutsceneInProgress &&
        !WorldComponent_GravshipController.GravshipRenderInProgess &&
        width == Screen.width && height == Screen.height && !LongEventHandler.ShouldWaitForEvent;

    internal static void ValidateContext()
    {
        if (!Active) return;
        if (!ContextValid()) Cancel();
        else if (!RimLayersMod.Settings.LayerTransitions) FinishForInput();
    }

    internal static void AfterDraw(MeshDrawFrame frame)
    {
        if (!Active) return;
        if (!ContextValid()) { Cancel(); return; }
        if (!RimLayersMod.Settings.LayerTransitions) { FinishForInput(); return; }
        if (owner!.ViewedLayer != displayedLayer) { Cancel(); return; }
        if ((LayerView.CameraRoot - root).sqrMagnitude > .0001f || Math.Abs(Find.CameraDriver.RootSize - rootSize) > .001f)
        { FinishForInput(); return; }
        if (captureFrame == Time.frameCount) return;
        captureFrame = Time.frameCount;
        try
        {
            EnsureResources();
            if (pending >= 0)
            {
                if (!hasPicture) Capture(outgoing!, frame);
                else if (blending) (outgoing, composite) = (composite, outgoing);
                // When awaiting the first target frame, outgoing already IS the visible picture.
                int target = pending;
                direction = LayerTransitionMath.Direction(owner.Elevation(displayedLayer), owner.Elevation(target));
                pending = -1;
                committing = true;
                try { LayerView.Switch(owner, target); }
                finally { committing = false; }
                displayedLayer = target;
                root = LayerView.CameraRoot; rootSize = Find.CameraDriver.RootSize;
                hasPicture = true; waitingForTarget = true; blending = false;
                return;
            }
            if (blending && Time.realtimeSinceStartup - started >= LayerTransitionMath.Duration) { Cancel(); return; }
            Capture(incoming!, frame);
            if (waitingForTarget)
            {
                waitingForTarget = false; blending = true; started = Time.realtimeSinceStartup;
            }
            sample = LayerTransitionMath.Sample(Time.realtimeSinceStartup - started, direction);
            Compose();
        }
        catch (Exception error)
        {
            Log.WarningOnce("[RimLayers] Floor transition capture failed; using immediate navigation. " + error, 73102601);
            FinishForInput();
        }
    }

    private static void EnsureResources()
    {
        if (captureCamera == null)
        {
            var obj = new GameObject("RimLayers floor transition camera");
            captureCamera = obj.AddComponent<Camera>();
            captureCamera.enabled = false;
            baseMaterial = new Material(ShaderDatabase.Transparent) { name = "RimLayers transition base", renderQueue = 3000 };
            fadeMaterial = new Material(ShaderDatabase.Transparent) { name = "RimLayers transition fade", renderQueue = 3001 };
        }
        RenderTexture Allocate()
        {
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
            rt.filterMode = FilterMode.Bilinear; rt.wrapMode = TextureWrapMode.Clamp;
            try
            {
                if (!rt.IsCreated() && !rt.Create()) throw new InvalidOperationException("RenderTexture allocation failed.");
                return rt;
            }
            catch { RenderTexture.ReleaseTemporary(rt); throw; }
        }
        outgoing ??= Allocate(); incoming ??= Allocate(); composite ??= Allocate();
    }

    private static void Capture(RenderTexture target, MeshDrawFrame frame)
    {
        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var previous = RenderTexture.active;
        try
        {
            captureCamera!.CopyFrom(Find.Camera);
            captureCamera.enabled = false;
            captureCamera.targetTexture = target;
            // Explicit Camera.Render omits persistent section draws (including lighting).
            // Borrow the SAME final items as the main map pass; never dispose or mutate them.
            var backend = CameraTextureRenderer.Current ?? throw new InvalidOperationException("No camera renderer.");
            renderItems ??= AccessTools.Method(backend.GetType(), "RenderItems")
                .CreateDelegate<Action<RenderTexture, List<FinalDrawItem>, Camera, Rect, Color?, System.Numerics.Vector3?>>(backend);
            sceneItems.Clear();
            sceneItems.AddRange(frame.Draws);
            sceneItems.AddRange(frame.PersistentDraws);
            sceneItems.Sort(DrawSortKeyComparer.Instance);
            var bg = frame.BackgroundColor;
            renderItems(target, sceneItems, captureCamera, new Rect(0, 0, 1, 1), new Color(bg.X,bg.Y,bg.Z,1), frame.SunDirection);
            if (target.GpuTexture == null) throw new InvalidOperationException("Camera produced no GPU texture.");
            SceneCaptures++;
        }
        finally { sceneItems.Clear(); captureCamera!.targetTexture = null; RenderTexture.active = previous; }
        LastCaptureMs = System.Diagnostics.Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
    }

    private static Rect UV(float scale, float down)
    {
        var uv = LayerTransitionMath.UV(scale, down);
        return new Rect(uv.X, uv.Y, uv.Width, uv.Height);
    }
    private static void ConfigureMaterial(Material material, Texture texture, Rect uv, float alpha)
    {
        material.mainTexture = texture;
        material.mainTextureOffset = new Vector2(uv.x, uv.y);
        material.mainTextureScale = new Vector2(uv.width, uv.height);
        material.color = new Color(1, 1, 1, alpha);
    }
    private static void Compose()
    {
        var camera = captureCamera!;
        camera.cullingMask = 0; // Only the two immediate quads, never the pending map commands.
        camera.orthographic = true; camera.orthographicSize = .5f;
        camera.aspect = (float)width / height;
        camera.nearClipPlane = .1f; camera.farClipPlane = 20;
        camera.transform.position = new Vector3(0, 10, 0);
        camera.transform.rotation = Quaternion.Euler(90, 0, 0);
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
        camera.rect = new Rect(0, 0, 1, 1); camera.targetTexture = composite;
        ConfigureMaterial(baseMaterial!, incoming!, UV(sample.NewScale, sample.NewDown), 1);
        ConfigureMaterial(fadeMaterial!, outgoing!, UV(sample.OldScale, sample.OldDown), sample.OldAlpha);
        var previous = RenderTexture.active;
        // Waylith's explicit Camera.Render collects DrawMeshNow inside camera callbacks.
        Camera.onPostRender += CompositeQuads;
        try { camera.Render(); Compositions++; }
        finally { Camera.onPostRender -= CompositeQuads; camera.targetTexture = null; RenderTexture.active = previous; }
    }
    private static void CompositeQuads(Camera camera)
    {
        if (camera != captureCamera) return;
        var matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3((float)width / height, 1, 1));
        baseMaterial!.SetPass(0); Graphics.DrawMeshNow(MeshPool.plane10, matrix);
        fadeMaterial!.SetPass(0); Graphics.DrawMeshNow(MeshPool.plane10, matrix);
    }

    internal static void OnGUI()
    {
        if (!Active) return;
        if (!ContextValid()) { Cancel(); return; }
        if (!RimLayersMod.Settings.LayerTransitions) { FinishForInput(); return; }
        var ev = Event.current;
        if (ev.type is EventType.MouseDown or EventType.MouseDrag or EventType.ScrollWheel)
        {
            if (!(ev.button == 0 && ev.type != EventType.ScrollWheel && LayerView.MouseOverFloorControls(owner!)))
                FinishForInput();
        }
        if (!HasPicture || ev.type != EventType.Repaint) return;
        var color = GUI.color;
        try
        {
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(0, 0, UI.screenWidth, UI.screenHeight), blending ? composite! : outgoing!, ScaleMode.StretchToFill, false);
        }
        finally { GUI.color = color; }
    }
}

[HarmonyPatch]
internal static class LayerTransitionDrawPatch
{
    static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("RimWorldWaylith.Rendering.MapRenderFeature"), "CaptureFrame");
    [HarmonyPriority(Priority.Last)]
    static void Postfix(object? __result)
    {
        if (__result is MeshDrawFrame frame) LayerTransition.AfterDraw(frame);
    }
}
[HarmonyPatch(typeof(Root), nameof(Root.Update))]
internal static class LayerTransitionLifetimePatch
{
    static void Postfix() => LayerTransition.ValidateContext();
}
[HarmonyPatch(typeof(CameraDriver), "CalculateCurInputDollyVect")]
internal static class TransitionDollyPatch
{
    static void Postfix(Vector2 __result) { if (__result != Vector2.zero && LayerTransition.Active) LayerTransition.FinishForInput(); }
}
[HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CameraDriverOnGUI))]
internal static class TransitionZoomPatch
{
    static void Prefix()
    {
        if (LayerTransition.Active && (Event.current.type == EventType.ScrollWheel ||
            KeyBindingDefOf.MapZoom_In.KeyDownEvent || KeyBindingDefOf.MapZoom_Out.KeyDownEvent))
            LayerTransition.FinishForInput();
    }
}
[HarmonyPatch]
internal static class TransitionCameraJumpPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CameraDriver), nameof(CameraDriver.SetRootPosAndSize));
        yield return AccessTools.Method(typeof(CameraDriver), nameof(CameraDriver.PanToMapLocAndSize));
    }
    static void Prefix() { if (!LayerTransition.Committing) LayerTransition.Cancel(); }
}
[HarmonyPatch]
internal static class TransitionLabelsPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ThingOverlays), nameof(ThingOverlays.ThingOverlaysOnGUI));
        yield return AccessTools.Method(typeof(GenMapUI), nameof(GenMapUI.DrawThingLabel), new[] { typeof(Vector2), typeof(string), typeof(Color) });
    }
    static bool Prefix() => !LayerTransition.HasPicture || Event.current.type != EventType.Repaint;
}
