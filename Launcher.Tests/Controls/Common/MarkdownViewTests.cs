using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Launcher.App.Controls;
using Launcher.App.Resources;
using Markdig.Syntax;

namespace Launcher.Tests.Controls.Common;

public sealed class MarkdownViewTests
{
    [Fact]
    public void SupportedMarkdownRendersAsNativeWpfElements()
    {
        RunOnStaThread(() =>
        {
            var command = new RecordingCommand();
            var view = new MarkdownView
            {
                LinkCommand = command,
                Markdown = """
                    # Release 1.0

                    Paragraph with **bold**, *italic*, ~~removed~~, and `code`.

                    - First
                      - Nested
                    3. Ordered

                    > Important note

                    ```text
                    BlockHelm
                    ```

                    ---

                    [Project](https://example.com/releases)
                    """
            };

            var elements = view.RenderedElements;
            var firstHeading = Assert.IsType<TextBlock>(elements[0]);
            Assert.Equal(24, firstHeading.FontSize);
            Assert.Contains("Release 1.0", ReadText(firstHeading), StringComparison.Ordinal);
            Assert.Contains(elements, element => element is StackPanel);
            Assert.True(CountElements<Border>(elements) >= 3);
            Assert.Contains("3.", string.Concat(elements.Select(ReadText)), StringComparison.Ordinal);

            var hyperlink = Assert.Single(FindInlines<Hyperlink>(elements));
            Assert.Same(command, hyperlink.Command);
            Assert.Equal("https://example.com/releases", hyperlink.CommandParameter);
            Assert.Contains(FindInlines<Run>(elements), run => run.Text == "code");
        });
    }

    [Fact]
    public void HtmlImagesAndUnsafeLinksDoNotCreateExecutableOrRemoteContent()
    {
        RunOnStaThread(() =>
        {
            var view = new MarkdownView
            {
                LinkCommand = new RecordingCommand(),
                Markdown = "# <script>alert(1)</script>\n\n![Remote logo](https://example.com/logo.png)\n\n[Local](file:///C:/test.txt)"
            };

            var text = string.Concat(view.RenderedElements.Select(ReadText));
            Assert.Contains("<script>alert(1)</script>", text, StringComparison.Ordinal);
            Assert.Contains("Remote logo", text, StringComparison.Ordinal);
            Assert.Contains("Local", text, StringComparison.Ordinal);
            Assert.Empty(FindInlines<Hyperlink>(view.RenderedElements));
            Assert.Equal(0, CountElements<Image>(view.RenderedElements));
        });
    }

    [Fact]
    public void AdjacentListItemsUseCompactVerticalSpacing()
    {
        RunOnStaThread(() =>
        {
            var view = new MarkdownView { Markdown = "- First\n- Second" };

            var list = Assert.IsType<StackPanel>(Assert.Single(view.RenderedElements));
            var rows = list.Children.Cast<UIElement>().Select(Assert.IsType<Grid>).ToArray();
            Assert.Equal(2, rows.Length);
            Assert.All(rows, row => Assert.Equal(3, row.Margin.Bottom));

            foreach (var row in rows)
            {
                var content = Assert.IsType<StackPanel>(row.Children[1]);
                var paragraph = Assert.IsType<TextBlock>(Assert.Single(content.Children));
                Assert.Equal(0, paragraph.Margin.Bottom);
            }
        });
    }

    [Fact]
    public void CharacterAndNodeLimitsShowLocalizedTruncationNotice()
    {
        RunOnStaThread(() =>
        {
            var oversized = new MarkdownView
            {
                Markdown = "# Release\n\n" + new string('a', MarkdownView.MaximumMarkdownCharacters + 100)
            };
            Assert.Contains(
                oversized.RenderedElements,
                element => ReadText(element).Contains(Strings.Markdown_ContentTruncated, StringComparison.Ordinal));

            var manyItems = new MarkdownView
            {
                Markdown = "# Release\n\n" + string.Join('\n', Enumerable.Range(1, 500).Select(index => $"- Item {index}"))
            };
            Assert.Contains(
                manyItems.RenderedElements,
                element => ReadText(element).Contains(Strings.Markdown_ContentTruncated, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void RendererFailureFallsBackToPlainText()
    {
        RunOnStaThread(() =>
        {
            var view = new MarkdownView(_ => throw new InvalidOperationException("test failure"))
            {
                Markdown = "# Safe fallback"
            };

            Assert.Contains("# Safe fallback", string.Concat(view.RenderedElements.Select(ReadText)), StringComparison.Ordinal);
        });
    }

    private static IEnumerable<T> FindInlines<T>(IEnumerable<UIElement> elements) where T : Inline
    {
        foreach (var textBlock in FindElements<TextBlock>(elements))
        {
            foreach (var inline in FindInlines<T>(textBlock.Inlines))
                yield return inline;
        }
    }

    private static IEnumerable<T> FindInlines<T>(InlineCollection inlines) where T : Inline
    {
        foreach (Inline inline in inlines)
        {
            if (inline is T match)
                yield return match;
            if (inline is Span span)
            {
                foreach (var child in FindInlines<T>(span.Inlines))
                    yield return child;
            }
        }
    }

    private static int CountElements<T>(IEnumerable<UIElement> elements) where T : UIElement =>
        FindElements<T>(elements).Count();

    private static IEnumerable<T> FindElements<T>(IEnumerable<UIElement> elements) where T : UIElement
    {
        foreach (var element in elements)
        {
            if (element is T match)
                yield return match;
            foreach (var child in GetChildren(element))
            {
                foreach (var matchChild in FindElements<T>([child]))
                    yield return matchChild;
            }
        }
    }

    private static IEnumerable<UIElement> GetChildren(UIElement element)
    {
        if (element is Panel panel)
        {
            foreach (UIElement child in panel.Children)
                yield return child;
        }
        else if (element is Border { Child: UIElement child })
        {
            yield return child;
        }
    }

    private static string ReadText(UIElement element)
    {
        if (element is TextBlock textBlock)
            return string.Concat(textBlock.Inlines.Cast<Inline>().Select(ReadInline));
        return string.Concat(GetChildren(element).Select(ReadText));
    }

    private static string ReadInline(Inline inline) => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
        Span span => string.Concat(span.Inlines.Cast<Inline>().Select(ReadInline)),
        _ => string.Empty
    };

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class RecordingCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}
