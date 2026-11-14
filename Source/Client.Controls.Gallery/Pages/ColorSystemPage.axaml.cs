using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Heron.ClientControls.Gallery.Pages;

/// <summary>
/// 色彩规范页：色阶、语义 Token 与对比度表格从 ColorTokens.axaml 实时读取并计算，
/// 修改 Token 后本页自动反映，避免文档与资源字典脱节。
/// </summary>
public sealed partial class ColorSystemPage : UserControl
{
    private bool _attached;
    private static readonly (string Title, string Prefix, string[] Steps, string Note)[] Scales =
    [
        ("Jade 玉色", "Jade", ["50", "100", "200", "300", "400", "500", "600", "700", "800", "900"],
            "侧栏、品牌区域、柔和背景。600 已由 #668760 压暗为 #5D7B57。"),
        ("Neutral 中性", "Neutral", ["0", "50", "100", "200", "300", "400", "500", "600", "700", "800", "900"],
            "正文、辅助文字、内容背景、边框。浅阶带极轻暖绿，文字阶饱和度 < 6%。"),
        ("Blue 操作 / 信息", "Blue", ["50", "100", "200", "300", "600", "700", "800"], "主操作、链接、焦点、选中。"),
        ("Emerald 成功", "Emerald", ["50", "200", "600", "700"], "偏蓝翠绿，与玉色拉开色相。"),
        ("Amber 警告", "Amber", ["50", "200", "500", "700"], "500 只用于图形，文字用 700。"),
        ("Red 危险", "Red", ["50", "100", "200", "600", "700"], "文字用 700；600 只用于图标和二次确认实心按钮。"),
    ];

    private static readonly (string Group, (string Key, string Source, string Usage)[] Items)[] SemanticTokens =
    [
        ("背景", [
            ("BgAppBrush", "Sage100", "窗口底、侧栏（灰玉区域）"),
            ("BgContentBrush", "Neutral100", "页面内容区底色（比卡片深一档）"),
            ("BgSurfaceBrush", "Neutral0", "卡片、面板、输入框"),
            ("BgSurfaceSubtleBrush", "Neutral50", "卡片内次级区块、代码块"),
            ("BgHoverBrush", "Neutral100", "悬停"),
            ("BgPressedBrush", "Neutral200", "按下"),
            ("BgDisabledBrush", "Neutral100", "禁用底色"),
            ("BgSelectedBrush", "Blue50", "列表 / 卡片选中"),
        ]),
        ("文字", [
            ("FgPrimaryBrush", "Neutral900", "页面标题、卡片标题"),
            ("FgDefaultBrush", "Neutral700", "正文"),
            ("FgSecondaryBrush", "Neutral600", "说明、次要信息"),
            ("FgMetaBrush", "Neutral500", "时间戳、计数；仅小号非关键信息"),
            ("FgDisabledBrush", "Neutral400", "禁用文字、占位符"),
            ("FgOnJadeBrush", "Jade900", "玉色区域上的深色文字"),
            ("FgOnAccentBrush", "Neutral0", "蓝色实心按钮上的文字"),
            ("FgLinkBrush", "Blue600", "链接"),
        ]),
        ("边框", [
            ("BorderSubtleBrush", "Neutral100", "卡片内分隔线"),
            ("BorderDefaultBrush", "Neutral200", "卡片边框"),
            ("BorderStrongBrush", "Neutral300", "输入框、次按钮"),
            ("BorderFocusBrush", "Blue300", "键盘焦点"),
            ("BorderSelectedBrush", "Blue600", "选中描边"),
        ]),
        ("操作", [
            ("AccentBrush", "Blue600", "主按钮"),
            ("AccentHoverBrush", "Blue700", "主按钮悬停"),
            ("AccentPressedBrush", "Blue800", "主按钮按下"),
            ("AccentSubtleBrush", "Blue50", "浅蓝底（任务浮层、选中）"),
        ]),
        ("导航 / 品牌", [
            ("NavBgBrush", "Sage100", "侧栏底（灰玉，降饱和）"),
            ("NavHoverBgBrush", "Sage200", "导航悬停"),
            ("NavSelectedBgBrush", "Sage300", "导航选中底（比悬停深一档）"),
            ("NavIndicatorBrush", "Jade700", "导航选中左侧 4px 竖条"),
            ("NavFgBrush", "Neutral600", "导航文字"),
            ("NavSelectedFgBrush", "Jade900", "导航选中文字"),
            ("NavIconBrush", "Jade600", "导航图标"),
        ]),
        ("状态", [
            ("SuccessFgBrush", "Emerald700", "成功文字 / 实心徽标"),
            ("SuccessBgBrush", "Emerald50", "成功浅底"),
            ("InfoFgBrush", "Blue600", "信息 / 进行中"),
            ("InfoBgBrush", "Blue50", "信息浅底"),
            ("WarningFgBrush", "Amber700", "警告文字"),
            ("WarningBgBrush", "Amber50", "警告浅底"),
            ("DangerFgBrush", "Red700", "危险文字"),
            ("DangerBgBrush", "Red50", "危险浅底"),
            ("NeutralStatusFgBrush", "Neutral600", "中性状态"),
            ("NeutralStatusBgBrush", "Neutral100", "中性浅底、分组标签"),
        ]),
        ("类别（不表达状态）", [
            ("CategoryCodeBgBrush", "CategoryCode50", "开发类标签浅底"),
            ("CategoryCodeFgBrush", "CategoryCode700", "开发类标签文字 / 图标"),
            ("CategoryCodeStripeBrush", "CategoryCode500", "开发类图标色"),
            ("CategoryArtBgBrush", "Jade50", "美术类标签浅底"),
            ("CategoryArtFgBrush", "Jade800", "美术类标签文字 / 图标"),
            ("CategoryArtStripeBrush", "Jade600", "美术类图标色"),
        ]),
    ];

