using System.Collections.Generic;

namespace XingyiStarry.Mp.Protocol;

public static class CommandRules
{
    public const int MaxResolvedActions = 10;

    public static bool MayRequest(CommandKind kind) => kind is
        CommandKind.Move or CommandKind.Action or CommandKind.EquipmentAction or CommandKind.EquipmentMoveAction or
        CommandKind.BuildWithMove or CommandKind.Skill or CommandKind.UndoMove or CommandKind.EndTurn or
        CommandKind.ResolvedUnitActions or CommandKind.Surrender or CommandKind.ToggleStandby or CommandKind.ToggleSleep or
        CommandKind.SelfDestruct;

    public static bool MayBroadcast(CommandKind kind) => kind != CommandKind.AutoGuideStart && kind != CommandKind.Stay && (int)kind != 10 &&
        System.Enum.IsDefined(typeof(CommandKind), kind);

    public static string? ValidateResolvedActions(GameCommand command)
    {
        if (command.Kind is CommandKind.BuildWithMove or CommandKind.EquipmentMoveAction)
        {
            if (command.UnitIds.Length != 1 || command.UnitTargetXs.Length != 1 || command.UnitTargetYs.Length != 1)
                return "A move action requires one unit and an explicit movement position.";
        }
        if (command.Kind != CommandKind.ResolvedUnitActions)
            return command.ResolvedActions.Count == 0 ? null : "Only resolved batches may contain resolved actions.";
        if (command.ResolvedActions.Count == 0 || command.ResolvedActions.Count > MaxResolvedActions ||
            command.UnitIds.Length != command.ResolvedActions.Count) return "Invalid resolved action count.";
        var units = new HashSet<long>();
        for (var index = 0; index < command.ResolvedActions.Count; index++)
        {
            var action = command.ResolvedActions[index];
            if (action.UnitId != command.UnitIds[index] || !units.Add(action.UnitId)) return "Resolved action unit order is invalid.";
            if (!System.Enum.IsDefined(typeof(ResolvedActionType), action.Type)) return "Unknown resolved action type.";
            if (command.ResolvedActions.Count > 1 && action.Type != ResolvedActionType.Move)
                return "Only pure movement may execute as a parallel native batch.";
            if (action.Type == ResolvedActionType.Create && action.TemplateId.Length == 0) return "Resolved creation has no template.";
            if (action.Type == ResolvedActionType.Unload && action.PassengerUnitId == 0) return "Resolved unload has no passenger.";
        }
        return null;
    }
}
