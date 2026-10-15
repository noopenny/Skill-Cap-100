using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;

namespace SkillCap100.Server.Patches;

/// <summary>
/// 藏身处健身房（QTE 健身）加技能经验时的满级过滤。
/// <para>原版代码（HideoutController.ApplyWorkoutSkillGain，编译器生成 lambda）：</para>
/// <code>
/// List&lt;QteEffect&gt; list = skillRewards
///     .Where(reward =&gt; (profileSkills.FirstOrDefault(s =&gt; s.Id == reward.SkillId)?.Progress ?? 0.0) &lt; 5100.0)
///     .ToList();
/// </code>
/// <para>
/// 这个 lambda 会把已经“满级”（原版 5100）的技能从健身奖励里剔除。
/// 不改这里的话，力量/耐力练到 51 级之后就再也无法通过健身房获得经验，
/// 而健身房是 SPT 里提升力量的主要途径 —— 100 级上限就形同虚设。
/// </para>
/// <para>
/// 目标方法是编译器生成的 lambda（<c>&lt;ApplyWorkoutSkillGain&gt;b__0</c>），
/// 名称在不同 SPT 版本可能变化，因此这里做两级动态定位：
/// 先按名字前缀找，找不到就扫描 IL 里含 <c>ldc.r8 5100</c> 的嵌套方法。
/// </para>
/// </summary>
internal sealed class WorkoutSkillGainCapPatch : AbstractPatch
{
    private const double VanillaMaxSkillProgress = 5100d;

    protected override MethodBase? GetTargetMethod()
    {
        var lambda = FindByCompilerGeneratedName() ?? FindByIlConstant();
        if (lambda == null)
        {
            SkillCap100Runtime.Error(
                "找不到 HideoutController.ApplyWorkoutSkillGain 中过滤满级技能的 lambda，" +
                "健身房经验在 51 级后会停止累计（SPT 版本可能已变化）。");
        }

        return lambda;
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

        SkillCap100Runtime.Verbose(replaced > 0
            ? $"健身房技能上限过滤：替换了 {replaced} 处 5100 为 {SkillCap100Runtime.MaxExperience:0}。"
            : "健身房技能上限过滤：未找到 5100 常量。");

        return code;
    }

    /// <summary>优先按编译器命名规则定位：<c>&lt;ApplyWorkoutSkillGain&gt;b__*</c>。</summary>
    private static MethodBase? FindByCompilerGeneratedName()
    {
        foreach (var nested in typeof(HideoutController).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var method in nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                     | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.Name.Contains("<ApplyWorkoutSkillGain>"))
                {
                    return method;
                }
            }
        }

        return null;
    }

    /// <summary>兜底：扫描嵌套类型里 IL 含 <c>ldc.r8 5100</c> 的无参 lambda。</summary>
    private static MethodBase? FindByIlConstant()
    {
        foreach (var nested in typeof(HideoutController).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var method in nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                     | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (ContainsLdcR8(method, VanillaMaxSkillProgress))
                {
                    return method;
                }
            }
        }

        return null;
    }

    private static bool ContainsLdcR8(MethodBase method, double value)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            return false;
        }

        var needle = BitConverter.GetBytes(value);
        for (var i = 0; i + 8 < il.Length; i++)
        {
            if (il[i] != 0x23) // ldc.r8
            {
                continue;
            }

            var match = true;
            for (var j = 0; j < 8; j++)
            {
                if (il[i + 1 + j] == needle[j])
                {
                    continue;
                }

                match = false;
                break;
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
