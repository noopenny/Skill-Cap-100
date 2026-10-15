using System.Reflection.Emit;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace SkillCap100.Client.Patches;

/// <summary>
/// 精英（Elite）判定阈值：原版 <c>Level &gt; 50</c>（即 51 级） -> <c>Level &gt;= MaxLevel</c>（默认 100）。
/// <para>
/// <b>重要（v1.0.3 修正）：<c>IsEliteLevel</c> 的语义是「是否练满」，不是「精英效果何时解锁」。</b>
/// 原版把两者合二为一（51 级既解锁精英效果、又是等级上限），所以游戏自己的代码到处把
/// <c>IsEliteLevel</c> 当作“练满”来用（反编译 <c>Assembly-CSharp</c> 实测）：
/// <list type="bullet">
///   <item><description>
///     <c>WorkoutBehaviour.CalculateExperience</c> / <c>CG_CalculateExperience</c>：
///     <c>.Where(effect =&gt; !Skills.GetSkill(effect.Skill).IsEliteLevel)</c> ——
///     被判定为精英的技能<b>直接不给健身房经验</b>；力量/耐力若在 51 级就被算作精英，健身房就练不动了。
///   </description></item>
///   <item><description>
///     技能面板/提示框：精英时显示 <c>maxed</c> / <c>ELITE Level</c>、进度条强制填满、等级数字隐藏
///     —— 51 级就判精英会让 51~99 级看起来“已满级、进度不动”。
///   </description></item>
///   <item><description>
///     <c>SkillDistribution</c> 等模组用它判断“这个技能的溢出经验要不要分发出去”。
///   </description></item>
/// </list>
/// 因此这里必须用等级上限 <see cref="SkillCapRuntime.MaxLevel"/>；
/// 精英<b>效果</b>的解锁时机由 <c>Skill.UpdateRules</c> 的阈值单独控制
/// （<see cref="SkillCapRuntime.EffectiveEliteUnlockLevel"/>，<c>Vanilla*</c> 模式下为 51），
/// 见 <see cref="SkillUpdateRulesPatch"/>。两者互不影响。
/// </para>
/// </summary>
[HarmonyPatch(typeof(Skill), "get_IsEliteLevel")]
internal static class SkillIsEliteLevelPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Skill __instance, ref bool __result)
    {
        // 原版：Level > 50
        __result = __instance.Level >= SkillCapRuntime.MaxLevel;
        return false;
    }
}

