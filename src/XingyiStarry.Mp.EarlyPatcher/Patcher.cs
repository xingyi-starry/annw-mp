using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace XingyiStarry.Mp.EarlyPatcher;

public static class Patcher
{
    private const string MarkerName = "XingyiStarry.Mp.NoSteam";
    public static IEnumerable<string> TargetDLLs { get { yield return "Assembly-CSharp.dll"; } }

    public static void Initialize() => Console.WriteLine("[XingyiStarry.Mp.EarlyPatcher] NoSteam=" + IsEnabled());

    public static void Patch(AssemblyDefinition assembly)
    {
        if (!IsEnabled()) { Console.WriteLine("[XingyiStarry.Mp.EarlyPatcher] skipped; marker absent"); return; }
        PatchSteamInit(assembly);
        PatchPreMenu(assembly);
    }

    private static bool IsEnabled() => File.Exists(Path.Combine(Paths.GameRootPath, MarkerName));

    private static void PatchSteamInit(AssemblyDefinition assembly)
    {
        var type = assembly.MainModule.Types.FirstOrDefault(value => value.Name == "SteamInterface") ?? throw new InvalidOperationException("SteamInterface type not found.");
        var method = type.Methods.FirstOrDefault(value => value.Name == "Init" && !value.IsStatic && value.HasBody) ?? throw new InvalidOperationException("SteamInterface.Init not found.");
        var initialized = type.Fields.FirstOrDefault(value => value.Name == "steam_inited") ?? throw new InvalidOperationException("SteamInterface.steam_inited not found.");
        method.Body.Instructions.Clear(); method.Body.ExceptionHandlers.Clear(); method.Body.Variables.Clear();
        var il = method.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Ldc_I4_0)); il.Append(il.Create(OpCodes.Stfld, initialized)); il.Append(il.Create(OpCodes.Ret));
        Console.WriteLine("[XingyiStarry.Mp.EarlyPatcher] SteamInterface.Init disabled");
    }

    private static void PatchPreMenu(AssemblyDefinition assembly)
    {
        var type = assembly.MainModule.Types.FirstOrDefault(value => value.Name == "SS_ANNW_PreMenu") ?? throw new InvalidOperationException("SS_ANNW_PreMenu type not found.");
        var method = type.Methods.FirstOrDefault(value => value.Name == "Awake" && !value.IsStatic && value.HasBody) ?? throw new InvalidOperationException("SS_ANNW_PreMenu.Awake not found.");
        var useSteam = type.Fields.FirstOrDefault(value => value.Name == "use_steam") ?? throw new InvalidOperationException("SS_ANNW_PreMenu.use_steam not found.");
        var first = method.Body.Instructions[0]; var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0)); il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0)); il.InsertBefore(first, il.Create(OpCodes.Stfld, useSteam));
        Console.WriteLine("[XingyiStarry.Mp.EarlyPatcher] SS_ANNW_PreMenu.use_steam forced false");
    }
}
