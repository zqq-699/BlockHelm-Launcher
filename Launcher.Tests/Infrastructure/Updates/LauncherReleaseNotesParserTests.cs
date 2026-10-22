using Launcher.Infrastructure.Updates;

namespace Launcher.Tests.Infrastructure.Updates;

public sealed class LauncherReleaseNotesParserTests
{
    [Fact]
    public void StructuredMarkdownCreatesTitledSections()
    {
        const string markdown = """
            # 正式版 1.0.0

            ## 新功能
            - 在更新弹窗中显示更新日志。
            - 支持多个子项。

            ## 问题修复
            * 修复旧日志无法显示的问题。
            """;

        var sections = LauncherReleaseNotesParser.Parse(markdown);

        Assert.Collection(
            sections,
            section =>
            {
                Assert.Equal("新功能", section.Title);
                Assert.Equal(["在更新弹窗中显示更新日志。", "支持多个子项。"], section.Items);
            },
            section =>
            {
                Assert.Equal("问题修复", section.Title);
                Assert.Equal(["修复旧日志无法显示的问题。"], section.Items);
            });
    }

    [Fact]
    public void HistoricalFlatMarkdownRemainsVisibleAsUntitledSection()
    {
        const string markdown = """
            # 正式版 0.9.16

            * 新增实例模组更新功能。
            * 优化页面切换动画。
            """;

        var section = Assert.Single(LauncherReleaseNotesParser.Parse(markdown));

        Assert.Null(section.Title);
        Assert.Equal(["新增实例模组更新功能。", "优化页面切换动画。"], section.Items);
    }

    [Fact]
    public void PlainParagraphsAreKeptForCompatibility()
    {
        const string markdown = """
            # Release 1.0.0

            ## Other
            This release contains an important compatibility notice.
            """;

        var section = Assert.Single(LauncherReleaseNotesParser.Parse(markdown));

        Assert.Equal("Other", section.Title);
        Assert.Equal(["This release contains an important compatibility notice."], section.Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyMarkdownCreatesNoSections(string? markdown)
    {
        Assert.Empty(LauncherReleaseNotesParser.Parse(markdown));
    }

    [Fact]
    public void PathologicalReleaseNotesAreBoundedForUiRendering()
    {
        var oversizedItem = new string('a', 2000);
        var markdown = $"# Release\n\n## New Features\n- {oversizedItem}\n"
            + string.Join('\n', Enumerable.Range(1, 250).Select(index => $"- Item {index}"));

        var section = Assert.Single(LauncherReleaseNotesParser.Parse(markdown));

        Assert.Equal(200, section.Items.Count);
        Assert.Equal(1000, section.Items[0].Length);
        Assert.EndsWith("…", section.Items[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExcessSectionsAreTruncatedWithoutDuplicatingTheLastSection()
    {
        var markdown = string.Join(
            '\n',
            Enumerable.Range(1, 20).Select(index => $"## Section {index}\n- Item {index}"));

        var sections = LauncherReleaseNotesParser.Parse(markdown);

        Assert.Equal(16, sections.Count);
        Assert.Equal("Section 16", sections[^1].Title);
        Assert.Equal(["Item 16"], sections[^1].Items);
        Assert.Equal(16, sections.Select(section => section.Title).Distinct().Count());
    }
}
