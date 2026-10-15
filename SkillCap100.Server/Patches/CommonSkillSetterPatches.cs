using System.Reflection;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SkillCap100.Server.Patches;

/// <summary>
/// <c>CommonSkill</c> 两个数值属性的 backing field。
/// <para>
/// 为什么要直接写 backing field：原版 setter 是
/// <c>set =&gt; &lt;Progress&gt;k__BackingField = GetSafeSkillProgress(value);</c>，
/// 而 <c>GetSafeSkillProgress</c> 只有一行 <c>Math.Min(value, 5100)</c>。
/// 当 <b>在第一次反序列化之前</b> 打补丁时，替换 <c>GetSafeSkillProgress</c> 就够了（已实测）；
/// 但为了万无一失（例如某些调用方在我们打补丁前就被 JIT 编译、把旧的 setter/getter 内联进去），
/// 这里再直接接管 setter 本身，彻底绕开原版实现。
/// </para>
/// </summary>
internal static class CommonSkillBackingFields
{
    internal static readonly AccessTools.FieldRef<CommonSkill, double> Progress =
        AccessTools.FieldRefAccess<CommonSkill, double>("<Progress>k__BackingField");

    internal static readonly AccessTools.FieldRef<CommonSkill, double> PointsEarnedDuringSession =
        AccessTools.FieldRefAccess<CommonSkill, double>("<PointsEarnedDuringSession>k__BackingField");

    /// <summary>与原版 GetSafeSkillProgress 语义一致，只把上限换成配置值。</summary>
    internal static double Clamp(double value)
    {
        return double.IsFinite(value) ? Math.Min(value, SkillCap100Runtime.MaxExperience) : 0d;
    }
}

/// <summary>
/// 接管 <c>CommonSkill.Progress</c> 的 setter。
/// <para>
/// 这是**存档/战局结算掉级的直接原因**：战局结束时客户端把战后档案发到
/// <c>/client/match/local/end</c>，服务端把它反序列化成 <c>PmcData</c>（其中包含
/// <c>Skills.Common[].Progress</c>）→ setter 里的 <c>Math.Min(value, 5100)</c> 把
/// 52 级以上的进度全部砍回 5100。
/// </para>
/// </summary>
internal sealed class CommonSkillProgressSetterPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        return AccessTools.PropertySetter(typeof(CommonSkill), nameof(CommonSkill.Progress));
    }

    [PatchPrefix]
    private static bool Prefix(CommonSkill __instance, double value)
    {
        CommonSkillBackingFields.Progress(__instance) = CommonSkillBackingFields.Clamp(value);
        return false;
    }
}

/// <summary>接管 <c>CommonSkill.PointsEarnedDuringSession</c> 的 setter（同一个 5100 上限）。</summary>
internal sealed class CommonSkillPointsEarnedSetterPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        return AccessTools.PropertySetter(typeof(CommonSkill), nameof(CommonSkill.PointsEarnedDuringSession));
    }

    [PatchPrefix]
    private static bool Prefix(CommonSkill __instance, double value)
    {
        CommonSkillBackingFields.PointsEarnedDuringSession(__instance) = CommonSkillBackingFields.Clamp(value);
        return false;
    }
}
