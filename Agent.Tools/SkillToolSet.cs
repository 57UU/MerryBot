using System.ComponentModel;
using System.Text;
using CommonLib;
using LlmBackend;

namespace Agent.Tools;

/// <summary>
/// 技能工具集：注册 skill_list / skill_read 两个工具。
/// 通过 <see cref="ISkillManagementService"/> 读取 Skill，支持由运行时或 WebUI 管理服务提供内容。
/// Prompt 返回技能名列表，让模型在 system prompt 中看到可用技能；
/// 模型通过 skill_read 读取具体技能内容后执行。
/// 技能表在构造时获取一次启用 Skill 快照；读取时仍由服务校验启用状态。
/// </summary>
public class SkillToolSet : ToolSet
{
    /// <summary>skill_read 单次返回内容上限（字符），防撑爆上下文</summary>
    private const int MaxReadLength = 20000;

    private readonly ToolSetBridge bridge;
    private readonly ISkillManagementService skillService;
    private readonly IReadOnlyDictionary<string, ManagedSkill> skills;

    /// <summary>兼容独立 TUI 运行；机器人运行时应传入接口实例。</summary>
    public SkillToolSet(string skillsPath)
        : this(new FileSkillManagementService(skillsPath))
    {
    }

    private SkillToolSet(FileSkillManagementService skillService)
        : this(skillService, skillService.ListSkillsAsync().GetAwaiter().GetResult())
    {
    }

    private SkillToolSet(ISkillManagementService skillService, IReadOnlyList<ManagedSkill> skills)
    {
        this.skillService = skillService;
        this.skills = skills
            .Where(static skill => skill.Enabled)
            .OrderBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static skill => skill.Name, StringComparer.OrdinalIgnoreCase);

        var builder = new ToolSetBridge.Builder(BuildPrompt());
        builder.AddFunction<SkillListArgs>("skill_list", "列出所有可用技能名称。", ListSkillsAsync);
        builder.AddFunction<SkillReadArgs>("skill_read", "读取指定技能的内容，返回技能文件全文。", ReadSkillAsync);
        bridge = builder.Build();
    }

    public static async Task<SkillToolSet> CreateAsync(ISkillManagementService skillService, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skillService);
        return new SkillToolSet(skillService, await skillService.ListSkillsAsync(cancellationToken));
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd) => bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);
    public override string? Prompt() => bridge.Prompt();

    /// <summary>工具参数：skill_read</summary>
    private sealed class SkillReadArgs
    {
        [Description("技能名称")]
        public string skill { get; set; } = string.Empty;
    }

    /// <summary>工具参数：skill_list（无参数）</summary>
    private sealed class SkillListArgs { }

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

    private async Task<string> ListSkillsAsync(SkillListArgs _)
    {
        var enabledSkills = (await skillService.ListSkillsAsync())
            .Where(static skill => skill.Enabled)
            .OrderBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static skill => skill.Name)
            .ToList();
        if (enabledSkills.Count == 0)
        {
            return "技能目录为空，没有可用技能。";
        }
        var sb = new StringBuilder($"可用技能（共 {enabledSkills.Count} 个）：\n");
        sb.AppendJoin('\n', enabledSkills);
        return sb.ToString();
    }

    private async Task<string> ReadSkillAsync(SkillReadArgs args)
    {
        var name = args.skill?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new ArgumentException("skill 参数不能为空");
        }
        if (!skills.ContainsKey(name))
        {
            throw new ArgumentException($"未找到技能: {name}（可用 skill_list 查看全部技能）");
        }
        var content = await skillService.ReadSkillAsync(name, includeDisabled: false);
        if (content.Length <= MaxReadLength)
        {
            return content;
        }
        return content[..MaxReadLength] + $"\n…（内容过长已截断，全文共 {content.Length} 字符）";
    }
}
