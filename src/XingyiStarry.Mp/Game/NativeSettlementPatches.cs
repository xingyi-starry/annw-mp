using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ANNW;
using HarmonyLib;
using XingyiStarry.Mp.Protocol.Wire;
using XingyiStarry.Mp.Session;
using Event = XingyiStarry.Mp.Protocol.Wire.SettlementEvent;

namespace XingyiStarry.Mp.Game;

// Save/load can reorder equal-priority effects. Keep the native filter
// calculations, but invoke the host's ordered filters at Hurt's callback point.
[HarmonyPatch(typeof(UnitData), "Hurt")]
internal static class SettlementDamageFiltersPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(UnitEffect), nameof(UnitEffect.PreDamage));
        var replacement = AccessTools.Method(typeof(SettlementDamageFiltersPatch), nameof(Filter));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original)) { instruction.opcode = System.Reflection.Emit.OpCodes.Call; instruction.operand = replacement; }
            yield return instruction;
        }
    }
    private static float Filter(UnitEffect effect, float damage, UnitData attacker, bool preview)
    {
        var operation = AttackSettlements.Current;
        if (!preview && operation?.Replaying == true && operation.ActiveCall?.Value.Damage is not null)
            return operation.ActiveCall.DamageFilters(damage);
        return effect.PreDamage(damage, attacker, preview);
    }
}

// LethalSave's damage rule remains native. Only its escape candidate is a
// host decision; a client's still-playing attacker movement cannot change it.
[HarmonyPatch(typeof(UnitEffect_LethalSave), nameof(UnitEffect_LethalSave.PreDamage))]
internal static class SettlementEscapeChoicePatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var neighbors = AccessTools.Method(typeof(Passability), nameof(Passability.GetNB), new[] { typeof(Inctor2) });
        var distance = AccessTools.Method(typeof(HexLogic), nameof(HexLogic.CubicDistance), new[] { typeof(Inctor2), typeof(Inctor2) });
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(neighbors)) { instruction.opcode = System.Reflection.Emit.OpCodes.Call; instruction.operand = AccessTools.Method(typeof(SettlementEscapeChoicePatch), nameof(Candidates)); }
            else if (instruction.Calls(distance)) { instruction.opcode = System.Reflection.Emit.OpCodes.Call; instruction.operand = AccessTools.Method(typeof(SettlementEscapeChoicePatch), nameof(Distance)); }
            yield return instruction;
        }
    }
    private static Event? Escape => AttackSettlements.Current?.Replaying == true && AttackSettlements.Current.ActiveCall?.Value.Effect?.EffectType == nameof(UnitEffect_LethalSave)
        ? AttackSettlements.Current.ActiveCall.Value : null;
    private static IEnumerable<Inctor2> Candidates(Inctor2 origin)
    {
        var choice = Escape;
        if (choice is null) return Passability.GetNB(origin);
        var destination = new Inctor2(choice.Effect.X, choice.Effect.Y);
        return destination == origin ? new List<Inctor2>() : new List<Inctor2> { destination };
    }
    private static int Distance(Inctor2 from, Inctor2 to)
    {
        var choice = Escape;
        return choice is null ? HexLogic.CubicDistance(from, to) : to == new Inctor2(choice.Effect.X, choice.Effect.Y) ? 1 : 0;
    }
}

