using System.ComponentModel;
using System.Text;
using LlmBackend;

namespace Agent.Tools;

/// <summary>
/// 技能工具集：注册 skill_list / skill_read 两个工具。
/// 构造参数为技能文件夹路径，支持两种布局：
/// - 平铺格式：顶层 *.md（技能名 = 文件名去 .md）
/// - 文件夹格式：顶层子目录 SKILL.md（技能名 = 子目录名）
/// Prompt 返回技能名列表，让模型在 system prompt 中看到可用技能；
/// 模型通过 skill_read 读取具体技能内容后执行。
/// 技能表在构造时扫描一次；skill_read 仅允许读取已扫描的技能文件，防止路径注入。
/// </summary>
public class SkillToolSet : ToolSet
{
    /// <summary>skill_read 单次返回内容上限（字符），防撑爆上下文</summary>
    private const int MaxReadLength = 20000;

    private readonly ToolSetBridge bridge;
    private readonly IReadOnlyDictionary<string, string> skills;

    public SkillToolSet(string skillsPath)
    {
        if (string.IsNullOrWhiteSpace(skillsPath) || !Directory.Exists(skillsPath))
        {
            throw new DirectoryNotFoundException($"技能目录不存在: {skillsPath}");
        }
        skills = ScanSkills(skillsPath);

        var builder = new ToolSetBridge.Builder(BuildPrompt());
        builder.AddFunction<SkillListArgs>("skill_list", "列出所有可用技能名称。", _ => Task.FromResult(ListSkills()));
        builder.AddFunction<SkillReadArgs>("skill_read", "读取指定技能的内容，返回技能文件全文。", ReadSkillAsync);
        bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall) => bridge.InvokeAsync(cancellationToken, toolCall);
    public override string? Prompt() => bridge.Prompt();

    /// <summary>工具参数：skill_read</summary>
    private sealed class SkillReadArgs
    {
        [Description("技能名称")]
        public string skill { get; set; } = string.Empty;
    }

    /// <summary>工具参数：skill_list（无参数）</summary>
    private sealed class SkillListArgs { }

    /// <summary>扫描技能目录：顶层 *.md（平铺）与顶层子目录 SKILL.md（文件夹格式）</summary>
    private static IReadOnlyDictionary<string, string> ScanSkills(string skillsPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(skillsPath, "*.md"))
        {
            result[Path.GetFileNameWithoutExtension(file)] = Path.GetFullPath(file);
        }
        foreach (var dir in Directory.GetDirectories(skillsPath))
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (File.Exists(skillFile))
            {
                result[Path.GetFileName(dir)] = Path.GetFullPath(skillFile);
            }
        }
        return result.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    private string BuildPrompt()
    {
        if (skills.Count == 0)
        {
            return "技能目录为空，没有可用技能。";
        }
        var sb = new StringBuilder("可用技能：");
        sb.AppendJoin("、", skills.Keys);
        sb.Append($"（共 {skills.Count} 个）。如需使用某技能，调用 skill_list 查看、skill_read 读取其内容后再执行。");
        return sb.ToString();
    }

    private string ListSkills()
    {
        if (skills.Count == 0)
        {
            return "技能目录为空，没有可用技能。";
        }
        var sb = new StringBuilder($"可用技能（共 {skills.Count} 个）：\n");
        sb.AppendJoin('\n', skills.Keys);
        return sb.ToString();
    }

    private Task<string> ReadSkillAsync(SkillReadArgs args)
    {
        var name = args.skill?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new ArgumentException("skill 参数不能为空");
        }
        if (!skills.TryGetValue(name, out var path))
        {
            throw new ArgumentException($"未找到技能: {name}（可用 skill_list 查看全部技能）");
        }
        var content = File.ReadAllText(path);
        if (content.Length <= MaxReadLength)
        {
            return Task.FromResult(content);
        }
        return Task.FromResult(content[..MaxReadLength] + $"\n…（内容过长已截断，全文共 {content.Length} 字符）");
    }
}