/// <summary>
/// 技能效果逐级叠加 + 精英效果解锁时机（<c>Skill.UpdateRules</c>）。
/// <para>原版 IL（本机 Assembly-CSharp.dll 实测，见 tools/SkillCap100.Verify 输出）：</para>
/// <code>
/// IL_0006: ldc.i4.s 50            // ① Math.Min(SummaryLevel, 50)  —— 传给 BaseRuleFunc 的等级上限
/// IL_003d: ldc.i4.s 50            // ② if (SummaryLevel &gt; 50)     —— 精英效果解锁判定
/// IL_006f: ldc.i4.s 50            // ③ if (Level &gt; 50)            —— 满级后取消订阅（停止涨经验）
/// </code>
/// <para>
/// Buff 的数值规则（<c>SkillManager.FloatBuff</c>）：
/// <list type="bullet">
///   <item><description><c>PerLevel(f)</c>：<c>Value = level * f</c></description></item>
///   <item><description><c>Max(f)</c>：<c>Value = level * (f / 50f)</c>（50 级时正好等于 f）</description></item>
///   <item><description><c>Elite(x)</c>：精英时把 <c>Value</c> 直接设为绝对值 x</description></item>
/// </list>
/// 因此只要把 ① 的上限从 50 提到 100，所有按等级叠加的技能效果就自动线性延伸到 100 级
/// （原本 50 级封顶的值在 100 级变成 2 倍，正好是“每级增量不变”的外推）。
/// </para>
/// <para>
/// 采用 Transpiler 只替换这 3 个常量，而不是整体替换方法体，是为了与
/// <c>SkillDistribution</c> 等同样补丁了 <c>UpdateRules</c> / <c>Unsubscribe</c> 的模组共存。
/// 如果 IL 结构与预期不符（例如游戏版本变化），补丁会放弃修改并在 BepInEx 日志里报错，
/// 而不是把方法改坏。
/// </para>
/// </summary>
[HarmonyPatch(typeof(Skill), nameof(Skill.UpdateRules))]
internal static class SkillUpdateRulesPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);

        var fiftyConstants = code.Where(IsConstant50).ToList();
        if (fiftyConstants.Count != 3)
        {
            SkillCap100Plugin.Log.LogError(
                $"Skill.UpdateRules 的 IL 结构与预期不符：期望 3 个常量 50，实际 {fiftyConstants.Count} 个。" +
                "为避免破坏游戏，本次不修改该方法。请更新 SkillCap100 或反馈此日志。");
            return code;
        }

        // ① 基础效果上限：外推开 -> MaxLevel(100)；关 -> 50（保持原版效果，只涨等级数字）
        ReplaceWithCall(fiftyConstants[0], nameof(SkillCapRuntime.BaseEffectLevelCap));
        // ② 精英效果阈值：注意原版是 “>” 比较，所以注入 EliteUnlockLevel - 1
        ReplaceWithCall(fiftyConstants[1], nameof(SkillCapRuntime.EliteRuleComparisonThreshold));
        // ③ 停止累计经验的等级：注入 MaxLevel - 1，即练到 100 级才停止
        ReplaceWithCall(fiftyConstants[2], nameof(SkillCapRuntime.UnsubscribeComparisonThreshold));

        SkillCap100Plugin.Verbose(
            $"Skill.UpdateRules 已改写：BaseEffectLevelCap={SkillCapRuntime.BaseEffectLevelCap}, " +
            $"EliteRuleComparisonThreshold={SkillCapRuntime.EliteRuleComparisonThreshold}, " +
            $"UnsubscribeComparisonThreshold={SkillCapRuntime.UnsubscribeComparisonThreshold}");

        return code;
    }

    /// <summary>
    /// 精英规则修正。做两件事：
    /// <list type="number">
    ///   <item><description><b>取较大者（必须）</b>：<c>Elite(x)</c> 是把 <c>Value</c> 直接设成绝对值，
    ///   并且原版在基础规则 <b>之后</b> 执行。放开等级上限后会出现“基础曲线已经超过精英绝对值”的情况，
    ///   例如 <c>EnduranceBuffEnduranceInc.Max(0.5).Elite(0.7)</c>：
    ///   50 级 = 0.5、51 级 = 0.7（原版精英），而 100 级基础值 = 1.0 ——
    ///   如果不处理，练到 100 级反而会被精英的 0.7 覆盖，比 99 级更差。
    ///   因此这里重新计算基础值并取 <c>Mathf.Max(精英值, 基础值)</c>：
    ///   在 51 级及以下与原版完全一致（精英值更大），100 级则取更大的基础值。</description></item>
    ///   <item><description><b>满级强化（可选）</b>：<c>EliteMode = VanillaAndEnhanced</c> 时，
    ///   练满 MaxLevel 后再按 <c>EliteBonusMultiplier</c> 强化一次精英数值。</description></item>
    /// </list>
    /// 由于精英规则是“赋绝对值”，本 Postfix 反复执行是幂等的（不会逐帧累乘）。
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(Skill __instance)
    {
        var buffs = __instance.Buffs;
        if (buffs == null)
        {
            return;
        }

        var summaryLevel = __instance.SummaryLevel;
        if (summaryLevel <= SkillCapRuntime.EliteRuleComparisonThreshold)
        {
            // 精英规则没有生效（还没到解锁等级），无需修正
            return;
        }

        var enhanceElite = SkillCapRuntime.EnhanceEliteAtMaxLevel && summaryLevel >= SkillCapRuntime.MaxLevel;

        foreach (var buff in buffs)
        {
            if (buff == null)
            {
                continue;
            }

            if (enhanceElite && buff.EliteRuleFunc != null)
            {
                buff.EliteRuleFunc();

                if (buff is SkillManager.FloatBuff enhancedBuff)
                {
                    enhancedBuff.Value *= SkillCapRuntime.EliteBonusMultiplier;

                    // 概率类效果不能超过 100%
                    if (buff is SkillManager.ProbabilityBuff)
                    {
                        enhancedBuff.Value = Mathf.Min(enhancedBuff.Value, 1f);
                    }
                }
            }

            if (buff is SkillManager.FloatBuff floatBuff && buff.BaseRuleFunc != null)
            {
                var eliteValue = floatBuff.Value;

                // 用「当前等级与基础效果上限的较小值」重新计算基础值，然后取较大者。
                // 注意必须是 Math.Min(SummaryLevel, cap) —— 与原版 UpdateRules 传给 BaseRuleFunc 的等级一致；
                // 若这里直接用 cap(=100)，在 EliteMode = Vanilla / VanillaAndEnhanced（精英阈值 = 51）时
                // 会让 51 级就吃到 100 级的基础值，属于错误放大。
                var baseLevel = Mathf.Min(summaryLevel, SkillCapRuntime.BaseEffectLevelCap);
                buff.BaseRuleFunc(baseLevel);
                floatBuff.Value = Mathf.Max(eliteValue, floatBuff.Value);
            }
        }
    }

    private static bool IsConstant50(CodeInstruction instruction)
    {
        return (instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte small && small == 50)
               || (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int value && value == 50);
    }

    /// <summary>把 <c>ldc.i4 50</c> 换成 <c>call get_Xxx()</c>（保留原有的 label / 异常块信息）。</summary>
    private static void ReplaceWithCall(CodeInstruction instruction, string propertyName)
    {
        var getter = AccessTools.PropertyGetter(typeof(SkillCapRuntime), propertyName);
        instruction.opcode = OpCodes.Call;
        instruction.operand = getter;
    }
}