[HarmonyPatch]
internal static class NativeSettlementPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in new[] {
            AccessTools.Method(typeof(UnitData),"Hurt"), AccessTools.Method(typeof(UnitData),"Die"),
            AccessTools.Method(typeof(UnitData),"Heal"), AccessTools.Method(typeof(UnitData),"AddUnitEffect"),
            AccessTools.Method(typeof(UnitData),"AddExp"), AccessTools.Method(typeof(UnitData),"AddExp_BasePercent"), AccessTools.Method(typeof(UnitData),"CheckAfterAction"),
            AccessTools.Method(typeof(GameAPI),"CreateUnit"), AccessTools.Method(typeof(GameAPI),"DescendUnit"),
            AccessTools.Method(typeof(UnitData),"TriggerDieEvent"), AccessTools.Method(typeof(Player),"DoWipeOut"),
            AccessTools.Method(typeof(Player),"AddMetalPower"), AccessTools.Method(typeof(Player),"AutoSetCmdPos"),
            AccessTools.Method(typeof(GameTileData),"CreateWreck"), AccessTools.Method(typeof(WP_Shield),"AbsorbDam"),
            AccessTools.Method(typeof(EffectHost),"Remove"), AccessTools.Method(typeof(ActionData),"OnUnitActionEnd"),
            AccessTools.Method(typeof(CO_Data),"AfterSkillCast") }) if (method is not null) yield return method;
        // Explicit native decision/filter boundaries. New game classes are not
        // automatically intercepted just because a method happens to be On*.
        foreach (var entry in new[] {
            (typeof(UnitEffect_ShockAura), "OnEvent"), (typeof(UnitEffect_ShockChain), "OnEvent"),
            (typeof(UnitEffect_CounterAtk), "OnEvent"), (typeof(UnitEffect_ExpOnSuicide), "OnEvent"),
            (typeof(UnitEffect_GenUnitOnDie), "OnEvent"), (typeof(UnitEffect_DieAfterAtk), "OnActionEnd"),
            (typeof(UnitEffect_OverHeat), "OnRepair"), (typeof(UnitEffect_OverHeat), "OnEvent"),
            (typeof(UnitEffect_AtkCostHP), "OnEvent"), (typeof(UnitEffect_AtkCostRes), "OnAtkEnd"),
            (typeof(UnitEffect_AtkCostRes), "PreAttack"), (typeof(UnitEffect_PushBack), "PreAttack"),
            (typeof(UnitEffect_PushBack), "OnEvent"), (typeof(UnitEffect_ReactiveRepair), "PreDamage"),
            (typeof(UnitEffect_ReactiveRepair), "OnEvent"), (typeof(UnitEffect_Block), "PreDamage"),
            (typeof(UnitEffect_PhaseShield), "PreDamage"), (typeof(UnitEffect_LethalSave), "PreDamage"),
            (typeof(UnitEffect_ShareExp), "PreAddExp") })
            yield return AccessTools.Method(entry.Item1, entry.Item2);
    }

    private static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args, out AttackSettlements.Call? __state)
    {
        __state = null;
        var operation = AttackSettlements.Current;
        if (operation is null || !ExecutionContext.IsAuthoritativeExecution) return true;
        if (operation.Replaying && NativeActionPlayback.IsPresenting) return false;
        // These deterministic native writes belong to their enclosing action/rule.
        // Record them only when a host-only selector must send concrete child steps.
        if (__originalMethod.Name is "Heal" or "AddUnitEffect" or "CreateUnit" or "AddMetalPower" or "AbsorbDam" or "Remove")
        {
            if (operation.ActiveCall is not null && !operation.SelectingDerivedSteps && !operation.ApplyingExplicitEvent) return true;
        }
        var parameters = __originalMethod.GetParameters();
        if (parameters.Select((p, i) => p.Name == "preview" && __args[i] is true).Any(x => x)) return true;
        if (operation.Replaying && operation.SuppressDeathTail && (__originalMethod.Name == "DescendUnit" || __originalMethod.Name == "TriggerDieEvent")) return false;
        var value = Describe(__instance, __originalMethod, __args);
        if (operation.ReplayCallbacks(value)) return false;
        if (!operation.Enter(value, out __state)) return false;
        if (!operation.Replaying) return true;
        value = __state!.Value;
        if (value.Pulse is not null || value.Effect is not null && (__instance is UnitEffect_ShockChain || __instance is UnitEffect_CounterAtk || __instance is UnitEffect_ExpOnSuicide || __instance is UnitEffect_GenUnitOnDie))
        { __state.Children(); AttackSettlements.EffectVisual(value); return false; }
        if (value.PlayerDefeated is not null)
        {
            var player = (Player)__instance; player.defeated = true;
            BattleEventBus.self.TriggerPlayerDefeat(player);
            SingletonMono<SS_ANNW_Game>.self.ui.messages.AddMessage(string.Format(LAN.Get("MSG_PlayerDefeat"), player.index + 1));
            __state.Children(); return false;
        }
        // Nested native calculations consume the exact input belonging to the host event.
        if (value.Damage is not null) __args[0] = value.Damage.Damage;
        if (value.Heal is not null) __args[0] = value.Heal.Amount;
        return true;
    }
    private static Exception? Finalizer(Exception? __exception, AttackSettlements.Call? __state, object __instance)
    {
        if (__state is null) return __exception;
        if (__exception is not null) __state.Fail(__exception);
        try
        {
            if (!__state.Owner.Replaying)
            {
                if (__instance is UnitEffect_LethalSave escape && __state.Value.Effect is not null)
                {
                    __state.Value.Effect.X = escape.owner.pos.x; __state.Value.Effect.Y = escape.owner.pos.y;
                }
                CaptureSpawnFlags(__state.Value);
            }
            __state.Dispose();
        }
        catch (Exception ex) { return __exception ?? ex; }
        return __exception;
    }
    private static void CaptureSpawnFlags(Event value)
    {
        if (value.Spawn is not null && value.TargetUnitId != 0)
        {
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)value.TargetUnitId);
            value.Spawn.Moved = unit.moved; value.Spawn.Actioned = unit.actioned;
        }
        foreach (var child in value.Children) CaptureSpawnFlags(child);
    }
    internal static Event Describe(object instance, MethodBase method, object[] args)
    {
        var e = new Event(); string name = method.Name;
        if (instance is UnitData unit) e.TargetUnitId = unit.unit_id;
        if (instance is Player player) e.PlayerIndex = player.index;
        if (instance is ActionData action)
        {
            e.SourceUnitId = action.owner?.unit_id ?? 0; e.PlayerIndex = action.player.index;
            if (name == "OnUnitActionEnd") { e.AttackFinished = new AttackFinishedEvent { ActionName = action.sd_action.name }; return e; }
            throw new InvalidOperationException("No native action boundary for " + method);
        }
        if (instance is UnitEffect effect)
        {
            e.TargetUnitId = effect.owner.unit_id;
            if (effect is UnitEffect_ShockAura && name == "OnEvent")
            { e.Pulse = new PulseEvent { EffectIndex = effect.owner.effect_host.effects.OfType<UnitEffect_ShockAura>().TakeWhile(x => x != effect).Count() }; return e; }
            var f = new EffectEvent
            {
                EffectName = effect.sd_effect.name,
                EffectType = effect.GetType().Name,
                Callback = name,
                EffectIndex = EffectIndex(effect)
            };
            var pars = method.GetParameters();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] is UnitData other) f.OtherUnitId = other.unit_id;
                else if (args[i] is float number) { if (pars[i].Name == "damage") f.Damage = number; else f.Value = number; }
                else if (args[i] is bool flag) { if (pars[i].Name == "preview") f.Preview = flag; else f.Killed = flag; }
                else if (args[i] is DieReason reason) f.Reason = (int)reason;
                else if (args[i] is Inctor2 pos) { f.X = pos.x; f.Y = pos.y; }
            }
            e.Effect = f; return e;
        }
        switch (name)
        {
            case "Hurt":
                var damage = new DamageEvent { Damage = (float)args[0], ActionName = (args[2] as ActionData)?.sd_action.name ?? "", UsePredamage = (bool)args[4], DoRandom = (bool)args[5], NoKill = (bool)args[6] };
                e.SourceUnitId = (args[1] as UnitData)?.unit_id ?? 0;
                if (args[2] is ActionData damageAction) e.PlayerIndex = damageAction.player.index;
                if (args[3] is Inctor2 center) { damage.CenterX = center.x; damage.CenterY = center.y; }
                e.Damage = damage; break;
            case "Die":
                var death = new DeathStartedEvent { Reason = (int)(DieReason)args[0], Damage = (float)args[1], ActionName = (args[3] as ActionData)?.sd_action.name ?? "", GenerateWreck = (bool)args[4] };
                e.SourceUnitId = (args[2] as UnitData)?.unit_id ?? 0;
                if (args[3] is ActionData deathAction) e.PlayerIndex = deathAction.player.index;
                if (args[5] is Inctor2 dc) { death.CenterX = dc.x; death.CenterY = dc.y; }
                e.DeathStarted = death; break;
            case "Heal": e.Heal = new HealEvent { Amount = (float)args[0] }; break;
            case "AddExp_BasePercent": e.Experience = new ExperienceEvent { Amount = (float)args[0], BasePercent = true }; break;
            case "AddExp": e.Experience = new ExperienceEvent { Amount = (float)args[0], UseFilter = (bool)args[1] }; break;
            case "AddMetalPower": e.Resource = new ResourceEvent { PlayerIndex = ((Player)instance).index, Reason = (int)(ResGetReason)args[0], Metal = (int)args[1], Power = (int)args[2] }; break;
            case "AutoSetCmdPos": e.CommanderPosition = new CommanderPositionEvent { PlayerIndex = ((Player)instance).index }; break;
            case "DoWipeOut": e.PlayerDefeated = new PlayerDefeatedEvent { PlayerIndex = ((Player)instance).index }; break;
            case "DescendUnit": e.TargetUnitId = ((UnitData)args[0]).unit_id; e.DeathCommitted = new DeathCommittedEvent { DescendOnly = true, KillerPlayer = -1 }; break;
            case "TriggerDieEvent": e.SourceUnitId = (args[1] as UnitData)?.unit_id ?? 0; e.DeathCommitted = new DeathCommittedEvent { CallbacksOnly = true, Reason = (int)(DieReason)args[0], KillerPlayer = (args[2] as Player)?.index ?? -1 }; break;
            case "CreateWreck":
                var tile = (GameTileData)instance; e.Wreck = new WreckEvent { X = tile.pos.x, Y = tile.pos.y, Amount = (int)args[0], UseRandom = (bool)args[1] }; break;
            case "AbsorbDam": e.TargetUnitId = ((WP_Shield)instance).owner.unit_id; e.Shield = new ShieldEvent { Damage = (int)args[0], Preview = (bool)args[1] }; break;
            case "CreateUnit":
                var spawn = new SpawnEvent { CreateReason = (int)(CREATE_REASON)args[0], TemplateId = ((UnitTemplate)args[1]).sd_unit.name, PlayerIndex = ((Player)args[3]).index, Building = (bool)args[4], Spawned = (bool)args[5], TriggerPassives = (bool)args[6] };
                if (args[2] is Inctor2 pos) { spawn.HasPosition = true; spawn.X = pos.x; spawn.Y = pos.y; }
                e.Spawn = spawn; break;
            case "AddUnitEffect": e.EffectMutation = new EffectMutationEvent { EffectName = ((SD_ANNW_EFFECT)args[0]).name, CasterUnitId = (args[1] as UnitData)?.unit_id ?? 0, Layer = (int)args[2], CasterPlayer = (args[3] as Player)?.index ?? -1 }; break;
            case "Remove":
                var host = (EffectHost)instance; var removed = (Effect)args[0]; e.TargetUnitId = host.owner?.unit_id ?? 0; e.PlayerIndex = host.player?.index ?? 0;
                e.EffectMutation = new EffectMutationEvent { Remove = true, EffectName = removed.sd_effect.name, EffectIndex = EffectIndex(removed) }; break;
            case "CheckAfterAction":
                var after = (ActionData)args[0]; e.SourceUnitId = ((UnitData)instance).unit_id; e.AttackFinished = new AttackFinishedEvent { ActionName = after.sd_action.name, CheckAfterAction = true }; break;
            case "AfterSkillCast": e.PlayerIndex = ((Player)AccessTools.Field(typeof(CO_Data), "player").GetValue(instance)).index; e.SkillFinished = new SkillFinishedEvent { PlayerIndex = e.PlayerIndex }; break;
            default: throw new InvalidOperationException("No native settlement adapter for " + method);
        }
        return e;
    }
    private static int EffectIndex(Effect effect) => effect.host.effects.Where(x => x.sd_effect.name == effect.sd_effect.name).TakeWhile(x => x != effect).Count();
}

