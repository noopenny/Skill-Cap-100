using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SkillCap100.Verify;

/// <summary>
/// 静态补丁校验器。用法：
/// <code>
/// SkillCap100.Verify.exe [GameRoot] [--plugin &lt;SkillCap100.Client.dll&gt;] [--mod &lt;SkillCap100.Server.dll&gt;]
/// </code>
/// 退出码 0 = 全部通过；1 = 有检查项失败。
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _checks;
    private static DefaultAssemblyResolver _resolver = null!;

    private static int Main(string[] args)
    {
        var gameRoot = args.Length > 0 && !args[0].StartsWith("--")
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

        string? pluginPath = GetOption(args, "--plugin");
        string? modPath = GetOption(args, "--mod");

        var clientAssemblyPath = Path.Combine(gameRoot, "EscapeFromTarkov_Data", "Managed", "Assembly-CSharp.dll");
        var serverAssemblyPath = Path.Combine(gameRoot, "SPT_Runtime", "SPTarkov.Server.Core.dll");

        // 读取插件/模组的 CustomAttribute 时 Cecil 需要解析 0Harmony / SemanticVersioning 等引用程序集
        _resolver = new DefaultAssemblyResolver();
        foreach (var dir in new[]
                 {
                     Path.Combine(gameRoot, "BepInEx", "core"),
                     Path.Combine(gameRoot, "EscapeFromTarkov_Data", "Managed"),
                     Path.Combine(gameRoot, "SPT_Runtime"),
                 })
        {
            if (Directory.Exists(dir))
            {
                _resolver.AddSearchDirectory(dir);
            }
        }

        var readerParameters = new ReaderParameters { AssemblyResolver = _resolver };

        Console.WriteLine("=== SkillCap100 静态补丁校验 ===");
        Console.WriteLine($"GameRoot          : {gameRoot}");
        Console.WriteLine($"Assembly-CSharp   : {clientAssemblyPath}");
        Console.WriteLine($"SPTarkov.Server.Core: {serverAssemblyPath}");
        Console.WriteLine();

        if (!File.Exists(clientAssemblyPath) || !File.Exists(serverAssemblyPath))
        {
            Console.Error.WriteLine("找不到目标程序集，请把 GameRoot 作为第一个参数传入。");
            return 2;
        }

        using var client = AssemblyDefinition.ReadAssembly(clientAssemblyPath, readerParameters);
        using var server = AssemblyDefinition.ReadAssembly(serverAssemblyPath, readerParameters);

        CheckClientTargets(client);
        CheckClientIl(client);
        CheckServerTargets(server);
        CheckServerIl(server);

        if (pluginPath != null)
        {
            CheckHarmonyAttributes(pluginPath, client, readerParameters);
        }

        if (modPath != null)
        {
            CheckServerModPatches(modPath, readerParameters);
        }

        Console.WriteLine();
        Console.WriteLine($"=== 结果：{_checks - _failures}/{_checks} 项通过，{_failures} 项失败 ===");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 客户端

    private static void CheckClientTargets(AssemblyDefinition assembly)
    {
        Console.WriteLine("--- 客户端补丁目标 ---");

        ExpectMethod(assembly, "EFT.BaseSkill", "ClampCurrent", new[] { "System.Single" }, "System.Single",
            "SkillCapRuntime 需要完整替换它（原版 Mathf.Clamp(value, 0, 5100)）");

        ExpectPropertyGetter(assembly, "EFT.BaseSkill", "SummaryLevel",
            "BaseSkillSummaryLevelPatch：原版 Mathf.Min(Buff>0?60:51, Level+Buff)");

        ExpectPropertyGetter(assembly, "EFT.Skill", "IsEliteLevel",
            "SkillIsEliteLevelPatch：原版 Level > 50");

        ExpectMethod(assembly, "EFT.Skill", "UpdateRules", Array.Empty<string>(), "System.Void",
            "SkillUpdateRulesTranspilerPatch：基础效果叠加 + 精英解锁时机");

        ExpectMethod(assembly, "EFT.SkillsDescriptor", ".ctor", new[] { "EFT.SkillManager" }, "System.Void",
            "SkillsDescriptorClampPatch：客户端把 5100 截断后的技能发给服务端");

        ExpectPropertyGetter(assembly, "EFT.MovementContext", "MaxSpeed",
            "可选：力量速度曲线外推");
        ExpectPropertyGetter(assembly, "EFT.MovementContext", "SprintingSpeed",
            "可选：力量速度曲线外推");
        Console.WriteLine();
    }

    private static void CheckClientIl(AssemblyDefinition assembly)
    {
        Console.WriteLine("--- 客户端 IL 常量检查 ---");

        var clampCurrent = FindMethod(assembly, "EFT.BaseSkill", "ClampCurrent", new[] { "System.Single" });
        if (clampCurrent != null)
        {
            var hits = CountConstants(clampCurrent, 5100f);
            Expect(hits >= 1, $"BaseSkill.ClampCurrent 中含 ldc.r4 5100（找到 {hits} 处），说明经验上限仍是 51 级封顶");
        }

        var updateRules = FindMethod(assembly, "EFT.Skill", "UpdateRules", Array.Empty<string>());
        if (updateRules != null)
        {
            var fifty = CountIntConstants(updateRules, 50);
            Expect(fifty == 3,
                $"Skill.UpdateRules 中常量 50 出现 3 次（① Math.Min(SummaryLevel,50) ② SummaryLevel>50 ③ Level>50），实际 {fifty} 次");
            Console.WriteLine("    Skill.UpdateRules IL：");
            foreach (var line in DumpIl(updateRules))
            {
                Console.WriteLine($"      {line}");
            }
        }

        var skillsDescriptor = assembly.MainModule.GetType("EFT.SkillsDescriptor");
        if (skillsDescriptor != null)
        {
            var carriers = new List<string>();
            foreach (var type in EnumerateSelfAndNested(skillsDescriptor))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    if (CountConstants(method, 5100f) > 0)
                    {
                        carriers.Add($"{type.FullName}::{method.Name}");
                    }
                }
            }

            Console.WriteLine($"    含 ldc.r4 5100 的方法：{(carriers.Count == 0 ? "（无，可能已内联到别处）" : string.Join(" | ", carriers))}");
        }

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 服务端

    private static void CheckServerTargets(AssemblyDefinition assembly)
    {
        Console.WriteLine("--- 服务端补丁目标 ---");

        ExpectMethod(assembly, "SPTarkov.Server.Core.Helpers.Profile.ProfileHelper", "HasEliteSkillLevel",
            new[] { "SPTarkov.Server.Core.Models.Enums.SkillTypes", "SPTarkov.Server.Core.Models.Eft.Common.PmcData" },
            "System.Boolean", "HasEliteSkillLevelPatch：原版 Progress >= 5100");

        ExpectMethod(assembly, "SPTarkov.Server.Core.Helpers.Profile.ProfileHelper", "AddSkillPointsToPlayer",
            new[]
            {
                "SPTarkov.Server.Core.Models.Eft.Common.PmcData",
                "SPTarkov.Server.Core.Models.Enums.SkillTypes",
                "System.Double", "System.Boolean", "System.Boolean",
            },
            "System.Void", "AddSkillPointsToPlayerPatch：Transpiler 替换 5100 常量");

        ExpectMethod(assembly, "SPTarkov.Server.Core.Models.Eft.Common.Tables.CommonSkill", "GetSafeSkillProgress",
            new[] { "System.Double" }, "System.Double",
            "CommonSkillProgressPatch：Progress/PointsEarnedDuringSession 的 setter 会 clamp 到 5100");

        ExpectMethod(assembly, "SPTarkov.Server.Core.Migration.Migrations.Fixes.InvalidSkillProgressFix", "NeedsFixing",
            new[] { "System.Double" }, "System.Boolean",
            "InvalidSkillProgressNeedsFixingPatch：档案迁移会把 > 5100 判为非法");

        ExpectMethod(assembly, "SPTarkov.Server.Core.Migration.Migrations.Fixes.InvalidSkillProgressFix", "GetFixedValue",
            new[] { "System.Double" }, "System.Double",
            "InvalidSkillProgressGetFixedValuePatch：修正值 clamp 到 5100");

        Console.WriteLine();
    }

    private static void CheckServerIl(AssemblyDefinition assembly)
    {
        Console.WriteLine("--- 服务端 IL 常量扫描（所有 ldc.r8 5100 / ldc.i4 5100 出现位置）---");

        var found = 0;
        foreach (var type in assembly.MainModule.Types)
        {
            ScanType(type, ref found);
        }

        Console.WriteLine($"    共找到 {found} 处 5100 常量。SkillCap100.Server 的覆盖情况：");
        Console.WriteLine("      ProfileHelper.AddSkillPointsToPlayer (2 处)   -> Transpiler 常量替换");
        Console.WriteLine("      ProfileHelper.HasEliteSkillLevel (1 处)      -> Prefix 整体替换");
        Console.WriteLine("      CommonSkill.GetSafeSkillProgress (1 处)      -> Prefix 整体替换");
        Console.WriteLine("      InvalidSkillProgressFix.NeedsFixing (1 处)   -> Prefix 整体替换");
        Console.WriteLine("      InvalidSkillProgressFix.GetFixedValue (1 处) -> Prefix 整体替换");
        Console.WriteLine("      HideoutController 健身房 lambda (1 处)       -> Transpiler 常量替换（动态定位）");
        Expect(found == 7, $"服务端 5100 常量数量与文档记录不一致（期望 7，实际 {found}）；" +
                           "如果 SPT 升级导致数量变化，请检查是否有新的技能上限截断点需要补丁。");
        Console.WriteLine();

        Console.WriteLine("--- 服务端“技能/精英”相关方法中的整数常量 51/50 扫描（仅提示，防止漏掉按等级判定的硬编码）---");
        var intHits = new List<string>();
        foreach (var type in assembly.MainModule.Types)
        {
            ScanIntThresholds(type, intHits);
        }

        if (intHits.Count == 0)
        {
            Console.WriteLine("      （无）");
        }
        else
        {
            foreach (var hit in intHits)
            {
                Console.WriteLine($"      {hit}");
            }
        }

        Console.WriteLine();
    }

    private static void ScanIntThresholds(TypeDefinition type, List<string> hits)
    {
        foreach (var nested in type.NestedTypes)
        {
            ScanIntThresholds(nested, hits);
        }

        var skillRelated = type.Name.Contains("Skill", StringComparison.OrdinalIgnoreCase);

        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            if (!skillRelated && !method.Name.Contains("Skill", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var count = method.Body.Instructions.Count(i =>
                (i.OpCode == OpCodes.Ldc_I4 && i.Operand is int v && (v == 51 || v == 50))
                || (i.OpCode == OpCodes.Ldc_I4_S && i.Operand is sbyte sb && (sb == 51 || sb == 50)));

            if (count > 0)
            {
                hits.Add($"{type.FullName}::{method.Name} -> {count} 处 (50/51)");
            }
        }
    }

    private static void ScanType(TypeDefinition type, ref int found)
    {
        foreach (var nested in type.NestedTypes)
        {
            ScanType(nested, ref found);
        }

        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            var hits = method.Body.Instructions.Where(i =>
                (i.OpCode == OpCodes.Ldc_R8 && i.Operand is double d && Math.Abs(d - 5100d) < 0.0001d)
                || (i.OpCode == OpCodes.Ldc_I4 && i.Operand is int v && v == 5100)).ToList();

            if (hits.Count > 0)
            {
                found += hits.Count;
                Console.WriteLine($"      {type.FullName}::{method.Name} -> {hits.Count} 处");
            }
        }
    }

    // ---------------------------------------------------------------- 插件特性校验

    private static void CheckHarmonyAttributes(string pluginPath, AssemblyDefinition target, ReaderParameters readerParameters)
    {
        Console.WriteLine("--- 已编译插件的 [HarmonyPatch] 目标解析 ---");

        if (!File.Exists(pluginPath))
        {
            Expect(false, $"插件不存在：{pluginPath}");
            return;
        }

        using var plugin = AssemblyDefinition.ReadAssembly(pluginPath, readerParameters);
        foreach (var type in plugin.MainModule.Types)
        {
            var attribute = type.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "HarmonyPatch");
            if (attribute == null)
            {
                continue;
            }

            string resolved;
            try
            {
                resolved = ResolveHarmonyPatch(attribute, target) ?? DescribeHarmonyPatch(attribute);
            }
            catch (Exception ex)
            {
                resolved = $"FAIL 解析特性失败：{ex.Message}";
            }

            if (attribute.ConstructorArguments.Count == 0)
            {
                // 参数为空的 [HarmonyPatch] = 目标方法由 [HarmonyTargetMethod] 动态决定，
                // 静态工具无法解析，这里只做提示（其目标已由前面的 IL 扫描覆盖）。
                var dynamicTarget = type.Methods.FirstOrDefault(m =>
                    m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyTargetMethod"));
                Console.WriteLine($"      INFO {type.Name} 使用动态目标 ({(dynamicTarget?.Name ?? "未找到 HarmonyTargetMethod")})，跳过静态解析");
                continue;
            }

            Expect(!resolved.StartsWith("FAIL"), $"{type.Name} -> {resolved}");
            if (!resolved.StartsWith("FAIL"))
            {
                Console.WriteLine($"      OK  {type.Name} -> {resolved}");
            }
        }

        Console.WriteLine();
    }

    private static void CheckServerModPatches(string modPath, ReaderParameters readerParameters)
    {
        Console.WriteLine("--- 服务端模组自检 ---");
        if (!File.Exists(modPath))
        {
            Expect(false, $"服务端模组不存在：{modPath}");
            return;
        }

        using var mod = AssemblyDefinition.ReadAssembly(modPath, readerParameters);
        var metadata = mod.MainModule.Types.FirstOrDefault(t =>
            t.Interfaces.Any(i => i.InterfaceType.Name == "IModMetadata"));
        Expect(metadata != null, "服务端模组必须包含一个 IModMetadata 实现，否则 SPT 不会加载它");
        if (metadata != null)
        {
            Console.WriteLine($"      IModMetadata: {metadata.FullName}");
        }

        var patchTypes = mod.MainModule.Types
            .Where(t => t.BaseType != null && t.BaseType.Name == "AbstractPatch")
            .Select(t => t.Name)
            .ToList();
        Expect(patchTypes.Count > 0, "服务端模组应包含 AbstractPatch 派生类");
        Console.WriteLine($"      AbstractPatch 子类：{string.Join(", ", patchTypes)}");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 辅助

    private static string? ResolveHarmonyPatch(CustomAttribute attribute, AssemblyDefinition target)
    {
        TypeReference? declaringType = null;
        string? methodName = null;
        string? methodType = null;
        var argumentTypes = new List<string>();

        foreach (var arg in attribute.ConstructorArguments)
        {
            switch (arg.Value)
            {
                case TypeReference typeRef when declaringType == null && typeRef.Name != "MethodType":
                    declaringType = typeRef;
                    break;
                case string name:
                    methodName = name;
                    break;
                case CustomAttributeArgument[] array:
                    argumentTypes.AddRange(array.Where(a => a.Value is TypeReference)
                        .Select(a => ((TypeReference)a.Value).FullName));
                    break;
                case int methodTypeValue when declaringType != null && methodName == null:
                    // HarmonyLib.MethodType: Normal=0, Getter=1, Setter=2, Constructor=3, StaticConstructor=4, Enumerator=5
                    methodType = methodTypeValue switch
                    {
                        1 => "Getter",
                        2 => "Setter",
                        3 => "Constructor",
                        4 => "StaticConstructor",
                        _ => null,
                    };
                    break;
            }
        }

        if (declaringType == null)
        {
            return null;
        }

        var targetType = target.MainModule.GetType(declaringType.FullName);
        if (targetType == null)
        {
            return $"FAIL 找不到类型 {declaringType.FullName}";
        }

        if (methodType == "Constructor")
        {
            var ctor = targetType.Methods.FirstOrDefault(m => m.IsConstructor
                && (argumentTypes.Count == 0 || m.Parameters.Count == argumentTypes.Count));
            return ctor != null ? $"{targetType.FullName}::.ctor({string.Join(", ", ctor.Parameters.Select(p => p.ParameterType.Name))})" : "FAIL 找不到构造函数";
        }

        var expectedName = methodType switch
        {
            "Getter" => $"get_{methodName}",
            "Setter" => $"set_{methodName}",
            _ => methodName,
        };

        var method = targetType.Methods.FirstOrDefault(m => m.Name == expectedName);
        return method != null ? $"{targetType.FullName}::{method.Name}" : $"FAIL 找不到方法 {expectedName}";
    }

    private static string DescribeHarmonyPatch(CustomAttribute attribute)
    {
        return $"[HarmonyPatch({string.Join(", ", attribute.ConstructorArguments.Select(a => a.Value?.ToString() ?? "null"))})]";
    }

    private static IEnumerable<TypeDefinition> EnumerateSelfAndNested(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
        {
            foreach (var inner in EnumerateSelfAndNested(nested))
            {
                yield return inner;
            }
        }
    }

    private static void ExpectMethod(AssemblyDefinition assembly, string typeName, string methodName,
        string[] parameterTypes, string returnType, string purpose)
    {
        var type = assembly.MainModule.GetType(typeName);
        if (type == null)
        {
            Expect(false, $"{typeName} 不存在（{purpose}）");
            return;
        }

        var method = type.Methods.FirstOrDefault(m =>
            m.Name == methodName
            && m.Parameters.Count == parameterTypes.Length
            && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes));

        if (method == null)
        {
            var candidates = string.Join(" | ", type.Methods.Where(m => m.Name == methodName)
                .Select(m => $"{m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName))})"));
            Expect(false, $"找不到 {typeName}.{methodName}({string.Join(", ", parameterTypes)}) —— {purpose}。现有重载：{candidates}");
            return;
        }

        var returnOk = method.ReturnType.FullName == returnType;
        Expect(returnOk, $"{typeName}.{methodName} 返回值应为 {returnType}，实际 {method.ReturnType.FullName}（{purpose}）");
        if (returnOk)
        {
            Console.WriteLine($"      OK  {typeName}.{methodName}({string.Join(", ", parameterTypes)}) -> {method.ReturnType.Name}  [{method.Attributes & MethodAttributes.MemberAccessMask}]");
            // Harmony 的 Prefix/Postfix 按“参数名”绑定原方法参数（如 float value / SkillManager skillManager），
            // 这里把真实参数名打印出来，便于确认补丁签名没有写错。
            var parameterNames = string.Join(", ", method.Parameters.Select(p => $"{p.Name}:{p.ParameterType.Name}"));
            Console.WriteLine($"          参数名 -> {parameterNames}");
        }
    }

    private static void ExpectPropertyGetter(AssemblyDefinition assembly, string typeName, string propertyName, string purpose)
    {
        var type = assembly.MainModule.GetType(typeName);
        var getter = type?.Methods.FirstOrDefault(m => m.Name == $"get_{propertyName}");
        Expect(getter != null, $"找不到 {typeName}.get_{propertyName} —— {purpose}");
        if (getter != null)
        {
            Console.WriteLine($"      OK  {typeName}.get_{propertyName} -> {getter.ReturnType.Name}");
        }
    }

    private static MethodDefinition? FindMethod(AssemblyDefinition assembly, string typeName, string methodName, string[] parameterTypes)
    {
        return assembly.MainModule.GetType(typeName)?.Methods.FirstOrDefault(m =>
            m.Name == methodName
            && m.Parameters.Count == parameterTypes.Length
            && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes));
    }

    private static int CountConstants(MethodDefinition method, float value)
    {
        if (!method.HasBody)
        {
            return 0;
        }

        return method.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldc_R4 && i.Operand is float f && Math.Abs(f - value) < 0.001f);
    }

    private static int CountIntConstants(MethodDefinition method, int value)
    {
        if (!method.HasBody)
        {
            return 0;
        }

        return method.Body.Instructions.Count(i =>
            (i.OpCode == OpCodes.Ldc_I4 && i.Operand is int v && v == value)
            || (i.OpCode == OpCodes.Ldc_I4_S && i.Operand is sbyte sb && sb == value));
    }

    private static IEnumerable<string> DumpIl(MethodDefinition method)
    {
        if (!method.HasBody)
        {
            yield break;
        }

        foreach (var instruction in method.Body.Instructions)
        {
            yield return instruction.ToString();
        }
    }

    private static void Expect(bool condition, string message)
    {
        _checks++;
        if (condition)
        {
            return;
        }

        _failures++;
        Console.WriteLine($"      FAIL {message}");
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
