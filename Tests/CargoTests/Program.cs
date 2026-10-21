using RimLayers.Cargo;
int checks=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;}
var m=new HoistMotion();
m.Begin(1,false,0);m.Advance(179,true,180);Check(m.Moving,"manual cannot finish early");
m.Advance(1,true,180);Check(!m.Moving&&m.Position==1,"manual exactly 180 ticks");
m.Begin(-2,true,30);m.Advance(29,true,45);Check(m.Position==1,"electric setup");
m.Advance(1,true,45);Check(m.Position==1,"setup is not travel");
m.Advance(45,true,45);Check(m.Position==0,"electric one floor");
m.Advance(100,false,45);Check(m.Position==0&&m.Moving,"power loss pauses");
m.Advance(90,true,45);Check(m.Position==-2&&!m.Moving,"resume exact destination");
m.Begin(-2,true,30);Check(!m.Moving&&m.Remaining(45)==0,"same floor no travel");
m.Begin(1,false,0);m.Advance(90,true,180);float paused=m.Position;m.Advance(200,false,180);
Check(m.Position==paused,"operator interruption pauses");
m.Advance(10000,true,180);Check(m.Position==1&&!m.Moving,"large delta never overshoots");
Console.WriteLine($"Cargo motion: {checks} assertions passed.");