[HarmonyPatch(typeof(GameAPI), nameof(GameAPI.CreateUnit))]
internal static class SettlementSpawnIdentityPatch
{
    private static void Postfix(UnitData __result)
    {
        var scope = AttackSettlements.Current?.ActiveCall;
        if (scope?.Value.Spawn is not { } spawn || __result is null) return;
        if (!scope.Owner.Replaying) scope.Value.TargetUnitId = __result.unit_id;
        else
        {
            if (scope.Value.TargetUnitId != __result.unit_id) throw new InvalidOperationException("Spawned unit id differs from authority.");
            __result.moved = spawn.Moved; __result.actioned = spawn.Actioned;
        }
    }
}

// Wrap at the DoWipeOut call site: Mono can inline the iterator factory before
// its Harmony postfix is installed, which would otherwise lose pending work.
[HarmonyPatch(typeof(Player), nameof(Player.DoWipeOut))]
internal static class SettlementWipeoutLifetimePatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(Player), "proc_DoWipeOut");
        var replacement = AccessTools.Method(typeof(SettlementWipeoutLifetimePatch), nameof(Create));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original)) { instruction.opcode = System.Reflection.Emit.OpCodes.Call; instruction.operand = replacement; }
            yield return instruction;
        }
    }
    private static IEnumerator Create(Player player)
    {
        var routine = player.proc_DoWipeOut(); var operation = AttackSettlements.Current;
        return operation is null ? routine : operation.Track(routine, () => operation.Emit(new Event { CleanupCompleted = new CleanupCompletedEvent { PlayerIndex = player.index } }));
    }
}

