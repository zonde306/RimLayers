using RimLayers.Presentation;
int checks=0;
void Check(bool condition,string name){checks++;if(!condition)throw new Exception(name);}
void Near(float a,float b,string name)=>Check(Math.Abs(a-b)<.00001f,name);

// No floor below: native value must survive untouched, so single-layer maps keep vanilla behaviour.
foreach(float height in new[]{15f,40f,65f,80f,120f})
{
    Near(CameraDepthMath.Far(height,0),CameraDepthMath.NativeFar,"Zero floors below keeps native far plane");
    Near(CameraDepthMath.Far(height,-1),CameraDepthMath.NativeFar,"Negative floor count is not projection");
}

// The reported bug: max zoom (65) with one projected floor puts the backdrop at -3 and native far
// plane 65.5 clips everything below -0.5, i.e. the whole background disappears.
float nativeBottom=CameraDepthMath.VisibleBottom(CameraDepthMath.MaxCameraHeight,CameraDepthMath.NativeFar);
Check(nativeBottom>=-1f,"Native frustum bottoms out just under the ground plane");
Check(!CameraDepthMath.SeesFace(CameraDepthMath.MaxCameraHeight,CameraDepthMath.NativeFar,1),"Native far plane clips the first projected floor at max zoom");
Check(CameraDepthMath.SeesFace(CameraDepthMath.MaxCameraHeight,CameraDepthMath.NativeFar,0),"Ground floor itself is still inside the native frustum");

// With the patched far plane every floor the layout can hold is inside the frustum at any height,
// for the full elevation range LayerLayout allows (-9..9).
for(int floors=1;floors<=9;floors++)
foreach(float height in new[]{15f,40f,65f,80f,120f})
{
    float far=CameraDepthMath.Far(height,floors);
    Check(far>=CameraDepthMath.NativeFar,"Patched far plane never narrows the vanilla frustum");
    Check(CameraDepthMath.SeesFace(height,far,floors),"Deepest projected floor is visible");
    for(int depth=1;depth<=floors;depth++)
        Check(CameraDepthMath.SeesFace(height,far,depth),$"Floor depth {depth}/{floors} inside the frustum");
    Check(CameraDepthMath.VisibleBottom(height,far)<=CameraDepthMath.FaceY(floors)-CameraDepthMath.Slack+1e-3f,"Slack covers the deepest face");
}

// Monotonic: more floors below never shrinks the far plane, and it never shrinks with camera height
// (below the clamp floor the native value wins, so this is non-decreasing rather than strictly).
for(int floors=1;floors<9;floors++)
    Check(CameraDepthMath.Far(65f,floors+1)>CameraDepthMath.Far(65f,floors),"More floors below extends the far plane");
for(float height=15f;height<120f;height+=.25f)
    Check(CameraDepthMath.Far(height+1,3)>=CameraDepthMath.Far(height,3),"Raising the camera never narrows the far plane");

// Regression guard on the exact reported numbers.
Near(CameraDepthMath.Far(CameraDepthMath.MaxCameraHeight,1),68f+CameraDepthMath.Slack,"Max zoom with one floor below");
Near(CameraDepthMath.FaceY(1),-3f,"One floor projection depth");
Near(CameraDepthMath.FaceY(2),-6f,"Two floor projection depth");

Console.WriteLine($"PASS: {checks} camera frustum assertions.");
