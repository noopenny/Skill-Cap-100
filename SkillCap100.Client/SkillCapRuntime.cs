using UnityEngine;

namespace SkillCap100.Client;

/// <summary>
/// 客户端补丁运行时参数。
/// <para>
/// Transpiler 会把原版 IL 里的常量替换成对这里 <c>public static</c> 属性的调用，
/// 因此这些成员必须是 <c>public</c>（跨程序集可见），否则 JIT 会抛 MethodAccessException。
/// </para>
/// <para>原版相关代码（Assembly-CSharp.dll, EFT 命名空间）：</para>
/// <code>
/// // EFT.BaseSkill
/// public const int  MAX_LEVEL_W_BUFF = 60;
/// public int Level =&gt; GetLevelForValue(ClampCurrent(Current));
/// public int SummaryLevel =&gt; Mathf.Min((Buff &gt; 0) ? 60 : 51, Level + Buff);
/// public float ClampCurrent(float value) =&gt; Mathf.Clamp(value, 0f, 5100f);
///
/// // EFT.Skill
/// public const int MAX_LEVEL = 50;
/// public bool IsEliteLevel =&gt; Level &gt; 50;
/// public void UpdateRules()
/// {
///     int obj = Math.Min(SummaryLevel, 50);                       // ① 基础效果等级上限
///     foreach (var buff in Buffs) buff.BaseRuleFunc?.Invoke(obj);
///     if (SummaryLevel &gt; 50)                                      // ② 精英效果阈值
///     {
///         foreach (var buff in Buffs) buff.EliteRuleFunc?.Invoke();
///         if (Level &gt; 50) Unsubscribe();                           // ③ 满级后停止累计经验
///     }
/// }
/// </code>
/// </summary>
public static class SkillCapRuntime
{
    /// <summary>技能等级上限（原版 51）。</summary>
    public static int MaxLevel { get; private set; } = 100;

    /// <summary>技能经验上限 = MaxLevel * 100（原版 5100）。</summary>
    public static double MaxExperience => MaxLevel * 100d;

    /// <summary>同上，float 版本 —— Transpiler 注入 IL 时 <c>ldc.r4 5100</c> 只能换成返回 float 的调用。</summary>
    public static float MaxExperienceSingle => (float)MaxExperience;

    /// <summary>
    /// 带“技能等级提升 Buff”（隐藏加成，例如藏身处技能加成）时的等级上限。
    /// 原版为 60，即比 51 多 9 级余量；这里保持同样的 +9 余量。
    /// </summary>
    public static int MaxLevelWithBuff { get; private set; } = 109;

    /// <summary>
    /// 对应 <c>Skill.UpdateRules</c> 中的 <c>Math.Min(SummaryLevel, 50)</c>：
    /// 传给 <c>BaseRuleFunc</c> 的等级，决定基础效果的线性叠加终点。
    /// <list type="bullet">
    ///   <item><description>外推开启：= MaxLevel（100），于是 100 级时 <c>PerLevel(f)</c> 型效果 = 100*f，
    ///   <c>Max(f)</c> 型效果 = f * 100/50 = 2f，正好是 50 级的线性延长。</description></item>
    ///   <item><description>外推关闭：= 50，效果停在原版满级水平（只涨等级数字）。</description></item>
    /// </list>
    /// </summary>
    public static int BaseEffectLevelCap { get; private set; } = 100;

    /// <summary>
    /// 精英效果解锁等级（用于显示/文档）。原版 = 51（<c>Level &gt; 50</c>）。
    /// </summary>
    public static int EliteUnlockLevel { get; private set; } = 100;

    /// <summary>
    /// 对应 <c>Skill.UpdateRules</c> 中的 <c>if (SummaryLevel &gt; 50)</c> 那个常量。
    /// 注意原版这里是 <b>大于</b> 比较，所以注入值必须比目标解锁等级小 1：
    /// 解锁 100 级 =&gt; 注入 99。
    /// </summary>
    public static int EliteRuleComparisonThreshold { get; private set; } = 99;

    /// <summary>
    /// 对应 <c>Skill.UpdateRules</c> 中的 <c>if (Level &gt; 50) Unsubscribe();</c> 那个常量。
    /// 同样是 <b>大于</b> 比较，注入 MaxLevel - 1 = 99，即练到 100 级后停止继续累计经验
    /// （原版是 51 级后停止，那会让技能在 51 级就彻底不再涨经验）。
    /// </summary>
    public static int UnsubscribeComparisonThreshold { get; private set; } = 99;

