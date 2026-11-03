using System.Reflection;
using System.Runtime.Loader;
var dir = @"D:\MC-CS-Bridge\games\Counter-Strike 2\csgo\addons\counterstrikesharp\api";
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(dir, name.Name + ".dll")) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir, name.Name + ".dll")) : null;
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir, "CounterStrikeSharp.API.dll"));
var names = args.Length > 0 ? args : new[]{"CCSPlayerPawn", "CCSPlayerController", "CBaseModelEntity", "CCollisionProperty", "CBaseEntity", "CPlayer_CameraServices", "CPlayer_MovementServices", "Utilities", "VirtualFunctions", "PlayerButtons"};
foreach (var type in assembly.GetTypes().Where(t => names.Contains(t.Name) || t.Name.Contains("Trace"))) {
    Console.WriteLine("TYPE " + type.FullName);
    foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
        if (member is MethodInfo method && method.IsSpecialName) continue;
        Console.WriteLine("  " + member);
    }
}
