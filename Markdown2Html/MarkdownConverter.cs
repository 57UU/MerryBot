using Markdig;

namespace Markdown2Html;

public static class MarkdownConverter
{
    private static MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();
    public static string ToHtml(string md)
    {
        return Markdown.ToHtml(md,pipeline);
    }
}
