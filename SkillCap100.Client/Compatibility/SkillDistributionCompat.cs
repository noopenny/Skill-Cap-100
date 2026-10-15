using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;

namespace SkillCap100.Client.Compatibility;

/// <summary>
/// 与 <b>Skill Distribution</b>（<c>com.zgfuedkx.skilldistribution</c> / <c>SkillDistribution-Client.dll</c>）的兼容处理。
/// <para>
/// 它的客户端逻辑把「满级 / 精英」写死成
/// <c>SkillDistributionLogic.ELITE_LEVEL = 5100f</c>（= 原版 51 级）：
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>GetApplicableSkills</c> 只把 <c>Current &lt; 5100</c> 的技能当作可分发目标。
///     技能上限提到 100 级后，几乎所有技能都 ≥5100，于是目标列表恒为空
///     （日志 <c>Failed to distribute skill XP - no non-elite skills found! Abort...</c>），
///     <b>溢出经验完全不再分发</b> —— 这就是「练到 100 级后经验不再转移」的原因。
///   </description></item>
///   <item><description>
///     <c>AbstractSkillPatch.Postfix</c> 用 <c>Skill.IsEliteLevel</c> 决定是否分发（来源判定）。
///     <c>EliteMode = MaxLevel</c> 时它正好等于“练满”；但在 <c>Vanilla</c> / <c>VanillaAndEnhanced</c>
///     下它是 51 级，会让 51~99 级的正常升级经验也被分发出去。
///   </description></item>
/// </list>
/// <para>
/// 处理方式（都走 Transpiler，因为 <c>ELITE_LEVEL</c> 是 <c>static readonly</c>，
/// .NET 上类型初始化后无法用反射改写——已用 <c>moddev/_diag/SkillCompatProbe</c> 实测确认）：
/// </para>
/// <list type="number">
///   <item><description>在 <c>GetApplicableSkills</c> 里把 <c>ldsfld ELITE_LEVEL</c> 换成我们的经验上限
///     （<see cref="SkillCapRuntime.MaxExperienceSingle"/>）→ 目标 = “还没练满的技能”，
///     健身房的分发回退路径（<c>SelectSkills</c>）同样受益；</description></item>
///   <item><description>（可关）在 <c>AbstractSkillPatch.Postfix</c> 里把 <c>Skill.IsEliteLevel</c> 判定换成
///     “是否已达到经验上限”，恢复“只有溢出经验才分发”的原版语义。</description></item>
/// </list>
/// <para>所有步骤都是“尽力而为”：找不到类型/方法/IL 特征时只记日志，不影响本模组其它功能。</para>
/// </summary>
internal static class SkillDistributionCompat
{
    private const string LogicTypeName = "SkillDistribution.Features.SkillDistributionLogic";
    private const string PatchTypeName = "SkillDistribution.Patches.AbstractSkillPatch";
    private const string EliteLevelFieldName = "ELITE_LEVEL";
    private const string SourceGateMethodName = "Postfix";

    /// <summary>
    /// 所有读取 <c>ELITE_LEVEL</c> 的方法（用 <c>moddev/_diag/SkillCompatProbe</c> 全程序集扫描得到，各 1 处）：
    /// <list type="bullet">
    ///   <item><description><c>SkillDistributionLogic.GetApplicableSkills</c>：挑选分发目标（Current &lt; ELITE_LEVEL）</description></item>
    ///   <item><description><c>DistributionModes.WeightedRandomDistribution</c>：按等级加权时以 ELITE_LEVEL 为基准</description></item>
    /// </list>
    /// </summary>
    private static readonly (string TypeName, string MethodName)[] EliteLevelReaders =
    {
        (LogicTypeName, "GetApplicableSkills"),
        ("SkillDistribution.Features.DistributionModes", "WeightedRandomDistribution"),
    };

    /// <summary>是否检测到 Skill Distribution。</summary>
    internal static bool Detected { get; private set; }

    internal static void Apply(Harmony harmony, SkillCapConfig config)
    {
        var logicType = FindType(LogicTypeName);
        if (logicType == null)
        {
            SkillCap100Plugin.Verbose("未检测到 Skill Distribution，跳过兼容处理。");
            return;
        }

        Detected = true;

        // ① 必须：把它的“满级(精英)阈值”从硬编码 5100 换成我们的经验上限（两处读取点）
        foreach (var (typeName, methodName) in EliteLevelReaders)
        {
            PatchEliteLevelReader(harmony, typeName, methodName);
        }

        // ② 可选：只有练满的技能才分发（原版语义）
        if (config.SkillDistributionMaxedSourceOnly)
        {
            PatchSourceGate(harmony);
        }
        else
        {
            SkillCap100Plugin.Verbose("SkillDistributionMaxedSourceOnly = false：保留它原来的“精英即分发”判定。");
        }
    }