    private static readonly (string Fg, string Bg, string Usage, double Required)[] Pairs =
    [
        ("FgPrimaryBrush", "BgSurfaceBrush", "标题 / 卡片", 4.5),
        ("FgDefaultBrush", "BgSurfaceBrush", "正文 / 卡片", 4.5),
        ("FgSecondaryBrush", "BgSurfaceBrush", "次要文字 / 卡片", 4.5),
        ("FgSecondaryBrush", "BgContentBrush", "次要文字 / 内容区", 4.5),
        ("FgMetaBrush", "BgSurfaceBrush", "元信息（按图形 3:1）", 3.0),
        ("FgDisabledBrush", "BgSurfaceBrush", "禁用文字（豁免）", 0),
        ("FgOnJadeBrush", "NavSelectedBgBrush", "导航选中文字", 4.5),
        ("NavFgBrush", "NavBgBrush", "导航文字", 4.5),
        ("NavIconBrush", "NavBgBrush", "导航图标（图形 3:1）", 3.0),
        ("FgOnAccentBrush", "AccentBrush", "主按钮文字", 4.5),
        ("FgLinkBrush", "BgSurfaceBrush", "链接", 4.5),
        ("InfoFgBrush", "InfoBgBrush", "信息徽标", 4.5),
        ("SuccessFgBrush", "SuccessBgBrush", "成功徽标", 4.5),
        ("FgOnAccentBrush", "SuccessSolidBrush", "成功实心徽标", 4.5),
        ("WarningFgBrush", "WarningBgBrush", "警告徽标", 4.5),
        ("DangerFgBrush", "DangerBgBrush", "危险徽标 / 按钮", 4.5),
        ("FgOnAccentBrush", "DangerSolidBrush", "危险实心（二次确认）", 4.5),
        ("NeutralStatusFgBrush", "NeutralStatusBgBrush", "中性徽标 / 分组标签", 4.5),
        ("CategoryCodeFgBrush", "CategoryCodeBgBrush", "开发类别标签", 4.5),
        ("CategoryArtFgBrush", "CategoryArtBgBrush", "美术类别标签", 4.5),
        ("CategoryCodeStripeBrush", "BgSurfaceBrush", "开发类色条（图形 3:1）", 3.0),
        ("CategoryArtStripeBrush", "BgSurfaceBrush", "美术类色条（图形 3:1）", 3.0),
    ];

