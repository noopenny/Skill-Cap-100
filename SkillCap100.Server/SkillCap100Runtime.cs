using SPTarkov.Common.Models.Logging;

namespace SkillCap100.Server;

/// <summary>
/// 补丁运行时上下文（配置 + 日志）。
/// <para>
/// Harmony 补丁方法必须是静态的，无法通过构造函数注入，因此这里用一个静态容器把
/// IOnLoad 阶段读取到的配置传给补丁。
/// </para>
/// <para>
/// 该类是 <c>public</c>：Transpiler 注入到 SPT 程序集 IL 中的调用需要跨程序集可见，
/// 否则 JIT 会抛 MethodAccessException。
/// </para>
/// <para>
/// 日志策略：默认（verboseLogging=false）只在加载完成时输出一行；
/// 详细过程用 <see cref="Verbose"/>，需要排查问题时把 verboseLogging 设为 true。
/// 错误/警告始终输出。
/// </para>
/// </summary>
public static class SkillCap100Runtime
{
    private const string Prefix = "[SkillCap100] ";

    private static SkillCap100Config _config = new();

    private static ISptLogger<SkillCap100Mod>? _logger;

    public static SkillCap100Config Config => _config;

    /// <summary>技能经验上限 = 等级上限 * 100（默认 100 级 = 10000 点）。</summary>
    public static double MaxExperience => _config.MaxExperience;

    /// <summary>精英效果解锁所需的经验值（默认 100 级 = 10000 点）。</summary>
    public static double EliteExperience => _config.EliteExperience;

    public static bool VerboseLogging => _config.VerboseLogging;

    public static void Initialise(SkillCap100Config config, ISptLogger<SkillCap100Mod> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>成功/完成提示（始终输出）。</summary>
    public static void Success(string message) => _logger?.Success(Prefix + message);

    /// <summary>普通信息（始终输出）。</summary>
    public static void Info(string message) => _logger?.Info(Prefix + message);

    public static void Warning(string message) => _logger?.Warning(Prefix + message);

    public static void Error(string message, Exception? ex = null) => _logger?.Error(Prefix + message, ex);

    /// <summary>详细过程日志（仅 verboseLogging=true 时输出）。</summary>
    public static void Verbose(string message)
    {
        if (VerboseLogging)
        {
            _logger?.Info(Prefix + message);
        }
    }
}
