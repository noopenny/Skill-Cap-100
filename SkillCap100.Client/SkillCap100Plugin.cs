using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SkillCap100.Client.Patches;

namespace SkillCap100.Client;

/// <summary>
/// BepInEx 插件入口。
/// <para>
/// 加载顺序：BepInEx 先加载 spt-core（SPT 自己的客户端补丁），再加载本插件；
/// 通过 <see cref="BepInDependencyAttribute"/> 声明依赖，保证 SPT 已经就绪。
/// </para>
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("com.SPT.core")]
public sealed class SkillCap100Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.skillcap100.client";
    public const string PluginName = "Skill Cap 100 (Client)";
    public const string PluginVersion = "1.0.5";

    internal static ManualLogSource Log = null!;

    internal static SkillCapConfig Settings = new();

    private Harmony? _harmony;

    private void Awake()
    {
        Log = Logger;
        Settings = SkillCapConfig.Bind(Config);
        SkillCapRuntime.Apply(Settings);

        Log.LogInfo($"{PluginName} v{PluginVersion} 加载中……");
        Log.LogInfo($"技能上限 = {SkillCapRuntime.MaxLevel} 级 / {SkillCapRuntime.MaxExperience:0} 经验；" +
                    $"效果外推 = {SkillCapRuntime.ExtrapolateEffects}；精英效果解锁 = {SkillCapRuntime.EffectiveEliteUnlockLevel} 级" +
                    $"（模式 {Settings.EliteMode}" +
                    $"{(SkillCapRuntime.EnhanceEliteAtMaxLevel ? $"，满 {SkillCapRuntime.MaxLevel} 级再强化 ×{SkillCapRuntime.EliteBonusMultiplier}" : string.Empty)}）");
        Log.LogInfo($"精英标记 / 健身房可练判定 = 练满 {SkillCapRuntime.MaxLevel} 级（IsEliteLevel 语义，v1.0.3 修正）");

        if (!Settings.Enabled)
        {
            Log.LogWarning("Enabled=false，未安装任何补丁（技能效果保持原版 51 级封顶）。");
            return;
        }

        _harmony = new Harmony(PluginGuid);

        var patchTypes = new List<Type>
        {
            // 1) 经验上限 5100 -> 10000（BaseSkill.ClampCurrent）
            typeof(BaseSkillClampCurrentPatch),
            // 2) SummaryLevel 上限 51/60 -> 100/109（决定 UI 显示等级与部分系统读取的等级）
            typeof(BaseSkillSummaryLevelPatch),
            // 3) 精英判定 51 -> 100（Skill.IsEliteLevel）
            typeof(SkillIsEliteLevelPatch),
            // 4) 技能效果逐级叠加 + 精英效果解锁时机（Skill.UpdateRules）
            typeof(SkillUpdateRulesPatch),
            // 5) 客户端把技能同步给服务端时的 5100 截断（SkillsDescriptor）
            typeof(SkillsDescriptorLambdaClampPatch),
            typeof(SkillsDescriptorCtorClampPatch),

        };

        if (Settings.ExtrapolateStrengthSpeedCurve)
        {
            patchTypes.Add(typeof(MovementContextMaxSpeedPatch));
            patchTypes.Add(typeof(MovementContextSprintingSpeedPatch));
        }

        var applied = 0;
        foreach (var type in patchTypes)
        {
            try
            {
                var patched = _harmony.CreateClassProcessor(type).Patch();
                applied++;
                Log.LogInfo($"补丁 {type.Name} 已启用，目标：{string.Join(", ", patched.ConvertAll(m => m.FullDescription()))}");
            }
            catch (Exception ex)
            {
                // 补丁失败不能拖垮游戏：记录错误并继续
                Log.LogError($"补丁 {type.Name} 启用失败（该功能不会生效）：{ex}");
            }
        }

        // 7) 刺激剂（Stimulator）技能等级 buff 的边界修正：
        //    原版把这个 buff 钳制在 (-Level, 60-Level)，100 级时上界变负 → 满级后吃药会“掉到 60 级”
        var stimulatorPatched = false;
        try
        {
            stimulatorPatched = Patches.StimulatorSkillBuffPatch.Apply(_harmony);
        }
        catch (Exception ex)
        {
            Log.LogError($"刺激剂技能 buff 修正安装失败（不影响其它功能）：{ex}");
        }

        // 6) UI：精英行/ELITE 标记按「精英效果是否生效」显示，而不是按「是否练满」
        //    （否则 51~99 级提示框会把精英行整行隐藏，看起来像精英效果没了）
        var uiApplied = 0;
        var uiTotal = 0;
        try
        {
            uiApplied = Patches.EliteUiStatePatches.Apply(_harmony, out uiTotal);
        }
        catch (Exception ex)
        {
            Log.LogError($"UI 精英状态补丁安装失败（不影响其它功能）：{ex}");
        }

        Log.LogInfo($"完成：{applied}/{patchTypes.Count} 个客户端补丁、{uiApplied}/{uiTotal} 个 UI 精英状态补丁、刺激剂技能 buff 修正 {(stimulatorPatched ? "已启用" : "未启用")}。");

        // 与 Skill Distribution 对接（它把“满级”写死为 5100，与 100 级上限冲突）
        if (Settings.SkillDistributionCompat)
        {
            try
            {
                Compatibility.SkillDistributionCompat.Apply(_harmony, Settings);
            }
            catch (Exception ex)
            {
                Log.LogError($"Skill Distribution 兼容处理失败（不影响其它功能）：{ex}");
            }
        }
    }

    private void OnDestroy()
    {
        try
        {
            _harmony?.UnpatchSelf();
        }
        catch (Exception ex)
        {
            Log?.LogError($"卸载补丁失败：{ex}");
        }
    }

    internal static void Verbose(string message)
    {
        if (SkillCapRuntime.VerboseLogging)
        {
            Log.LogInfo(message);
        }
    }
}