    /// <summary>
    /// <b>精英「效果」</b>的解锁等级（只用于 <c>Skill.UpdateRules</c> 的精英规则阈值）：
    /// <list type="bullet">
    ///   <item><description><c>EliteMode = MaxLevel</c> → 配置里的 <c>EliteUnlockLevel</c>（默认 100）</description></item>
    ///   <item><description><c>EliteMode = Vanilla</c> / <c>VanillaAndEnhanced</c> → <b>51</b>（保留原版解锁时机）</description></item>
    /// </list>
    /// <para>
    /// <b>注意不要把它用在 <c>Skill.IsEliteLevel</c> 上</b>：那个属性在原版里是「是否练满」的意思，
    /// 健身房的技能筛选、技能面板的 maxed/进度显示、以及 SkillDistribution 等模组都按这个语义使用它
    /// （见 <see cref="Patches.SkillIsEliteLevelPatch"/>）。它用的是等级上限 <see cref="MaxLevel"/>。
    /// </para>
    /// </summary>
    public static int EffectiveEliteUnlockLevel { get; private set; } = 100;

    /// <summary><c>EliteMode = VanillaAndEnhanced</c>：练满后再次强化精英效果。</summary>
    public static bool EnhanceEliteAtMaxLevel { get; private set; }

    /// <summary>精英效果强化倍率（仅 VanillaAndEnhanced）。</summary>
    public static float EliteBonusMultiplier { get; private set; } = 2f;

    /// <summary>是否外推力量的速度曲线（默认 false，见配置说明）。</summary>
    public static bool ExtrapolateStrengthSpeedCurve { get; private set; }

    public static bool VerboseLogging { get; private set; }

    /// <summary>原版把力量速度曲线归一化到 60 级（<c>SummaryLevel / 60f</c>）。</summary>
    public const float VanillaStrengthSpeedNormalisation = 60f;

    public static void Apply(SkillCapConfig config)
    {
        MaxLevel = Math.Max(51, config.MaxSkillLevel);
        MaxLevelWithBuff = MaxLevel + 9; // 原版 51 -> 60 的余量
        ExtrapolateEffects = config.ExtrapolateEffects;

        // ① 基础效果上限
        BaseEffectLevelCap = config.ExtrapolateEffects ? MaxLevel : 50;

        EliteUnlockLevel = Math.Clamp(config.EliteUnlockLevel, 51, MaxLevel);

        // ② 精英阈值：MaxLevel 模式用配置值；Vanilla / VanillaAndEnhanced 保持原版 51 级
        EffectiveEliteUnlockLevel = config.EliteMode == EliteUnlockMode.MaxLevel ? EliteUnlockLevel : 51;
        EliteRuleComparisonThreshold = EffectiveEliteUnlockLevel - 1;

        // ③ 停止累计经验的等级：始终是 MaxLevel，这样 52~100 级才能继续涨经验
        UnsubscribeComparisonThreshold = MaxLevel - 1;

        EnhanceEliteAtMaxLevel = config.EliteMode == EliteUnlockMode.VanillaAndEnhanced;
        EliteBonusMultiplier = config.EliteBonusMultiplier;
        ExtrapolateStrengthSpeedCurve = config.ExtrapolateStrengthSpeedCurve;
        VerboseLogging = config.VerboseLogging;
    }

    public static bool ExtrapolateEffects { get; private set; } = true;

    /// <summary>
    /// 外推力量速度曲线：原版 <c>Vector2.Evaluate(v, t) =&gt; Mathf.Lerp(v.x, v.y, t)</c> 会截断 t，
    /// 而 t = SummaryLevel / 60f，所以 60 级之后曲线值不再变化。这里用 LerpUnclamped 继续线性外推。
    /// </summary>
    public static float EvaluateStrengthSpeedCurve(Vector2 curve, int strengthSummaryLevel)
    {
        var t = strengthSummaryLevel / VanillaStrengthSpeedNormalisation;
        if (!ExtrapolateStrengthSpeedCurve)
        {
            t = Mathf.Clamp01(t);
        }

        return Mathf.LerpUnclamped(curve.x, curve.y, t);
    }
}
