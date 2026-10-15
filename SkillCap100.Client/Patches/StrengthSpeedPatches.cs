using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace SkillCap100.Client.Patches;

/// <summary>
/// 【可选，默认关闭】力量(Strength)速度曲线的外推。
/// <para>原版代码（EFT.MovementContext）：</para>
/// <code>
/// public float MaxSpeed       =&gt; Singleton&lt;GlobalConfiguration&gt;.Instance.WalkSpeed.Evaluate((float)SkillManager.Strength.SummaryLevel / 60f);
/// public float SprintingSpeed =&gt; Singleton&lt;GlobalConfiguration&gt;.Instance.SprintSpeed.Evaluate((float)SkillManager.Strength.SummaryLevel / 60f);
///
/// // 扩展方法：UnityEngine.Vector2.Evaluate(t) =&gt; Mathf.Lerp(v.x, v.y, t);   // Mathf.Lerp 内部把 t 截断到 0~1
/// </code>
/// <para>
/// 也就是说这条曲线在客户端被硬编码归一化到 60 级（而不是 51），且由于 <c>Mathf.Lerp</c> 会截断 t，
/// <b>60 级之后速度就不再增长</b>（51 级约 0.955，60 级到顶 1.0）。
/// 这是原版少数“无法通过放开等级上限自动外推”的效果之一。
/// </para>
/// <para>
/// 打开配置 <c>ExtrapolateStrengthSpeedCurve</c> 后，本补丁用 <c>Mathf.LerpUnclamped</c> 继续线性外推：
/// 100 级时 t = 100/60 ≈ 1.667，行走速度系数约 0.7 + 0.3*1.667 ≈ 1.2。
/// </para>
/// <para>
/// ⚠ 风险：客户端有速度异常检查（"Too high speed" 日志）以及
/// <c>MovementContext.SpeedLimiter</c>（同样按 60 级归一化）会平滑限制实际速度，
/// 因此开启后提升幅度有限并可能产生日志刷屏。默认 false。
/// </para>
/// </summary>
[HarmonyPatch(typeof(MovementContext), "get_MaxSpeed")]
internal static class MovementContextMaxSpeedPatch
{
    [HarmonyPrefix]
    private static bool Prefix(MovementContext __instance, ref float __result)
    {
        var walkSpeed = Singleton<GlobalConfiguration>.Instance.WalkSpeed;
        __result = SkillCapRuntime.EvaluateStrengthSpeedCurve(walkSpeed, __instance.SkillManager.Strength.SummaryLevel);
        return false;
    }
}

/// <summary>见 <see cref="MovementContextMaxSpeedPatch"/>。</summary>
[HarmonyPatch(typeof(MovementContext), "get_SprintingSpeed")]
internal static class MovementContextSprintingSpeedPatch
{
    [HarmonyPrefix]
    private static bool Prefix(MovementContext __instance, ref float __result)
    {
        var sprintSpeed = Singleton<GlobalConfiguration>.Instance.SprintSpeed;
        __result = SkillCapRuntime.EvaluateStrengthSpeedCurve(sprintSpeed, __instance.SkillManager.Strength.SummaryLevel);
        return false;
    }
}
