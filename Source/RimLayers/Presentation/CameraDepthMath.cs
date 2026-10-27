namespace RimLayers.Presentation;

/// <summary>
/// Pure camera-frustum rules. Split out of CameraDepth so the arithmetic is testable without the
/// game assembly: the bug this fixes is a numeric one.
/// </summary>
public static class CameraDepthMath
{
    /// <summary>Native far plane, set once in CameraDriver.Awake and never refreshed.</summary>
    public const float NativeFar=65.5f;
    /// <summary>Depth of one projected lower floor; every backdrop draw sinks by depth*3.</summary>
    public const float FaceDrop=3f;
    /// <summary>Margin past the deepest projected face, so culling still keeps tall things.</summary>
    public const float Slack=40f;
    /// <summary>Camera altitude at maximum zoom: ApplyPositionToGameObject uses 15 + t*50 with t = 1.</summary>
    public const float MaxCameraHeight=65f;

    /// <summary>
    /// Far plane that contains every projected floor below a camera at <paramref name="cameraHeight"/>.
    /// Zero floors below means nothing is projected, so the native value is returned untouched.
    /// </summary>
    public static float Far(float cameraHeight,int floorsBelow)
    {
        if(floorsBelow<=0)return NativeFar;
        float deepest=cameraHeight+floorsBelow*FaceDrop;
        return Math.Max(NativeFar,deepest+Slack);
    }

    /// <summary>Lowest world y the camera can still see: an orthographic top-down camera shows y >= h - far.</summary>
    public static float VisibleBottom(float cameraHeight,float far)=>cameraHeight-far;

    /// <summary>World y of the face one floor <paramref name="depth"/> floors below the viewed layer.</summary>
    public static float FaceY(int depth)=>-depth*FaceDrop;

    /// <summary>True when the projected face below the viewed layer survives the camera's far clip.</summary>
    public static bool SeesFace(float cameraHeight,float far,int depth)=>FaceY(depth)>=VisibleBottom(cameraHeight,far);
}
