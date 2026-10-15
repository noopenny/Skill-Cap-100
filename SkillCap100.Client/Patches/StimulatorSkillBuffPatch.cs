using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SkillCap100.Client.Patches;

/// <summary>
/// 刺激剂（Stimulator）「技能等级 buff」的边界修正 —— 修复「技能满级后吃药反而掉到几十级」。
/// <para>
/// 反编译原版 <c>EFT.HealthSystem.ActiveHealthController/Stimulator.ActivateBuff</c>：
/// </para>
/// <code>
/// case EStimulatorBuffType.SkillRate:
///     float f = EvaluateValue(buff.Settings, skill.Level, new Vector2(-skill.Level, 60 - skill.Level));
///     buff.ValueObj = Mathf.CeilToInt(f);
///     int num = Mathf.RoundToInt(buff.Value);
///     skill.Buff += (activate ? num : -num);
/// </code>
/// <para>而 <c>EvaluateValue</c> 内部：</para>
/// <code>
/// float num = buffSettings.AbsoluteValue ? buffSettings.Value : ((buffSettings.Value + 1f) * refValue);
/// if (limits.HasValue) num = Mathf.Clamp(num, limits.Value.x, limits.Value.y);
/// </code>
/// <para>
/// 那个 <c>60 - skill.Level</c> 是「技能总等级不超过 60（原版 51 + 9）」的假设，只有在 <c>Level ≤ 60</c> 时上界才非负。
/// 技能上限提到 100 后，<b>Level ≥ 61 时上界变成负数</b>：
/// 例如 100 级 + 一支「+3 级」的刺激剂 → <c>Mathf.Clamp(3, -100, -40) = -40</c>，
/// 于是 <c>skill.Buff += -40</c>、<c>SummaryLevel = min(109, 100 - 40) = 60</c> ——
/// 技能面板显示成红字「Level 60」，技能不再算满级、效果也按 60 级算。
/// </para>
/// <para>
/// <b>修法</b>：只在「上界已经变成负数」时，把上界改回 <c>MaxLevelWithBuff - Level</c>
/// （默认 109 - Level，即保留原版“+9 级余量”的设计）；上界为非负时（Level ≤ 60）保持原版行为完全不变。
/// 可以这样修的原因：原版所有 <c>EvaluateValue</c> 调用里，只有 SkillRate 分支传了 <c>limits</c>
/// （另外 5 处分别是 HealthRate / EnergyRate / HydrationRate / MaxStamina / StaminaRate，都不传）。
/// </para>
/// <para>
/// 目标方法用「类型名 + 方法名」在运行时查找（不写 <c>typeof</c>，避免本插件依赖游戏内部类型），
/// 找不到就跳过并记日志，不影响其它功能。
/// </para>
/// </summary>
internal static class StimulatorSkillBuffPatch
{
    private const string StimulatorTypeName = "EFT.HealthSystem.ActiveHealthController+Stimulator";
    private const string MethodName = "EvaluateValue";

    /// <summary>安装补丁；返回是否成功。</summary>
    internal static bool Apply(Harmony harmony)
    {
        var type = FindType(StimulatorTypeName);
        var method = type?.GetMethod(MethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null)
        {
            SkillCap100Plugin.Log?.LogWarning(
                $"刺激剂技能 buff 修正跳过：找不到 {StimulatorTypeName}.{MethodName}（满级后吃技能刺激剂可能会掉级）。");
            return false;
        }

        try
        {
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(StimulatorSkillBuffPatch).GetMethod(
                    nameof(Prefix),
                    BindingFlags.NonPublic | BindingFlags.Static)));
            SkillCap100Plugin.Verbose($"刺激剂技能 buff 修正已启用：{StimulatorTypeName}.{MethodName}");
            return true;
        }
        catch (Exception ex)
        {
            SkillCap100Plugin.Log?.LogWarning($"刺激剂技能 buff 修正失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>原版上界（<c>60 - Level</c>）为负时，按我们的等级上限重新给出上界。</summary>
    [HarmonyPrefix]
    private static void Prefix(ref Vector2? limits)
    {
        if (!limits.HasValue || limits.Value.y >= 0f)
        {
            return; // 原版区间仍然成立（Level ≤ 60），不做任何改动
        }

        var level = -limits.Value.x;              // 调用方传的是 (-Level, 60 - Level)
        var vanillaUpper = limits.Value.y;        // 原版上界（此时为负数，已失效）
        if (level <= 0f)
        {
            return;
        }

        var headroom = SkillCapRuntime.MaxLevelWithBuff - level;
        limits = new Vector2(limits.Value.x, headroom);
        SkillCap100Plugin.Verbose(
            $"刺激剂技能 buff：技能 {level:0} 级时原版上界 {vanillaUpper:0} 已失效（负数），" +
            $"改为允许 +{headroom:0} 级（技能总等级上限 {SkillCapRuntime.MaxLevelWithBuff}）");
    }

    private static Type? FindType(string fullName)
    {
        var type = Type.GetType(fullName);
        if (type != null)
        {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                type = assembly.GetType(fullName, throwOnError: false);
            }
            catch
            {
                continue;
            }

            if (type != null)
            {
                return type;
            }
        }

        return null;
    }
}
