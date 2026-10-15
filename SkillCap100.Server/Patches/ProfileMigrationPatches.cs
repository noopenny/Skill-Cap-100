using System.Reflection;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Migration.Migrations.Fixes;

namespace SkillCap100.Server.Patches;

/// <summary>
/// 档案迁移清洗逻辑（InvalidSkillProgressFix）。
/// <para>
/// SPT 4.1.x 在读取玩家档案时会跑一系列 migration，其中 InvalidSkillProgressFix 的
/// 判定标准是：
/// </para>
/// <code>
/// private static bool NeedsFixing(double skillValue)
///     =&gt; double.IsFinite(v) &amp;&amp; !(v &gt; 5100.0) ? IsNegativeOrNegativeZero(v) : true;
///
/// private static double GetFixedValue(double skillValue)
///     =&gt; !finite || negative ? 0 : Math.Min(skillValue, 5100.0);
/// </code>
/// <para>
/// 只要有一个技能的 Progress &gt; 5100，迁移就会被触发，并把所有 &gt; 5100 的技能修正回 5100
/// —— 对 100 级存档来说等于每次读档都掉回 51 级。这两个补丁把阈值改成配置的经验上限。
/// </para>
/// <para>
/// 可以用配置 <c>patchProfileMigrations=false</c> 关闭（不推荐）。
/// </para>
/// </summary>
internal static class ProfileMigrationHelpers
{
    /// <summary>对应原版 IsNegativeOrNegativeZero：负数或“负零”。</summary>
    internal static bool IsNegativeOrNegativeZero(double value)
    {
        return value < 0d || BitConverter.DoubleToInt64Bits(value) == long.MinValue;
    }

    /// <summary>对应原版 NeedsFixing(double)。</summary>
    internal static bool NeedsFixing(double value)
    {
        return !double.IsFinite(value) || value > SkillCap100Runtime.MaxExperience || IsNegativeOrNegativeZero(value);
    }

    /// <summary>对应原版 GetFixedValue(double)。</summary>
    internal static double GetFixedValue(double value)
    {
        if (!double.IsFinite(value) || IsNegativeOrNegativeZero(value))
        {
            return 0d;
        }

        return Math.Min(value, SkillCap100Runtime.MaxExperience);
    }
}

/// <summary>补丁 <c>InvalidSkillProgressFix.NeedsFixing(double)</c>（private static）。</summary>
internal sealed class InvalidSkillProgressNeedsFixingPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        return AccessTools.Method(typeof(InvalidSkillProgressFix), "NeedsFixing", new[] { typeof(double) });
    }

    [PatchPrefix]
    private static bool Prefix(double skillValue, ref bool __result)
    {
        __result = ProfileMigrationHelpers.NeedsFixing(skillValue);
        return false;
    }
}

/// <summary>补丁 <c>InvalidSkillProgressFix.GetFixedValue(double)</c>（private static）。</summary>
internal sealed class InvalidSkillProgressGetFixedValuePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        return AccessTools.Method(typeof(InvalidSkillProgressFix), "GetFixedValue", new[] { typeof(double) });
    }

    [PatchPrefix]
    private static bool Prefix(double skillValue, ref double __result)
    {
        __result = ProfileMigrationHelpers.GetFixedValue(skillValue);
        return false;
    }
}
