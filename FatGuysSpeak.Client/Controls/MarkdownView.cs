using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Maui.Controls.Shapes;

namespace FatGuysSpeak.Client.Controls;

/// <summary>
/// Renders a markdown string as MAUI views.
/// Supports: **bold**, *italic*, ~~strikethrough~~, `inline code`, ```code blocks```.
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