[HarmonyPatch(typeof(Player), "proc_DoWipeOut")]
internal static class SettlementWipeoutDriverPatch
{
    private static void Postfix(Player __instance, ref IEnumerator __result)
    {
        var operation = AttackSettlements.Current; if (operation is null) return;
        if (operation.Replaying) __result = Empty();
    }
    private static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(GameAPI), "proc_UnitDeathAnimation")]
internal static class SettlementDeathDriverPatch
{
    private static void Postfix(ref IEnumerator __result)
    { var operation = AttackSettlements.Current; if (operation is not null) __result = operation.Track(__result, deathAnimation: true); }
}

// Native DoMove starts a detached per-unit iterator. It belongs to the same
// cancellation scope as the command, including while a snapshot is loading.
[HarmonyPatch(typeof(UnitData), nameof(UnitData.DoMoveWithAni))]
internal static class SettlementMovementLifetimePatch
{
    private static void Postfix(ref IEnumerator __result)
    {
        var operation = AttackSettlements.Current;
        if (operation?.Replaying == true) __result = NativeActionPlayback.Movement(__result);
        if (operation is not null) __result = operation.Track(__result);
    }
}

[HarmonyPatch(typeof(ActionData), "proc_DoActionCell")]
internal static class SettlementDetachedCellPatch
{
    private static void Postfix(ActionData __instance, GameTileData gtd, ref IEnumerator __result)
    {
        var operation = AttackSettlements.Current; if (operation is null) return;
        if (operation.Replaying) __result = NativeActionPlayback.Presentation(__result);
        __result = operation.Track(__result);
    }
}

