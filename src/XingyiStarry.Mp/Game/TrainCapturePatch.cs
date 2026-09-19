using HarmonyLib;
using ANNW;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(SS_ANNW_Game), "TryAutoTrainUnit")]
internal static class TrainCapturePatch
{
    private static bool Prefix(UnitTemplate template)
    {
        if (InputGate.ShouldRunOriginal) return true;
        if (!InputGate.MaySubmit || template is null || GS_Battle.self is null) return false;
        foreach (var unit in GS_Battle.self.sel_group_units)
        {
            if (!LocalCommandUnits.Owned(unit) || unit.actioned || unit.in_animation || unit.GetAction(ActionCate.TRAIN) is not Action_TrainUnit action) continue;
            action.train_template = template;
            if (action.CanDoAction(null, null) != REASON_CANTDO.OK || !action.CanAfford(null)) continue;
            var target = action.AutoSetPos();
            if (!target.HasValue) continue;
            XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand
            {
                Kind = CommandKind.Action,
                UnitIds = new long[] { unit.unit_id },
                TargetX = target.Value.x,
                TargetY = target.Value.y,
                ActionCategory = (int)ActionCate.TRAIN,
                TemplateId = template.sd_unit.name
            });
            break;
        }
        return false;
    }
}
