using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace SkillCap100.Client.Patches;

/// <summary>
/// 客户端把技能进度同步给服务端时的截断。
/// <para>原版代码（EFT.SkillsDescriptor 的编译器生成 lambda：<c>SkillsDescriptor/CG_Ctor::method_0</c>）：</para>
/// <code>
/// new SkillInfoDescriptor
/// {
///     Id = skill.Id,
///     Progress = Mathf.Clamp(skill.Current, 0f, 5100f),   // &lt;-- 这里
///     ...
/// }
/// </code>
/// <para>
/// 这是**最容易漏掉**的一处：即使客户端能把技能练到 100 级，只要这里仍然 clamp 到 5100，
/// 客户端发给 SPT 服务端的档案里技能进度还是 5100，下一次读档就掉回 51 级（经验丢失）。
/// </para>
/// <para>
/// 这里用两种互补的方式修补它：
/// <list type="number">
///   <item><description><see cref="SkillsDescriptorLambdaClampPatch"/>：Transpiler 把 lambda 里的
///   <c>ldc.r4 5100</c> 换成配置值。目标方法通过“返回 <c>SkillInfoDescriptor</c> 且只有一个
///   <c>Skill</c> 参数”动态定位，不依赖编译器生成的类名/方法名。</description></item>
///   <item><description><see cref="SkillsDescriptorCtorClampPatch"/>：构造函数 Postfix，
///   直接从 <c>SkillManager</c> 重新读取真实进度并写回描述符，作为兜底。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class SkillsDescriptorPatchHelpers
{
    private const float VanillaMaxSkillProgress = 5100f;

    /// <summary>定位编译器生成的 lambda：<c>SkillInfoDescriptor method_X(Skill skill)</c>。</summary>
    internal static MethodBase? FindDescriptorComposingMethod()
    {
        foreach (var nested in typeof(SkillsDescriptor).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var method in nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                     | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.ReturnType != typeof(SkillsDescriptor.SkillInfoDescriptor))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Skill))
                {
                    return method;
                }
            }
        }

        return null;
    }

    /// <summary>把 IL 中的 <c>ldc.r4 5100</c> 替换成 <c>SkillCapRuntime.MaxExperienceSingle</c>。</summary>
    internal static IEnumerable<CodeInstruction> ReplaceVanillaClamp(IEnumerable<CodeInstruction> instructions, string patchName)
    {
        var code = new List<CodeInstruction>(instructions);
        var getter = AccessTools.PropertyGetter(typeof(SkillCapRuntime), nameof(SkillCapRuntime.MaxExperienceSingle));
        var replaced = 0;

        foreach (var instruction in code)
        {
            if (instruction.opcode == OpCodes.Ldc_R4
                && instruction.operand is float constant
                && Math.Abs(constant - VanillaMaxSkillProgress) < 0.001f)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = getter;
                replaced++;
            }
        }

        if (replaced == 0)
        {
            SkillCap100Plugin.Log.LogError($"{patchName}：没有找到 5100 常量，技能进度同步仍会被截断到 51 级！");
        }
        else
        {
            SkillCap100Plugin.Verbose($"{patchName}：替换了 {replaced} 处 5100 截断为 {SkillCapRuntime.MaxExperience:0}。");
        }

        return code;
    }
}

/// <summary>Transpiler 补丁：SkillsDescriptor 的 lambda 里的 5100 截断。</summary>
[HarmonyPatch]
internal static class SkillsDescriptorLambdaClampPatch
{
    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        var target = SkillsDescriptorPatchHelpers.FindDescriptorComposingMethod();
        if (target == null)
        {
            throw new MissingMethodException(
                "EFT.SkillsDescriptor 中找不到生成 SkillInfoDescriptor 的 lambda（SPT/游戏版本可能已变化）");
        }

        return target;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return SkillsDescriptorPatchHelpers.ReplaceVanillaClamp(instructions, nameof(SkillsDescriptorLambdaClampPatch));
    }
}

/// <summary>兜底 Postfix：构造函数结束后，用真实技能进度覆盖描述符里的值。</summary>
[HarmonyPatch(typeof(SkillsDescriptor), MethodType.Constructor, new[] { typeof(SkillManager) })]
internal static class SkillsDescriptorCtorClampPatch
{
    [HarmonyPostfix]
    private static void Postfix(SkillsDescriptor __instance, SkillManager skillManager)
    {
        var descriptors = __instance?.Common;
        var skills = skillManager?.Skills;
        if (descriptors == null || skills == null)
        {
            return;
        }

        var max = (float)SkillCapRuntime.MaxExperience;
        var fixedCount = 0;

        foreach (var descriptor in descriptors)
        {
            if (descriptor == null)
            {
                continue;
            }

            foreach (var skill in skills)
            {
                if (skill == null || skill.Id != descriptor.Id)
                {
                    continue;
                }

                // 原版这里已经被 clamp 到 5100，用技能对象里的真实 Current 覆盖回去
                var realProgress = Mathf.Clamp(skill.Current, 0f, max);
                if (!Mathf.Approximately(realProgress, descriptor.Progress))
                {
                    descriptor.Progress = realProgress;
                    fixedCount++;
                }

                break;
            }
        }

        if (fixedCount > 0)
        {
            SkillCap100Plugin.Verbose($"SkillsDescriptor：修正了 {fixedCount} 个技能的进度截断。");
        }
    }
}