    public ColorSystemPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            BuildTables();
        };
        DetachedFromVisualTree += (_, _) => _attached = false;
        ActualThemeVariantChanged += (_, _) =>
        {
            if (_attached) BuildTables();
        };
    }

    private void BuildTables()
    {
        ScalePanel.Children.Clear();
        SemanticPanel.Children.Clear();
        PairPanel.Children.Clear();

        foreach (var (title, prefix, steps, note) in Scales)
            ScalePanel.Children.Add(BuildScale(title, prefix, steps, note));

        foreach (var (group, items) in SemanticTokens)
            SemanticPanel.Children.Add(BuildSemanticGroup(group, items));

        PairPanel.Children.Add(PairHeader());
        foreach (var pair in Pairs)
            PairPanel.Children.Add(BuildPair(pair.Fg, pair.Bg, pair.Usage, pair.Required));
    }

    private Control BuildScale(string title, string prefix, string[] steps, string note)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var step in steps)
        {
            var color = FindColor($"{prefix}{step}Color");
            var onWhite = Contrast(color, Colors.White);
            var textColor = Contrast(color, Colors.White) >= Contrast(color, FindColor("Neutral900Color"))
                ? Colors.White
                : FindColor("Neutral900Color");
            row.Children.Add(new Border
            {
                Width = 92,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(8),
                BorderBrush = Brush("BorderDefaultBrush"),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = new StackPanel
                {
                    Children =
                    {
                        new Border
                        {
                            Height = 56,
                            Background = new SolidColorBrush(color),
                            Padding = new Thickness(8, 6),
                            Child = new TextBlock
                            {
                                Text = step, FontWeight = FontWeight.SemiBold, FontSize = 13,
                                Foreground = new SolidColorBrush(textColor)
                            }
                        },
                        new StackPanel
                        {
                            Margin = new Thickness(8, 6, 8, 7), Spacing = 1,
                            Children =
                            {
                                Mono(Hex(color), "FgDefaultBrush"),
                                new TextBlock
                                {
                                    Text = $"白底 {onWhite:0.00}", FontSize = 11,
                                    Foreground = Brush("FgSecondaryBrush")
                                }
                            }
                        }
                    }
                }
            });
        }

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush("FgPrimaryBrush") },
                new TextBlock { Text = note, FontSize = 12, Foreground = Brush("FgSecondaryBrush"), TextWrapping = TextWrapping.Wrap },
                row
            }
        };
    }

    private Control BuildSemanticGroup(string group, (string Key, string Source, string Usage)[] items)
    {
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(new TextBlock
        {
            Text = group, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Foreground = Brush("FgPrimaryBrush"), Margin = new Thickness(0, 4, 0, 6)
        });
        foreach (var (key, source, usage) in items)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("28,220,110,80,*"),
                Height = 30
            };
            grid.Children.Add(Cell(new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(4),
                Background = Brush(key), BorderBrush = Brush("BorderStrongBrush"), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
            }, 0));
            grid.Children.Add(Cell(Mono(key, "FgPrimaryBrush"), 1));
            grid.Children.Add(Cell(new TextBlock { Text = source, FontSize = 12, Foreground = Brush("FgSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center }, 2));
            grid.Children.Add(Cell(Mono(Hex(((ISolidColorBrush)Brush(key)).Color), "FgSecondaryBrush"), 3));
            grid.Children.Add(Cell(new TextBlock { Text = usage, FontSize = 12, Foreground = Brush("FgDefaultBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 4));
            panel.Children.Add(new Border
            {
                BorderBrush = Brush("BorderSubtleBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid
            });
        }

        return panel;
    }

    private Control PairHeader()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,90,90"), Height = 28 };
        string[] labels = ["示例", "前景 / 背景 · 用途", "对比度", "结果"];
        for (var i = 0; i < labels.Length; i++)
            grid.Children.Add(Cell(new TextBlock { Text = labels[i], FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush("FgSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center }, i));
        return new Border { BorderBrush = Brush("BorderDefaultBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    private Control BuildPair(string fg, string bg, string usage, double required)
    {
        var ratio = Contrast(((ISolidColorBrush)Brush(fg)).Color, ((ISolidColorBrush)Brush(bg)).Color);
        var pass = required == 0 || ratio >= required;
        var verdict = required == 0 ? "豁免" : pass ? $"达标 ≥{required:0.#}" : $"不足 <{required:0.#}";

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,90,90"), MinHeight = 40 };
        grid.Children.Add(Cell(new Border
        {
            Background = Brush(bg), BorderBrush = Brush("BorderDefaultBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4), Margin = new Thickness(0, 4, 12, 4),
            Child = new TextBlock { Text = "玉色工具 Aa", Foreground = Brush(fg), FontSize = 13 }
        }, 0));
        grid.Children.Add(Cell(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Mono($"{fg} / {bg}", "FgDefaultBrush"),
                new TextBlock { Text = usage, FontSize = 11, Foreground = Brush("FgSecondaryBrush") }
            }
        }, 1));
        grid.Children.Add(Cell(new TextBlock { Text = $"{ratio:0.00} : 1", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush("FgPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center }, 2));
        grid.Children.Add(Cell(new Border
        {
            Background = Brush(pass ? "SuccessBgBrush" : "DangerBgBrush"),
            BorderBrush = Brush(pass ? "SuccessBorderBrush" : "DangerBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = verdict, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush(pass ? "SuccessFgBrush" : "DangerFgBrush") }
        }, 3));
        return new Border { BorderBrush = Brush("BorderSubtleBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    private static Control Cell(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private TextBlock Mono(string text, string brushKey) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
        FontSize = 11.5,
        Foreground = Brush(brushKey),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Magenta;

    private Color FindColor(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : Colors.Magenta;

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>WCAG 2.x 相对亮度对比度。</summary>
    internal static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
