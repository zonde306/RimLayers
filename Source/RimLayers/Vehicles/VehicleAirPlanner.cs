namespace RimLayers.Vehicles;

public readonly record struct VehicleFlightEnvelope(double Speed,double Climb,double Descent,double TurnRate,bool FixedWing,double MaximumBank=45,double NominalSpeed=0,double PoweredSpeed=0,double VerticalSpeed=0,double ClimbAcceleration=0,double DescentAcceleration=0);
public readonly record struct VehicleAirGuidance(VehiclePoint Aim,double Heading,double TargetHeight);

/// <summary>Receding-horizon guidance over an immutable airspace snapshot. The provider
/// integrates the actual controls; this planner never turns or teleports an aircraft.</summary>
public static class VehicleAirPlanner
{
    public static VehicleAirGuidance? Choose(VehicleHull hull,RampBody current,VehiclePoint goal,VehicleFlightEnvelope envelope,IReadOnlyList<VehicleObstacle> obstacles)
    {
        double direct=Angle(goal.X-current.Bottom.X,goal.Z-current.Bottom.Z);
        double speed=Math.Max(.5,envelope.Speed),horizon=Math.Clamp(2+speed/Math.Max(1,envelope.TurnRate*Math.PI/180),3,5);
        double distance=Math.Sqrt(Math.Pow(goal.X-current.Bottom.X,2)+Math.Pow(goal.Z-current.Bottom.Z,2));
        double best=double.PositiveInfinity;VehicleAirGuidance? result=null;
        var altitudes=new[]{goal.Height,current.Bottom.Height,current.Bottom.Height+3,current.Bottom.Height+6,current.Bottom.Height-3};
        foreach(double offset in new[]{0d,-30,30,-60,60,-90,90,-135,135,180})
        foreach(double altitude in altitudes)
        {
            double yaw=(direct+offset+360)%360,travel=envelope.FixedWing?speed*horizon:Math.Min(distance,speed*horizon);
            if(Math.Abs(offset)>1)travel=Math.Max(travel,speed*horizon*.7);
            var aim=new VehiclePoint(current.Bottom.X+Math.Sin(yaw*Math.PI/180)*travel,altitude,current.Bottom.Z+Math.Cos(yaw*Math.PI/180)*travel);
            var previous=current with{Pitch=0,Bank=0};bool clear=true;double lastYaw=current.Yaw;
            double closest=double.PositiveInfinity,roll=current.Bank*180/Math.PI,velocity=envelope.VerticalSpeed,airspeed=speed;
            for(int step=1;step<=60;step++)
            {
                double dt=horizon/60;
                double delta=VehicleGeometry.SignedTurn(lastYaw,yaw);
                if(envelope.FixedWing&&envelope.NominalSpeed>0)
                {
                    // Snapshot model of the provider's bounded bank/heading response. A turn
                    // rate alone incorrectly predicts instant bank reversal and cuts corners.
                    double maxBank=envelope.MaximumBank,desiredBank=Math.Clamp(delta/72*maxBank,-maxBank,maxBank);
                    double alignment=Math.Clamp(1-Math.Abs(delta)/120,0,1);
                    airspeed=Move(airspeed,envelope.PoweredSpeed*(.76+.24*alignment*alignment),envelope.NominalSpeed*.45*dt);
                    double capture=5+25*Math.Clamp(Math.Abs(roll)/maxBank,0,1);
                    if(Math.Abs(delta)<capture)desiredBank*=Math.Abs(delta)/capture;
                    roll=Move(roll,desiredBank,maxBank*.92*dt);
                    double turn=envelope.TurnRate*Math.Clamp(Math.Tan(Math.Abs(roll)*Math.PI/180)/Math.Max(.05,Math.Tan(maxBank*Math.PI/180)),0,1)*Math.Clamp(1/Math.Max(.45,airspeed/envelope.NominalSpeed),.58,1.28);
                    double change=Math.Sign(roll)*turn*dt;
                    if(Math.Sign(roll)==Math.Sign(delta))change=Math.Sign(change)*Math.Min(Math.Abs(change),Math.Abs(delta));
                    lastYaw+=change;
                }
                else lastYaw+=Math.Clamp(delta,-envelope.TurnRate*dt,envelope.TurnRate*dt);
                double vertical=altitude-previous.Bottom.Height;
                double rate=vertical>=0?envelope.Climb:envelope.Descent;
                double acceleration=vertical>=0?envelope.ClimbAcceleration:envelope.DescentAcceleration;
                double height;
                if(acceleration>0)
                {
                    velocity=Move(velocity,Math.Sign(vertical)*Math.Min(rate,Math.Sqrt(2*acceleration*Math.Abs(vertical))),acceleration*dt);
                    double change=velocity*dt;
                    if(change*vertical>=0&&Math.Abs(change)>=Math.Abs(vertical)){height=altitude;velocity=0;}
                    else height=previous.Bottom.Height+change;
                }
                else height=previous.Bottom.Height+Math.Clamp(vertical,-envelope.Descent*dt,envelope.Climb*dt);
                double move=envelope.FixedWing?airspeed*dt:Math.Min(travel/60,Distance(previous.Bottom,aim));
                double direction=(envelope.FixedWing?lastYaw:yaw)*Math.PI/180;
                var next=new RampBody(new(previous.Bottom.X+Math.Sin(direction)*move,height,previous.Bottom.Z+Math.Cos(direction)*move),lastYaw,0,0);
                if(VehicleFlightSweep.FirstHit(hull,previous,next,obstacles)!=null){clear=false;break;}
                closest=Math.Min(closest,Distance(next.Bottom,goal)+Math.Abs(next.Bottom.Height-goal.Height)*1.8+step*.002);
                previous=next;
            }
            if(!clear)continue;
            // A fixed wing must fly THROUGH its goal, not optimize where it will be long
            // after passing it. Endpoint-only cost causes an endless orbit around close goals.
            double score=(envelope.FixedWing?closest+Distance(previous.Bottom,goal)*.01:Distance(previous.Bottom,goal)+Math.Abs(previous.Bottom.Height-goal.Height)*1.8)+Math.Abs(offset)*.015;
            if(score<best){best=score;result=new(aim,yaw,altitude);}
        }
        // Only rotorcraft can hold position when a clear route temporarily disappears.
        if(result==null&&!envelope.FixedWing&&VehicleFlightSweep.FirstHit(hull,current,current,obstacles)==null)
            result=new(current.Bottom,current.Yaw,current.Bottom.Height);
        return result;
    }
    private static double Move(double value,double target,double amount)=>value+Math.Clamp(target-value,-amount,amount);
    private static double Angle(double x,double z)=>(Math.Atan2(x,z)*180/Math.PI+360)%360;
    private static double Distance(VehiclePoint a,VehiclePoint b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Z-b.Z,2));
}
