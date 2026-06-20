using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Maui.Controls.Shapes;

namespace FatGuysSpeak.Client.Controls;

/// <summary>
/// Renders a markdown string as MAUI views.
/// Supports: **bold**, *italic*, ~~strikethrough~~, `inline code`, ```code blocks```,
/// # headings, and - / 1. lists.
/// </summary>
public class MarkdownView : ContentView
{
    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(MarkdownView), string.Empty,
            propertyChanged: (b, _, n) => ((MarkdownView)b).Render((string?)n ?? string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .DisableHtml()
        .Build();

    private void Render(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Content = null;
            return;
        }

        var doc = Markdown.Parse(text, Pipeline);
        var views = new List<View>();

        foreach (var block in doc)
        {
            var view = block switch
            {
                FencedCodeBlock cb => RenderCodeBlock(cb.Lines.ToString()),
                CodeBlock        cb => RenderCodeBlock(cb.Lines.ToString()),
                HeadingBlock     hb => RenderHeading(hb),
                ListBlock        lb => RenderList(lb),
                ParagraphBlock   pb => RenderParagraph(pb.Inline),
                _                   => null,
            };
            if (view is not null) views.Add(view);
        }

        Content = views.Count switch
        {
            0 => null,
            1 => views[0],
            _ => new VerticalStackLayout { Spacing = 4, Children = { } }.Also(s =>
            {
                foreach (var v in views) s.Children.Add(v);
            }),
        };
    }

    private static View RenderParagraph(ContainerInline? inlines)
    {
        var fs = new FormattedString();
        if (inlines is not null)
            AppendInlines(fs, inlines, bold: false, italic: false, strike: false);

        // Fall back to a plain label if FormattedString is empty
        if (fs.Spans.Count == 0)
            return new Label { TextColor = Color.FromArgb("#c8c8c8"), FontSize = 13 };

        return new Label
        {
            FormattedText = fs,
            FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap,
            HorizontalOptions = LayoutOptions.Fill,
        };
    }

    private static View RenderHeading(HeadingBlock heading)
    {
        var fs = new FormattedString();
        if (heading.Inline is not null)
            AppendInlines(fs, heading.Inline, bold: true, italic: false, strike: false);
        return new Label
        {
            FormattedText = fs,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.WordWrap,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0, 2, 0, 0),
        };
    }

    private static View RenderList(ListBlock list)
    {
        var stack = new VerticalStackLayout { Spacing = 2 };
        var index = 1;
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start)) index = start;

        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;

            var marker = list.IsOrdered ? $"{index}." : "•";
            index++;

            // Render the item's paragraph(s) inline; nested lists are flattened one level in.
            var fs = new FormattedString();
            foreach (var block in item)
                if (block is ParagraphBlock pb && pb.Inline is not null)
                {
                    if (fs.Spans.Count > 0) fs.Spans.Add(new Span { Text = "\n" });
                    AppendInlines(fs, pb.Inline, bold: false, italic: false, strike: false);
                }

            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Star },
                },
                ColumnSpacing = 6,
            };
            var markerLabel = new Label
            {
                Text = marker,
                TextColor = Color.FromArgb("#c8c8c8"),
                FontSize = 13,
                VerticalOptions = LayoutOptions.Start,
            };
            var textLabel = new Label
            {
                FormattedText = fs,
                FontSize = 13,
                LineBreakMode = LineBreakMode.WordWrap,
                HorizontalOptions = LayoutOptions.Fill,
            };
            Grid.SetColumn(markerLabel, 0);
            Grid.SetColumn(textLabel, 1);
            row.Children.Add(markerLabel);
            row.Children.Add(textLabel);
            stack.Children.Add(row);
        }

        return stack;
    }

    private static void AppendInlines(FormattedString fs, ContainerInline inlines,
        bool bold, bool italic, bool strike)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    fs.Spans.Add(new Span
                    {
                        Text = lit.Content.ToString(),
                        FontAttributes = ToFontAttrs(bold, italic),
                        TextDecorations = strike ? TextDecorations.Strikethrough : TextDecorations.None,
                        TextColor = Color.FromArgb("#c8c8c8"),
                        FontSize = 13,
                    });
                    break;

                case EmphasisInline em:
                    bool isBold   = em.DelimiterChar is '*' or '_' && em.DelimiterCount == 2;
                    bool isItalic = em.DelimiterChar is '*' or '_' && em.DelimiterCount == 1;
                    bool isStrike = em.DelimiterChar == '~';
                    AppendInlines(fs, em, bold || isBold, italic || isItalic, strike || isStrike);
                    break;

                case CodeInline code:
                    fs.Spans.Add(new Span
                    {
                        Text = $" {code.Content} ", // non-breaking spaces for padding
                        FontFamily = "Consolas",
                        TextColor = Color.FromArgb("#e9a96e"),
                        BackgroundColor = Color.FromArgb("#1a1a1a"),
                        FontSize = 12,
                    });
                    break;

                case LineBreakInline lb:
                    fs.Spans.Add(new Span { Text = lb.IsHard ? "\n" : " " });
                    break;

                case ContainerInline ci:
                    AppendInlines(fs, ci, bold, italic, strike);
                    break;
            }
        }
    }

    private static View RenderCodeBlock(string code) =>
        new Border
        {
            BackgroundColor = Color.FromArgb("#141414"),
            Stroke = new SolidColorBrush(Color.FromArgb("#333333")),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Padding = new Thickness(10, 8),
            HorizontalOptions = LayoutOptions.Fill,
            Content = new Label
            {
                Text = code.TrimEnd('\n'),
                FontFamily = "Consolas",
                FontSize = 12,
                TextColor = Color.FromArgb("#d0d0d0"),
                LineBreakMode = LineBreakMode.NoWrap,
            },
        };

    private static FontAttributes ToFontAttrs(bool bold, bool italic) =>
        (bold, italic) switch
        {
            (true, true)   => FontAttributes.Bold | FontAttributes.Italic,
            (true, false)  => FontAttributes.Bold,
            (false, true)  => FontAttributes.Italic,
            _              => FontAttributes.None,
        };
}

file static class ViewExtensions
{
    public static T Also<T>(this T self, Action<T> action) { action(self); return self; }
}
