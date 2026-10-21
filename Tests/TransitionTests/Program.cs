using RimLayers.Presentation;
int checks=0;
void Check(bool condition,string name){checks++;if(!condition)throw new Exception(name);}
void Near(float a,float b,string name)=>Check(Math.Abs(a-b)<.00001f,name);
Near(LayerTransitionMath.Duration,.32f,"320 ms");
Near(LayerTransitionMath.Ease(-1),0,"Before start");
Near(LayerTransitionMath.Ease(1),1,"After end");
Near(LayerTransitionMath.Ease(.16f),.5f,"SmoothStep midpoint");
foreach(var direction in new[]{-1,1})
{
 var start=LayerTransitionMath.Sample(0,direction);
 var end=LayerTransitionMath.Sample(.32f,direction);
 Near(start.OldScale,1,"Old starts aligned");Near(start.OldDown,0,"Old starts stationary");
 Near(start.OldAlpha,1,"Old starts opaque");Near(end.OldAlpha,0,"Old ends transparent");
 Near(end.NewScale,1,"Target ends at actual zoom");Near(end.NewDown,0,"Target ends at actual center");
 Near(end.OldScale,1-.04f*direction,"Departure scale");
 Near(start.NewScale,1+.04f*direction,"Arrival scale");
 Near(end.OldDown,.01f*direction,"Departure direction");
 Near(start.NewDown,-.01f*direction,"Arrival direction");
 float alpha=1;
 for(int frame=0;frame<=100;frame++)
 {
  var s=LayerTransitionMath.Sample(frame*.0032f,direction);
  Check(s.OldAlpha<=alpha+.00001f,"Monotonic fade");alpha=s.OldAlpha;
  var uv=LayerTransitionMath.UV(s.NewScale,s.NewDown);
  Near(uv.Width,uv.Height,"Isotropic scaling");
  Near(uv.X+uv.Width*.5f,.5f,"Centered horizontal sampling");
  Check(float.IsFinite(uv.Y)&&uv.Width>0,"Finite UV");
 }
}
foreach(int a in new[]{-9,-1,0,1,9})foreach(int b in new[]{-9,-1,0,1,9})
 Check(LayerTransitionMath.Direction(a,b)==Math.Sign(b-a),"Physical elevation controls direction");
Console.WriteLine($"PASS: {checks} transition math assertions.");
