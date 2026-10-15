using System.Reflection;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace SkillCap100.Server.Patches;

/// <summary>
/// 精英技能判定（服务端侧）。
/// <para>
/// 原版逻辑（ProfileHelper.HasEliteSkillLevel）：
/// </para>
/// <code>
/// CommonSkill commonSkill = pmcProfile.Skills?.Common?.FirstOrDefault(s =&gt; s.Id == skill);
/// if (commonSkill == null) { logger.Error(...); return false; }
/// return commonSkill.Progress &gt;= 5100.0;
/// </code>
/// <para>
/// 服务端用这个方法判断精英效果，例如藏身处精英制作位、比特币矿场额外插槽
/// (ProfileFixerService / HideoutHelper.GetEliteSkillAdditionalBitcoinSlotCount) 等。
/// 这里改成按配置的 EliteUnlockLevel（默认 100 级 = 10000 经验）判定。
/// </para>
/// <para>
/// 注意：游戏内 99% 的精英效果由客户端本地计算（见客户端插件的 Skill.IsEliteLevel 补丁），
/// 这个补丁只是让服务端的判断与客户端保持一致，避免“客户端已精英、服务端仍当非精英”的割裂。
/// </para>
/// </summary>
internal sealed class HasEliteSkillLevelPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        return AccessTools.Method(
            typeof(ProfileHelper),
            nameof(ProfileHelper.HasEliteSkillLevel),
            new[] { typeof(SkillTypes), typeof(PmcData) });
    }

    [PatchPrefix]
    private static bool Prefix(SkillTypes skill, PmcData pmcProfile, ref bool __result)
    {
        var commonSkills = pmcProfile?.Skills?.Common;
        if (commonSkills == null)
        {
            __result = false;
            return false;
        }

        CommonSkill? commonSkill = commonSkills.FirstOrDefault(s => s.Id == skill);
        if (commonSkill == null)
        {
            // 原版会在这里 logger.Error("quest-no_skill_found")；我们只返回 false，避免刷屏
            __result = false;
            return false;
        }

        __result = commonSkill.Progress >= SkillCap100Runtime.EliteExperience;
        return false;
    }
}
