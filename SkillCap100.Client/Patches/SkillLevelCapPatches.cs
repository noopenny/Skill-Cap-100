using EFT;
using HarmonyLib;
using UnityEngine;

namespace SkillCap100.Client.Patches;

/// <summary>
/// 技能经验上限：5100（51 级） -> MaxSkillLevel * 100（默认 10000）。
/// <para>原版代码（EFT.BaseSkill）：</para>
/// <code>public float ClampCurrent(float value) =&gt; Mathf.Clamp(value, 0f, 5100f);</code>
/// <para>
/// 这个方法决定了 <c>Current</c>（技能内部经验值）的最大值，进而决定 <c>Level = Current / 100</c>。
/// 不替换这里，技能经验永远涨不到 5100 以上，52 级无从谈起。
/// </para>
/// <para>
/// 补丁方式：Prefix 返回 false = 完整替换方法体（不依赖 IL 中 5100 常量的位置，抗版本差异）。
/// </para>
/// </summary>
[HarmonyPatch(typeof(BaseSkill), nameof(BaseSkill.ClampCurrent))]
internal static class BaseSkillClampCurrentPatch
{
    [HarmonyPrefix]
    private static bool Prefix(float value, ref float __result)
    {
        // 原版：Mathf.Clamp(value, 0f, 5100f)
        // 现在：Mathf.Clamp(value, 0f, MaxSkillLevel * 100)
        __result = Mathf.Clamp(value, 0f, (float)SkillCapRuntime.MaxExperience);
        return false;
    }
}

/// <summary>
/// <c>SummaryLevel</c>（考虑隐藏等级加成后的等级）上限：原版 51 / 有 Buff 时 60 -> 100 / 109。
/// <para>原版代码（EFT.BaseSkill）：</para>
/// <code>public int SummaryLevel =&gt; Mathf.Min((Buff &gt; 0) ? 60 : 51, Level + Buff);</code>
/// <para>
/// SummaryLevel 被 UI（<c>SkillPanel</c>、<c>SkillTooltip</c>）以及惯性/移动等系统读取
/// （例如 <c>MovementContext.MaxSpeed</c> 用 <c>Strength.SummaryLevel / 60f</c>），
/// 所以它也必须放开上限，否则 100 级只会被显示/当作用 51 级。
/// </para>
/// </summary>
[HarmonyPatch(typeof(BaseSkill), "get_SummaryLevel")]
internal static class BaseSkillSummaryLevelPatch
{
    [HarmonyPrefix]
    private static bool Prefix(BaseSkill __instance, ref int __result)
    {
        var buff = __instance.Buff;
        var cap = buff > 0 ? SkillCapRuntime.MaxLevelWithBuff : SkillCapRuntime.MaxLevel;

        // 原版：Mathf.Min(Buff > 0 ? 60 : 51, Level + Buff)
        __result = Mathf.Min(cap, __instance.Level + buff);
        return false;
    }
}