    /// <summary>分发来源判定：只有练满（Current ≥ 经验上限）的技能才把经验分发出去。</summary>
    public static bool IsSourceAtCap(Skill skill)
    {
        return skill != null && skill.Current >= SkillCapRuntime.MaxExperience;
    }

    private static void PatchEliteLevelReader(Harmony harmony, string typeName, string methodName)
    {
        var type = FindType(typeName);
        var method = FindMethod(type, methodName);
        if (method == null)
        {
            SkillCap100Plugin.Log?.LogWarning(
                $"检测到 Skill Distribution，但找不到 {typeName}.{methodName}；" +
                "它的满级阈值仍是 5100，溢出经验可能不会分发。请把日志反馈给模组作者。");
            return;
        }

        try
        {
            harmony.Patch(
                method,
                transpiler: new HarmonyMethod(GetTranspiler(nameof(TargetFilterTranspiler))));
            SkillCap100Plugin.Verbose($"[兼容] Skill Distribution：已接入 {typeName}.{methodName} 的阈值替换。");
        }
        catch (Exception ex)
        {
            SkillCap100Plugin.Log?.LogWarning($"给 Skill Distribution 的 {typeName}.{methodName} 打补丁失败：{ex.Message}");
        }
    }

    /// <summary>把 <c>ldsfld SkillDistributionLogic::ELITE_LEVEL</c> 换成我们的经验上限。</summary>
    private static IEnumerable<CodeInstruction> TargetFilterTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        var replacement = typeof(SkillCapRuntime).GetProperty(nameof(SkillCapRuntime.MaxExperienceSingle))!.GetGetMethod()!;
        var replaced = 0;

        foreach (var instruction in list)
        {
            if (instruction.opcode != OpCodes.Ldsfld
                || instruction.operand is not FieldInfo field
                || field.Name != EliteLevelFieldName)
            {
                continue;
            }

            instruction.opcode = OpCodes.Call;
            instruction.operand = replacement;
            replaced++;
        }

        if (replaced == 0)
        {
            SkillCap100Plugin.Log?.LogWarning(
                "Skill Distribution：未找到 ELITE_LEVEL 读取点，满级阈值仍是 5100（溢出经验可能不分发）。");
        }
        else
        {
            SkillCap100Plugin.Verbose(
                $"[兼容] Skill Distribution：{replaced} 处满级阈值 5100 → {SkillCapRuntime.MaxExperience:0}" +
                $"（{SkillCapRuntime.MaxLevel} 级），未练满的技能仍可作为分发目标。");
        }

        return list;
    }

    private static void PatchSourceGate(Harmony harmony)
    {
        var patchType = FindType(PatchTypeName);
        var method = FindMethod(patchType, SourceGateMethodName);
        if (method == null)
        {
            SkillCap100Plugin.Log?.LogWarning(
                $"检测到 Skill Distribution，但找不到 {PatchTypeName}.{SourceGateMethodName}；" +
                "分发来源判定保持不变（EliteMode = Vanilla* 时会在 51 级起分发）。");
            return;
        }

        try
        {
            harmony.Patch(
                method,
                transpiler: new HarmonyMethod(GetTranspiler(nameof(SourceGateTranspiler))));
        }
        catch (Exception ex)
        {
            SkillCap100Plugin.Log?.LogWarning($"给 Skill Distribution 打来源判定补丁失败：{ex.Message}");
        }
    }

    /// <summary>把 <c>callvirt Skill::get_IsEliteLevel</c> 换成 <see cref="IsSourceAtCap"/>。</summary>
    private static IEnumerable<CodeInstruction> SourceGateTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        var replacement = typeof(SkillDistributionCompat).GetMethod(nameof(IsSourceAtCap))!;
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
            SkillCap100Plugin.Log?.LogWarning(
                "Skill Distribution：未找到 IsEliteLevel 判定，来源判定未修改（Vanilla* 模式下 51 级起就会分发）。");
        }
        else
        {
            SkillCap100Plugin.Verbose($"[兼容] Skill Distribution：{replaced} 处来源判定改为“练满才分发”。");
        }

        return list;
    }

    private static MethodInfo GetTranspiler(string name)
    {
        return typeof(SkillDistributionCompat).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    }

    /// <summary>按名字找方法（不依赖 Harmony 的 AccessTools，纯反射更稳）。</summary>
    private static MethodInfo? FindMethod(Type? type, string name)
    {
        return type?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
    }

    /// <summary>在已加载的程序集里按全名找类型。</summary>
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
