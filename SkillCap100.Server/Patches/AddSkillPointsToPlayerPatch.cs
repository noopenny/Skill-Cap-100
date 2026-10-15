using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace SkillCap100.Server.Patches;

/// <summary>
/// 服务端“加技能点”入口（任务奖励、藏身处制作/健身、修理等）。
/// <para>
/// 原版逻辑（ProfileHelper.AddSkillPointsToPlayer，5 参数重载）：
/// </para>
/// <code>
/// if (commonSkill.Progress &gt;= 5100.0) { ...; return; }   // 已满级，不再加点
/// ...
/// commonSkill.Progress += num;
/// commonSkill.Progress = Math.Min(commonSkill.Progress, 5100.0);  // 再次截断
/// </code>
/// <para>
/// 这里用 Transpiler 只替换这两处 <c>ldc.r8 5100</c> 常量，其它逻辑（低等级经验曲线
/// AdjustSkillExpForLowLevels、SkillGainMultipliers 等）完全保持原版行为，
/// 因此对其它模组的兼容性最好。
/// </para>
/// <para>
/// 4 参数重载只是转发到 5 参数重载，所以只需要补丁 5 参数版本。
/// </para>
/// </summary>
internal sealed class AddSkillPointsToPlayerPatch : AbstractPatch
{
    private const double VanillaMaxSkillProgress = 5100d;

    protected override MethodBase? GetTargetMethod()
    {
        return AccessTools.Method(
            typeof(ProfileHelper),
            nameof(ProfileHelper.AddSkillPointsToPlayer),
            new[] { typeof(PmcData), typeof(SkillTypes), typeof(double), typeof(bool), typeof(bool) });
    }

    [PatchTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var replaced = 0;

        foreach (var instruction in code)
        {
            if (instruction.opcode == OpCodes.Ldc_R8
                && instruction.operand is double constant
                && Math.Abs(constant - VanillaMaxSkillProgress) < 0.0001d)
            {
                instruction.operand = SkillCap100Runtime.MaxExperience;
                replaced++;
            }
        }

        if (replaced == 0)
        {
            SkillCap100Runtime.Error(
                "AddSkillPointsToPlayer：没有找到 5100 常量，补丁未生效。" +
                "请检查 SPT 版本是否仍为 4.1.x（ProfileHelper 实现可能已改变）。");
        }
        else
        {
            SkillCap100Runtime.Verbose($"AddSkillPointsToPlayer：替换了 {replaced} 处 5100 常量为 {SkillCap100Runtime.MaxExperience:0}。");
        }

        return code;
    }
}