[HarmonyPatch(typeof(ActionData), nameof(ActionData.DoActionAni))]
internal static class SettlementActionDriverPatch
{
    private static void Postfix(ActionData __instance, GameTileData gtd, ref IEnumerator __result)
    {
        if (AttackSettlements.Current is not { } operation) return;
        if (operation.Replaying) __result = NativeActionPlayback.Play(__instance, __result);
        else if (gtd is not null) Launch(__instance, gtd);
    }
    internal static void Launch(ActionData action, GameTileData target)
    {
        AttackSettlements.Current?.Emit(new Event
        {
            SourceUnitId = action.owner?.unit_id ?? 0,
            PlayerIndex = action.player.index,
            AttackStarted = new AttackStartedEvent { ActionName = action.sd_action.name, TargetX = target.pos.x, TargetY = target.pos.y }
        });
    }
}

[HarmonyPatch(typeof(UnitData), "AutoAction")]
internal static class SettlementForcedActionPatch
{
    private static bool Prefix(UnitData __instance, ActionCate ac)
    {
        var operation = AttackSettlements.Current; if (operation is null) return true; if (operation.Replaying) return false;
        var action = __instance.GetAction(ac); var targets = new WeightedList<GameTileData>();
        foreach (var pos in (List<Inctor2>)AccessTools.Method(typeof(UnitData), "GetActionZone").Invoke(__instance, new object?[] { ac, null })) { var tile = GameTileData.IsValid(pos) ? GameTileData.Get(pos) : null; if (tile is not null && action.CanDoAction(tile) == REASON_CANTDO.OK) targets.Add(tile); }
        if (targets.items.Count == 0) return false;
        var target = targets.Get();
        operation.Emit(new Event
        {
            SourceUnitId = __instance.unit_id,
            TargetUnitId = __instance.unit_id,
            ForcedAttack = new ForcedAttackEvent { ActionCategory = (int)ac, TargetX = target.pos.x, TargetY = target.pos.y }
        });
        GameController.self.StartCoroutine(operation.Track(__instance.DoActionWithAni(target, action))); return false;
    }
}
