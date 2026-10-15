using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Common.Models.Logging;
using SkillCap100.Server.Patches;

namespace SkillCap100.Server;

/// <summary>
/// 服务端模组入口。
/// <para>
/// SPT 4.x 通过依赖注入扫描 <see cref="IOnLoad"/> 实现并在启动阶段调用 <see cref="OnLoadAsync"/>。
/// </para>
/// <para>
/// <b>关键：加载优先级必须小于 200000（早期阶段）。</b>
/// SPT 分两批执行 IOnLoad：
/// <list type="bullet">
///   <item><description><c>0 &lt;= TypePriority &lt; 200000</c>：ProgramExtensions.RunPreSptLoadCallbacks（「正在执行启动回调」）</description></item>
///   <item><description><c>TypePriority &gt;= 200000</c>：SPTStartupHostedService（更晚）</description></item>
/// </list>
/// 而<b>读取存档</b>的 <c>SaveCallbacks.OnLoadAsync</c>（它会调用 <c>SaveServer.LoadAsync</c> 反序列化
/// <c>user/profiles/*.json</c>）的优先级是 <c>int.MaxValue</c>，属于第二批。
/// 如果我们也用默认的 <c>int.MaxValue</c>，执行顺序取决于 DI 容器里类型的遍历顺序 ——
/// 一旦存档先被反序列化，<c>CommonSkill.Progress</c> setter / <c>GetSafeSkillProgress</c>
/// 就已经被 JIT 编译（旧的一行 <c>Math.Min(value, 5100)</c> 被内联进去），之后再怎么打 Harmony 补丁
/// 对反序列化路径都不生效 —— 这正是「战局里能练到 51 级以上、结算后掉回 51 级」的根因（已用
/// <c>moddev/_diag/SkillPersistProbe</c> 在离线下复现并验证）。
/// </para>
/// <para>
/// 因此这里显式使用 <see cref="OnLoadOrder.Preload"/>（=100000），保证补丁在存档加载之前生效。
/// </para>
/// <para>
/// 控制台输出刻意保持安静：默认只打印一行加载结果；
/// 每个补丁的目标方法等细节只在 config.json 里 <c>verboseLogging: true</c> 时输出。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton, OnLoadOrder.Preload)]
public sealed class SkillCap100Mod(ModHelper modHelper, ISptLogger<SkillCap100Mod> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modDirectory = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var configPath = Path.Combine(modDirectory, "config.json");

        SkillCap100Config config;
        try
        {
            config = SkillCap100Config.Load(configPath);
        }
        catch (Exception ex)
        {
            logger.Error($"[SkillCap100] 读取配置失败，使用默认配置：{configPath}", ex);
            config = new SkillCap100Config();
        }

        SkillCap100Runtime.Initialise(config, logger);

        if (!config.Enabled)
        {
            SkillCap100Runtime.Warning("已加载，但 Enabled=false：未打任何补丁（技能仍受原版 5100 上限限制）。");
            return Task.CompletedTask;
        }

        if (config.VerboseLogging)
        {
            SkillCap100Runtime.Info($"配置：{config}");
        }

        if (config.MaxSkillLevel == 51)
        {
            SkillCap100Runtime.Warning("MaxSkillLevel=51，等同原版，不会改变技能上限。");
        }

        // 逐个启用补丁：任何一个失败都不影响其它补丁，并且会明确报错。
        var patches = new List<AbstractPatch>
        {
            // 1) 核心：CommonSkill.Progress / PointsEarnedDuringSession 的 setter 会调用它，
            //    原版把任何赋值 clamp 到 5100 —— 不补丁的话存档一读一写就被砍回 51 级。
            new CommonSkillProgressPatch(),
            // 1b) 再直接接管两个 setter（写 backing field），双保险：
            //     即使某个调用方在打补丁前就被 JIT 编译并内联了旧 setter，也不会被砍回 5100。
            new CommonSkillProgressSetterPatch(),
            new CommonSkillPointsEarnedSetterPatch(),
            // 2) 精英判定：原版 commonSkill.Progress >= 5100
            new HasEliteSkillLevelPatch(),
            // 3) 服务端加技能点：原版有两处 5100（提前 return + Math.Min）
            new AddSkillPointsToPlayerPatch(),
            // 4) 藏身处健身房（QTE）加经验时过滤“满级”技能的 lambda（原版 5100）
            new WorkoutSkillGainCapPatch(),
        };

        if (config.PatchProfileMigrations)
        {
            // 5) 档案迁移清洗：原版会把 > 5100 的 Progress 判为非法并修正为 5100
            patches.Add(new InvalidSkillProgressNeedsFixingPatch());
            patches.Add(new InvalidSkillProgressGetFixedValuePatch());
        }

        var enabled = 0;
        foreach (var patch in patches)
        {
            try
            {
                patch.Enable();
                enabled++;
                SkillCap100Runtime.Verbose($"补丁 {patch.GetType().Name} -> {patch.TargetMethod?.FullDescription()}");
            }
            catch (Exception ex)
            {
                SkillCap100Runtime.Error($"补丁启用失败：{patch.GetType().Name} - {ex.Message}（该功能不会生效）", ex);
            }
        }

        // 默认只输出这一行
        SkillCap100Runtime.Success(
            $"加载成功：技能上限 {config.MaxSkillLevel} 级（{config.MaxExperience:0} 经验），" +
            $"精英解锁 {config.EliteUnlockLevel} 级，服务端补丁 {enabled}/{patches.Count} 生效。");

        return Task.CompletedTask;
    }
}
