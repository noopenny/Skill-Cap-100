using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;

namespace SkillCap100.Client.Patches;

/// <summary>
/// 把 UI 里「精英状态」的判定从「是否练满」改成「精英效果是否已生效」。
/// <para>
/// 原版把两个概念合在一起：<c>Skill.IsEliteLevel</c>（<c>Level &gt; 50</c>）既是「精英效果已解锁」，
/// 又是「技能已练满」。本模组把两者拆开：
/// </para>
/// <list type="bullet">
///   <item><description><b>是否练满</b>（健身房可练、技能面板显示等级/进度、第三方模组判断满级）
///     → 继续读 <c>Skill.IsEliteLevel</c>，由 <see cref="SkillIsEliteLevelPatch"/> 设为等级上限（默认 100）；</description></item>
///   <item><description><b>精英效果是否生效</b>（提示框/技能栏里的精英 buff 行与 ELITE 标记）
///     → 这里改用 <see cref="EliteUiState.IsEliteEffectActive"/>（<c>SummaryLevel ≥ EffectiveEliteUnlockLevel</c>）。</description></item>
/// </list>
/// <para>
/// 不做这个区分就会出现「v1.0.3 的现象」：效果从 51 级就按精英算，但提示框把精英行整行隐藏，
/// 玩家只看到基础数值，以为精英效果没了。反编译原版 <c>EFT.UI.BuffThumb.UpdateVisibility</c>：
/// </para>
/// <code>
/// bool flag = (Buff.BuffType == EBuffType.Elite &amp;&amp; !Skill.IsEliteLevel)
///          || (Buff.BuffType == EBuffType.Plebian &amp;&amp; Skill.IsEliteLevel)
///          || Skill.SummaryLevel == 0 || Buff.HidenForPlayers;
/// gameObject.SetActive(!flag);   // ← IsEliteLevel=false（51~99 级）时精英行被隐藏
/// </code>
/// <para>
/// 目标方法用「类型名 + 方法名」在运行时查找（不写 <c>typeof</c>，避免给本插件引入 UI 依赖），
/// 找不到就跳过并记日志，不影响其它功能。
/// </para>
/// </summary>
internal static class EliteUiStatePatches
{
    /// <summary>需要把 <c>IsEliteLevel</c> 换成「精英效果是否生效」的 UI 方法。</summary>
    private static readonly (string TypeName, string MethodName, string Purpose)[] Targets =
    {
        ("EFT.UI.BuffThumb", "UpdateVisibility", "精英 buff 行的显隐（最关键：否则 51~99 级看不到精英行）"),
        ("EFT.UI.BuffIcon", "UpdateBuff", "buff 图标的精英底色"),
        ("EFT.UI.BuffIcon", "OnPointerEnter", "buff 悬浮提示的 ELITE 前缀"),
        ("EFT.UI.BuffPanel", "UpdateBuff", "技能栏 buff 行的 ELITE 前缀（含 Switching 类型）"),
    };

    /// <summary>返回成功打上的补丁数量。</summary>
    internal static int Apply(Harmony harmony, out int total)
    {
        total = Targets.Length;
        var applied = 0;

        foreach (var (typeName, methodName, purpose) in Targets)
        {
            var type = FindType(typeName);
            var method = type?.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (method == null)
            {
                SkillCap100Plugin.Log?.LogWarning($"UI 精英状态补丁跳过：找不到 {typeName}.{methodName}（{purpose}）");
                continue;
            }

            try
            {
                harmony.Patch(
                    method,
                    transpiler: new HarmonyMethod(typeof(EliteUiState).GetMethod(
                        nameof(EliteUiState.ReplaceIsEliteLevel),
                        BindingFlags.NonPublic | BindingFlags.Static)));
                applied++;
                SkillCap100Plugin.Verbose($"UI 精英状态补丁已启用：{typeName}.{methodName}（{purpose}）");
            }
            catch (Exception ex)
            {
                SkillCap100Plugin.Log?.LogWarning($"UI 精英状态补丁失败：{typeName}.{methodName} - {ex.Message}");
            }
        }

        return applied;
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

/// <summary>UI「精英状态」判定 + 共用的 Transpiler。</summary>
internal static class EliteUiState
{
    /// <summary>精英效果是否已经生效（等价于原版 <c>SummaryLevel &gt; 50</c>，但使用配置里的解锁等级）。</summary>
    public static bool IsEliteEffectActive(Skill skill)
    {
        return skill != null && skill.SummaryLevel >= SkillCapRuntime.EffectiveEliteUnlockLevel;
    }

    /// <summary>把 <c>callvirt Skill::get_IsEliteLevel</c> 换成 <see cref="IsEliteEffectActive"/>。</summary>
    internal static IEnumerable<CodeInstruction> ReplaceIsEliteLevel(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        var replacement = typeof(EliteUiState).GetMethod(nameof(IsEliteEffectActive), BindingFlags.Public | BindingFlags.Static);
        var replaced = 0;

        foreach (var instruction in list)
        {
            if (instruction.opcode != OpCodes.Callvirt || instruction.operand is not MethodInfo method)
            {
                continue;
            }

            if (method.Name != "get_IsEliteLevel")
            {
                continue;
            }

            var declaringTypeName = method.DeclaringType?.FullName ?? string.Empty;
            if (!declaringTypeName.EndsWith("Skill", StringComparison.Ordinal))
            {
                continue;
            }

            instruction.opcode = OpCodes.Call;
            instruction.operand = replacement;
            replaced++;
        }

        if (replaced == 0)
        {
            SkillCap100Plugin.Log?.LogWarning("UI 精英状态：未匹配到 IsEliteLevel 判定，精英行显隐保持原版行为。");
        }
        else
        {
            SkillCap100Plugin.Verbose($"UI 精英状态：替换了 {replaced} 处 IsEliteLevel 判定。");
        }

        return list;
    }
}
