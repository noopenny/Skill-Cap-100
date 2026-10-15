using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkillCap100.Server;

/// <summary>
/// 服务端配置文件模型（user/mods/SkillCap100/config.json）。
/// <para>
/// 服务端只负责“档案层”的数值上限：技能经验上限、精英判定阈值、档案迁移修正。
/// 技能效果的逐级叠加（52~100 级）由客户端 BepInEx 插件负责，因为 Tarkov 的技能效果
/// 完全在客户端（Assembly-CSharp.dll / EFT.SkillManager）本地计算。
/// </para>
/// </summary>
public sealed class SkillCap100Config
{
    /// <summary>是否启用本模组。设置为 false 时不会打任何 Harmony 补丁（等同未安装）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 技能等级上限，原版为 51。默认 100。
    /// 每级经验 100 点，因此经验上限 = MaxSkillLevel * 100（100 级 = 10000 点）。
    /// </summary>
    public int MaxSkillLevel { get; set; } = 100;

    /// <summary>
    /// 精英（Elite）效果的解锁等级，原版为 51。默认 100，即必须练满才获得精英效果。
    /// 取值必须 &lt;= MaxSkillLevel。
    /// </summary>
    public int EliteUnlockLevel { get; set; } = 100;

    /// <summary>
    /// 是否修补 SPT 的档案迁移/清洗逻辑（InvalidSkillProgressFix），
    /// 该逻辑会把经验 &gt; 5100 的技能强行砍回 5100。
    /// 除非你确定要关闭，否则请保持 true。
    /// </summary>
    public bool PatchProfileMigrations { get; set; } = true;

    /// <summary>是否输出详细的加载/补丁日志。</summary>
    public bool VerboseLogging { get; set; } = true;

    [JsonIgnore]
    public double MaxExperience => MaxSkillLevel * 100d;

    [JsonIgnore]
    public double EliteExperience => EliteUnlockLevel * 100d;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SkillCap100Config Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            var defaults = new SkillCap100Config();
            File.WriteAllText(filePath, JsonSerializer.Serialize(defaults, WriteOptions));
            return defaults;
        }

        var json = File.ReadAllText(filePath);
        var config = JsonSerializer.Deserialize<SkillCap100Config>(json, ReadOptions) ?? new SkillCap100Config();
        config.Normalise();
        return config;
    }

    /// <summary>把不合法的配置值修正到安全范围内，避免用户手改配置后把服务器搞崩。</summary>
    public void Normalise()
    {
        // 原版上限是 51，低于 51 没有意义（会直接砍掉玩家已有等级）
        MaxSkillLevel = Math.Clamp(MaxSkillLevel, 51, 1000);
        EliteUnlockLevel = Math.Clamp(EliteUnlockLevel, 1, MaxSkillLevel);
    }

    public override string ToString()
    {
        return $"Enabled={Enabled}, MaxSkillLevel={MaxSkillLevel}, EliteUnlockLevel={EliteUnlockLevel}, " +
               $"MaxExperience={MaxExperience}, EliteExperience={EliteExperience}, " +
               $"PatchProfileMigrations={PatchProfileMigrations}";
    }
}
