using SPTarkov.Server.Core.Models.Spt.Mod;
// SemanticVersioning.Version / Range 与 System.Version / System.Range 同名，必须用别名区分
using SemVer = SemanticVersioning.Version;
using SemVerRange = SemanticVersioning.Range;

namespace SkillCap100.Server;

/// <summary>
/// SPT 4.1.x 模组元数据。服务端在加载 DLL 时会扫描实现 <see cref="IModMetadata"/> 的类型，
/// 用它做版本校验、依赖校验与加载顺序排序。
/// <para>
/// 注意：<c>SptVersion</c> 必须覆盖当前服务端版本（本机为 4.1.6），否则模组会被 ModValidator 拒绝。
/// </para>
/// </summary>
public sealed record SkillCap100Metadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.skillcap100.server";

    public string Name { get; init; } = "Skill Cap 100 (Server)";

    public string Author { get; init; } = "SkillCap100";

    public List<string>? Contributors { get; init; } = null;

    /// <summary>本模组版本（semver，不支持四段版本号）。</summary>
    public SemVer Version { get; init; } = new("1.0.1", false);

    /// <summary>兼容的 SPT 版本范围：4.1.x。</summary>
    public SemVerRange SptVersion { get; init; } = new("~4.1.0", false);

    public bool HasPrepatcher { get; init; } = false;

    public List<string>? Incompatibilities { get; init; } = null;

    public Dictionary<string, SemVerRange>? ModDependencies { get; init; } = null;

    public string? Url { get; init; } = null;

    public string License { get; init; } = "MIT";
}
