/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Launcher.App.Resources;
using Markdig;
using Markdig.Syntax;
using Serilog;

namespace Launcher.App.Controls;

public partial class MarkdownView : UserControl
{
    internal const int MaximumMarkdownCharacters = 64 * 1024;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseCjkFriendlyEmphasis()
        .DisableHtml()
        .Build();

    private readonly Func<string, MarkdownDocument> parser;

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(string.Empty, OnPresentationChanged));

    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.Register(
        nameof(LinkCommand),
        typeof(ICommand),
        typeof(MarkdownView),
        new PropertyMetadata(null, OnPresentationChanged));

    public MarkdownView()
        : this(source => Markdig.Markdown.Parse(source, Pipeline))
    {
    }

    internal MarkdownView(Func<string, MarkdownDocument> parser)
    {
        this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
        InitializeComponent();
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public ICommand? LinkCommand
    {
        get => (ICommand?)GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    internal IReadOnlyList<UIElement> RenderedElements => ContentPanel.Children.Cast<UIElement>().ToArray();

    private static void OnPresentationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        ((MarkdownView)dependencyObject).RenderMarkdown();
    }

    private void RenderMarkdown()
    {
        ContentPanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(Markdown))
            return;

        var source = LimitMarkdown(Markdown, out var sourceTruncated);
        try
        {
            var document = parser(source);
            var result = MarkdownWpfRenderer.Render(document, LinkCommand);
            foreach (var element in result.Elements)
                ContentPanel.Children.Add(element);

            if (sourceTruncated || result.IsTruncated)
                ContentPanel.Children.Add(CreateTruncationNotice());
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to render launcher update release notes Markdown; using plain text fallback.");
            ContentPanel.Children.Add(CreatePlainTextFallback(source));
            if (sourceTruncated)
                ContentPanel.Children.Add(CreateTruncationNotice());
        }
    }

    private static string LimitMarkdown(string markdown, out bool truncated)
    {
        truncated = markdown.Length > MaximumMarkdownCharacters;
        if (!truncated)
            return markdown;

        var length = MaximumMarkdownCharacters;
        if (char.IsHighSurrogate(markdown[length - 1]))
            length--;
        return markdown[..length];
    }

    private static TextBlock CreatePlainTextFallback(string text)
    {
        var block = MarkdownWpfRenderer.CreateBodyTextBlock();
        block.Text = text;
        block.Margin = new Thickness(0, 0, 0, 8);
        return block;
    }

    private static TextBlock CreateTruncationNotice()
    {
        var block = MarkdownWpfRenderer.CreateBodyTextBlock();
        block.Text = Strings.Markdown_ContentTruncated;
        block.Margin = new Thickness(0, 8, 0, 0);
        block.FontStyle = FontStyles.Italic;
        block.SetResourceReference(ForegroundProperty, "Brush.Text.Secondary");
        return block;
    }
}
