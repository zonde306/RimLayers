using RimLayers.Core;
using UnityEngine;
using Verse;
namespace RimLayers.Vehicles;

public sealed partial class VehicleTraffic
{
    public override void MapComponentDraw()
    {
        base.MapComponentDraw();
        if(map!=Find.CurrentMap||LayerMapComponent.For(map) is not {} owner||VehicleCompatibility.Adapter is not {} adapter)return;
        int viewed=owner.Elevation(owner.ViewedLayer);
        foreach(var trip in trips)
        {
            if(!held.Contains(trip.Vehicle)||trip.Entry?.IsLift==false||!owner.TryDecode(trip.EntryCell,out var at))continue;
            double distance=trip.CabinElevation-viewed;
            if(Math.Abs(distance)>.5)continue;
            var cell=owner.ToStorage(new(owner.ViewedLayer,at.X,at.Z));
            var pose=VehicleSpace.Pose(owner,trip.Vehicle,cell,trip.Heading);
            var origin=owner.ToStorage(new(owner.ViewedLayer,0,0));
            var point=new Vector3((float)pose.Position.X+origin.x,trip.Vehicle.def.Altitude,(float)pose.Position.Z+origin.z);
            var graphic=adapter.Visual(trip.Vehicle,point);
            // Perspective contracts the car as the cabin moves away from this stop. Only its
            // original paint/material is drawn; no proxy Pawn or duplicate inventory exists.
            float scale=1-(float)Math.Abs(distance)*.18f;
            Graphics.DrawMesh(graphic.Mesh,Matrix4x4.TRS(graphic.Position,graphic.Rotation,new Vector3(scale,1,scale)),graphic.Material,0);
        }
    }
}
