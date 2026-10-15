using BepInEx.Configuration;

namespace SkillCap100.Client;

/// <summary>精英效果的解锁模式。</summary>
public enum EliteUnlockMode
{
    /// <summary>默认：只有练到 EliteUnlockLevel（默认 100 级）才解锁精英效果，51 级不再提前解锁。</summary>
    MaxLevel = 0,

    /// <summary>保留原版行为：51 级解锁精英效果（100 级不再额外强化）。</summary>
    Vanilla = 1,

    /// <summary>51 级照常解锁（原版），100 级练满后再把精英效果数值乘以 EliteBonusMultiplier。</summary>
    VanillaAndEnhanced = 2,
}

/// <summary>客户端插件配置（BepInEx .cfg，可在游戏内 ConfigurationManager 里热改）。</summary>
public sealed class SkillCapConfig
{
    public bool Enabled { get; private set; } = true;

    public int MaxSkillLevel { get; private set; } = 100;

    public bool ExtrapolateEffects { get; private set; } = true;

    public int EliteUnlockLevel { get; private set; } = 100;

    public EliteUnlockMode EliteMode { get; private set; } = EliteUnlockMode.MaxLevel;

    public float EliteBonusMultiplier { get; private set; } = 2f;

    public bool ExtrapolateStrengthSpeedCurve { get; private set; }

    /// <summary>是否自动与 Skill Distribution 模组对接（把它的“满级”阈值从 5100 改成我们的经验上限）。</summary>
    public bool SkillDistributionCompat { get; private set; } = true;

    /// <summary>Skill Distribution：是否只在技能“练满（CURRENT ≥ 经验上限）”时才把经验分发出去。</summary>
    public bool SkillDistributionMaxedSourceOnly { get; private set; } = true;

    public bool VerboseLogging { get; private set; }

    public static SkillCapConfig Bind(ConfigFile file)
    {
        var config = new SkillCapConfig
        {
            Enabled = file.Bind(
                "1. General", "Enabled", true,
                "总开关。false = 不安装任何 Harmony 补丁（等同未安装本插件）。").Value,

            MaxSkillLevel = file.Bind(
                "1. General", "MaxSkillLevel", 100,
                new ConfigDescription(
                    "技能等级上限。原版 = 51，默认 100。每级 100 点经验，所以经验上限 = MaxSkillLevel * 100。" +
                    "必须与服务端 config.json 的 maxSkillLevel 保持一致。",
                    new AcceptableValueRange<int>(51, 1000))).Value,

            ExtrapolateEffects = file.Bind(
                "1. General", "ExtrapolateEffects", true,
                "true = 52~100 级继续按原版每级增量线性叠加（技能效果真的变强）。" +
                "false = 只提升等级数字，技能效果停在 50 级水平（原版上限行为），可用于对比测试。").Value,

            EliteUnlockLevel = file.Bind(
                "2. Elite", "EliteUnlockLevel", 100,
                new ConfigDescription(
                    "精英（Elite）效果的解锁等级。原版 = 51。默认 100 = 必须练满才获得精英效果。" +
                    "仅当 EliteMode = MaxLevel 时生效。",
                    new AcceptableValueRange<int>(51, 1000))).Value,

            EliteMode = file.Bind(
                "2. Elite", "EliteMode", EliteUnlockMode.MaxLevel,
                "精英效果解锁策略：MaxLevel = 只在 EliteUnlockLevel 解锁（默认，51 级不再提前解锁）；" +
                "Vanilla = 保留 51 级解锁；VanillaAndEnhanced = 51 级解锁 + 100 级练满后再强化 EliteBonusMultiplier 倍。").Value,

            EliteBonusMultiplier = file.Bind(
                "2. Elite", "EliteBonusMultiplier", 2f,
                new ConfigDescription(
                    "EliteMode = VanillaAndEnhanced 时，练满 MaxSkillLevel 后精英效果数值的倍率。" +
                    "概率类精英效果会被限制在 100% 以内。",
                    new AcceptableValueRange<float>(1f, 10f))).Value,

            ExtrapolateStrengthSpeedCurve = file.Bind(
                "3. Compatibility", "ExtrapolateStrengthSpeedCurve", false,
                "高级选项。力量(Strength)影响行走/冲刺速度的那条曲线在客户端被硬编码为 SummaryLevel/60" +
                "并用 Mathf.Lerp（自带 0~1 截断），所以它在 60 级之后就不再增长。" +
                "true = 额外补丁 MovementContext.MaxSpeed / SprintingSpeed 做外推（可能触发客户端\"Too high speed\"日志）。" +
                "默认 false = 保持原版，只提升等级数字。").Value,

            SkillDistributionCompat = file.Bind(
                "3. Compatibility", "SkillDistributionCompat", true,
                "检测到 Skill Distribution（com.zgfuedkx.skilldistribution）时自动对接：" +
                "把它的满级(精英)阈值从硬编码的 5100（=51 级）改成我们的经验上限（默认 10000）。" +
                "不改的话，所有 ≥51 级的技能都会被它当成“已满级”而排除，结果是溢出经验完全没有分发目标。" +
                "false = 不做任何对接（Skill Distribution 保持原样，可能与 100 级上限不兼容）。").Value,

            SkillDistributionMaxedSourceOnly = file.Bind(
                "3. Compatibility", "SkillDistributionMaxedSourceOnly", true,
                "Skill Distribution 的分发来源判定：true = 只有“练满（达到 MaxSkillLevel）”的技能才把经验分发出去" +
                "（即原版语义的溢出经验；EliteMode = Vanilla/VanillaAndEnhanced 时它原本会在 51 级就开始分发）。" +
                "false = 沿用它的原始判定（精英即分发，配合 Vanilla* 模式会是 51 级起分发）。").Value,

            VerboseLogging = file.Bind(
                "4. Debug", "VerboseLogging", false,
                "输出详细的补丁/IL 日志，排查问题时打开。").Value,
        };

        // 简单的一致性修正
        if (config.EliteUnlockLevel > config.MaxSkillLevel)
        {
            config.EliteUnlockLevel = config.MaxSkillLevel;
        }

        return config;
    }
}
