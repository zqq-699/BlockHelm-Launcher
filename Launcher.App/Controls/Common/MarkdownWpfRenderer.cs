/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;
using WpfInline = System.Windows.Documents.Inline;

namespace Launcher.App.Controls;

internal static class MarkdownWpfRenderer
{
    internal const int MaximumRenderedNodes = 1000;
    internal const int MaximumRenderDepth = 16;

    public static MarkdownRenderResult Render(MarkdownDocument document, ICommand? linkCommand)
    {
        ArgumentNullException.ThrowIfNull(document);
        var panel = new StackPanel();
        var state = new RenderState();
        RenderBlocks(document, panel, linkCommand, state, 0);
        var elements = panel.Children.Cast<UIElement>().ToArray();
        panel.Children.Clear();
        return new MarkdownRenderResult(elements, state.IsTruncated);
    }

    internal static TextBlock CreateBodyTextBlock()
    {
        var block = new TextBlock
        {
            FontSize = 14,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap
        };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "LauncherFontFamily");
        block.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Primary");
        return block;
    }

    private static void RenderBlocks(
        ContainerBlock container,
        Panel target,
        ICommand? linkCommand,
        RenderState state,
        int depth)
    {
        if (!state.Enter(depth))
            return;

        foreach (var block in container)
        {
            if (!state.ConsumeNode())
                return;

            switch (block)
            {
                case HeadingBlock heading:
                    target.Children.Add(CreateHeading(heading, linkCommand, state, depth));
                    break;
                case ParagraphBlock paragraph:
                    target.Children.Add(CreateParagraph(paragraph, linkCommand, state, depth));
                    break;
                case ListBlock list:
                    target.Children.Add(CreateList(list, linkCommand, state, depth));
                    break;
                case QuoteBlock quote:
                    target.Children.Add(CreateQuote(quote, linkCommand, state, depth));
                    break;
                case ThematicBreakBlock:
                    target.Children.Add(CreateThematicBreak());
                    break;
                case CodeBlock code:
                    target.Children.Add(CreateCodeBlock(code));
                    break;
                case LeafBlock leaf when leaf.Lines.Count > 0:
                    var fallback = CreateBodyTextBlock();
                    fallback.Text = leaf.Lines.ToString();
                    fallback.Margin = new Thickness(0, 0, 0, 8);
                    target.Children.Add(fallback);
                    break;
            }

            if (state.IsTruncated)
                return;
        }
    }

    private static TextBlock CreateHeading(
        HeadingBlock heading,
        ICommand? linkCommand,
        RenderState state,
        int depth)
    {
        var sizes = new[] { 24d, 20d, 17d, 15d, 14d, 14d };
        var block = CreateBodyTextBlock();
        block.FontSize = sizes[Math.Clamp(heading.Level, 1, 6) - 1];
        block.FontWeight = heading.Level <= 3 ? FontWeights.SemiBold : FontWeights.Medium;
        block.LineHeight = block.FontSize + 8;
        block.Margin = new Thickness(0, heading.Level == 1 ? 0 : 8, 0, 8);
        AddInlines(block.Inlines, heading.Inline, linkCommand, state, depth + 1);
        return block;
    }

    private static TextBlock CreateParagraph(
        ParagraphBlock paragraph,
        ICommand? linkCommand,
        RenderState state,
        int depth)
    {
        var block = CreateBodyTextBlock();
        block.Margin = new Thickness(0, 0, 0, 9);
        AddInlines(block.Inlines, paragraph.Inline, linkCommand, state, depth + 1);
        return block;
    }

    private static FrameworkElement CreateList(
        ListBlock list,
        ICommand? linkCommand,
        RenderState state,
        int depth)
    {
        var panel = new StackPanel { Margin = new Thickness(depth == 0 ? 0 : 10, 0, 0, 8) };
        var index = list.IsOrdered && int.TryParse(list.OrderedStart, out var orderedStart)
            ? orderedStart
            : 1;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item || !state.ConsumeNode())
                continue;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = CreateBodyTextBlock();
            marker.Text = list.IsOrdered ? $"{index}{list.OrderedDelimiter}" : "-";
            marker.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            row.Children.Add(marker);

            var content = new StackPanel();
            Grid.SetColumn(content, 1);
            RenderBlocks(item, content, linkCommand, state, depth + 1);
            TrimTrailingMargin(content);
            row.Children.Add(content);
            panel.Children.Add(row);
            index++;

            if (state.IsTruncated)
                break;
        }

        return panel;
    }

    private static void TrimTrailingMargin(Panel panel)
    {
        if (panel.Children.Count == 0 || panel.Children[^1] is not FrameworkElement lastChild)
            return;

        var margin = lastChild.Margin;
        lastChild.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 0);
    }

    private static Border CreateQuote(
        QuoteBlock quote,
        ICommand? linkCommand,
        RenderState state,
        int depth)
    {
        var content = new StackPanel();
        RenderBlocks(quote, content, linkCommand, state, depth + 1);
        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 7, 10, 0),
            Margin = new Thickness(0, 2, 0, 11),
            Child = content
        };
        border.SetResourceReference(Border.BorderBrushProperty, "LauncherAccentBrush");
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Markdown.QuoteBackground");
        return border;
    }

    private static Border CreateThematicBreak()
    {
        var border = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 14)
        };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Markdown.Divider");
        return border;
    }

    private static Border CreateCodeBlock(CodeBlock code)
    {
        var text = CreateBodyTextBlock();
        text.Text = code.Lines.ToString().TrimEnd('\r', '\n');
        text.FontSize = 13;
        text.LineHeight = 19;
        text.SetResourceReference(TextBlock.FontFamilyProperty, "LauncherMonospaceFontFamily");
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 11),
            Child = text
        };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Markdown.CodeBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Markdown.CodeBorder");
        return border;
    }

    private static void AddInlines(
        InlineCollection target,
        ContainerInline? container,
        ICommand? linkCommand,
        RenderState state,
        int depth)
    {
        if (container is null || !state.Enter(depth))
            return;

        var current = container.FirstChild;
        while (current is not null)
        {
            if (!state.ConsumeNode())
                return;
            var rendered = CreateInline(current, linkCommand, state, depth);
            if (rendered is not null)
                target.Add(rendered);
            if (state.IsTruncated)
                return;
            current = current.NextSibling;
        }
    }

    private static WpfInline? CreateInline(
        MarkdownInline inline,
        ICommand? linkCommand,
        RenderState state,
        int depth)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());
            case CodeInline code:
                var codeRun = new Run(code.Content);
                codeRun.SetResourceReference(TextElement.BackgroundProperty, "Brush.Markdown.CodeBackground");
                codeRun.SetResourceReference(TextElement.FontFamilyProperty, "LauncherMonospaceFontFamily");
                return codeRun;
            case LineBreakInline:
                return new LineBreak();
            case EmphasisInline emphasis:
                var emphasisSpan = new Span();
                if (emphasis.DelimiterChar == '~')
                    emphasisSpan.TextDecorations = TextDecorations.Strikethrough;
                else if (emphasis.DelimiterCount >= 2)
                    emphasisSpan.FontWeight = FontWeights.SemiBold;
                else
                    emphasisSpan.FontStyle = FontStyles.Italic;
                AddInlines(emphasisSpan.Inlines, emphasis, linkCommand, state, depth + 1);
                return emphasisSpan;
            case LinkInline link when link.IsImage:
                var imageAlt = new Span();
                AddInlines(imageAlt.Inlines, link, linkCommand, state, depth + 1);
                return imageAlt;
            case LinkInline link:
                return CreateLink(link, link.Url, linkCommand, state, depth);
            case AutolinkInline autolink:
                return CreateLink(null, autolink.Url, linkCommand, state, depth, autolink.Url);
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case ContainerInline container:
                var span = new Span();
                AddInlines(span.Inlines, container, linkCommand, state, depth + 1);
                return span;
            default:
                return null;
        }
    }

    private static WpfInline CreateLink(
        ContainerInline? content,
        string? url,
        ICommand? linkCommand,
        RenderState state,
        int depth,
        string? fallbackText = null)
    {
        var isSafe = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https";
        Span span = isSafe && linkCommand is not null
            ? new Hyperlink { Command = linkCommand, CommandParameter = uri!.AbsoluteUri }
            : new Span();
        if (span is Hyperlink hyperlink)
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "LauncherAccentBrush");

        if (content is not null)
            AddInlines(span.Inlines, content, linkCommand, state, depth + 1);
        else if (!string.IsNullOrEmpty(fallbackText))
            span.Inlines.Add(new Run(fallbackText));
        return span;
    }

    private sealed class RenderState
    {
        private int renderedNodes;

        public bool IsTruncated { get; private set; }

        public bool Enter(int depth)
        {
            if (depth <= MaximumRenderDepth)
                return true;
            IsTruncated = true;
            return false;
        }

        public bool ConsumeNode()
        {
            renderedNodes++;
            if (renderedNodes <= MaximumRenderedNodes)
                return true;
            IsTruncated = true;
            return false;
        }
    }
}

internal sealed record MarkdownRenderResult(
    IReadOnlyList<UIElement> Elements,
    bool IsTruncated);
