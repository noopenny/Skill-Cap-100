using System.Reflection;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SkillCap100.Server.Patches;

/// <summary>
/// 【最关键的服务端补丁】
/// <para>
/// 原版逻辑（SPTarkov.Server.Core, Models/Eft/Common/Tables/CommonSkill.cs）：
/// </para>
/// <code>
/// public double Progress { get => ...; set => &lt;Progress&gt;k__BackingField = GetSafeSkillProgress(value); }
/// private static double GetSafeSkillProgress(double value)
///     =&gt; double.IsFinite(value) ? Math.Min(value, 5100.0) : 0.0;
/// </code>
/// <para>
/// 也就是说：只要服务端把存档反序列化成 <see cref="CommonSkill"/>（玩家档案读写、任务奖励、
/// 藏身处加成等全部路径），任何大于 5100 的进度都会被静默截断回 5100（= 51 级）。
/// 不补丁这里，客户端练到 52~100 级的经验在下次保存/读取档案时就会全部丢失。
/// </para>
/// <para>
/// 补丁方式：完整替换该方法体，把上限换成 MaxSkillLevel * 100（默认 10000）。
/// 非有限值仍然返回 0，保持原版对 NaN/Infinity 的防护。
/// </para>
/// </summary>
internal sealed class CommonSkillProgressPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        // private static double GetSafeSkillProgress(double value)
        return AccessTools.Method(typeof(CommonSkill), "GetSafeSkillProgress", new[] { typeof(double) });
    }

    [PatchPrefix]
    private static bool Prefix(double value, ref double __result)
    {
        if (!double.IsFinite(value))
        {
            // 原版：NaN / Infinity -> 0
            __result = 0d;
            return false;
        }

        // 原版：Math.Min(value, 5100.0)  -> 现在用配置的技能经验上限
        __result = Math.Min(value, SkillCap100Runtime.MaxExperience);
        return false;
    }
}
