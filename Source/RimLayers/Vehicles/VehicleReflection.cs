using System.Linq.Expressions;
using System.Reflection;
namespace RimLayers.Vehicles;

/// <summary>Bind once. No assembly search or private-member discovery in the simulation loop.</summary>
internal static class VehicleReflection
{
    internal static T Call<T>(MethodInfo method) where T:Delegate
    {
        var args=typeof(T).GetMethod("Invoke")!.GetParameters().Select(p=>Expression.Parameter(p.ParameterType,p.Name)).ToArray();
        int offset=method.IsStatic?0:1;
        Expression body=Expression.Call(method.IsStatic?null:Expression.Convert(args[0],method.DeclaringType!),method,
            method.GetParameters().Select((p,i)=>Expression.Convert(args[i+offset],p.ParameterType)));
        var result=typeof(T).GetMethod("Invoke")!.ReturnType;
        if(result!=typeof(void)&&body.Type!=result)body=Expression.Convert(body,result);
        return Expression.Lambda<T>(body,args).Compile();
    }
    internal static Func<object,T> Read<T>(Type type,string name)
    {
        var f=FrameworkVehicleAdapter.Field(type,name);var obj=Expression.Parameter(typeof(object));
        return Expression.Lambda<Func<object,T>>(Expression.Convert(Expression.Field(Expression.Convert(obj,f.DeclaringType!),f),typeof(T)),obj).Compile();
    }
    internal static (Func<object,T> Get,Action<object,T> Set) Field<T>(Type type,string name)
    {
        var field=FrameworkVehicleAdapter.Field(type,name);
        if(field.FieldType!=typeof(T))throw new MissingFieldException(type.FullName,name+" : "+typeof(T));
        var instance=Expression.Parameter(typeof(object),"instance");var value=Expression.Parameter(typeof(T),"value");
        var access=Expression.Field(Expression.Convert(instance,field.DeclaringType!),field);
        return (Expression.Lambda<Func<object,T>>(access,instance).Compile(),
            Expression.Lambda<Action<object,T>>(Expression.Assign(access,value),instance,value).Compile());
    }
}
