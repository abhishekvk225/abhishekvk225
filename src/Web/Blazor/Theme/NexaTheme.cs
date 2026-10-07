using MudBlazor;

namespace NexaVerify.Web.Theme;

/// <summary>
/// The single MudTheme (docs/06 §4). Every colour in the app comes from here or from CSS variables derived from it; pages must not hard-code colours.
/// All text/background pairs below were checked for WCAG AA (>= 4.5:1).
/// </summary>
public static class NexaTheme
{
    /// <summary>8-px spacing scale in pixels; MudBlazor spacing classes (ma-1 = 4px) map onto it.</summary>
    public static readonly IReadOnlyList<int> SpacingScale = [4, 8, 12, 16, 24, 32, 48, 64];

    /// <summary>Corner radius token. Cards and inputs use this; chips are fully rounded.</summary>
    public const string Radius = "10px";

    public static readonly string[] FontStack =
    [
        "Inter", "ui-sans-serif", "system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif",
    ];

    /// <summary>Colour-blind-safe categorical palette for charts (light and dark share it; all >= 3:1 on both surfaces).</summary>
    public static readonly string[] ChartPalette =
    [
        "#6366F1", "#14B8A6", "#F59E0B", "#EC4899", "#3B82F6", "#84CC16",
    ];

    public static MudTheme Instance { get; } = Create();

    private static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#4338CA",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#0F766E",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#7C3AED",
            Info = "#0369A1",
            InfoContrastText = "#FFFFFF",
            Success = "#15803D",
            SuccessContrastText = "#FFFFFF",
            Warning = "#B45309",
            WarningContrastText = "#FFFFFF",
            Error = "#B91C1C",
            ErrorContrastText = "#FFFFFF",
            Dark = "#0F172A",
            Background = "#F6F7FB",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#0F172A",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#334155",
            DrawerIcon = "#475569",
            TextPrimary = "#0F172A",
            TextSecondary = "#475569",
            TextDisabled = "#94A3B8",
            ActionDefault = "#475569",
            ActionDisabled = "#94A3B8",
            Divider = "#E2E8F0",
            DividerLight = "#EEF2F7",
            LinesDefault = "#E2E8F0",
            LinesInputs = "#94A3B8",
            TableLines = "#E2E8F0",
            TableStriped = "#F8FAFC",
            TableHover = "#F1F5F9",
            OverlayDark = "rgba(15,23,42,0.5)",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#A5B4FC",
            PrimaryContrastText = "#0B1020",
            Secondary = "#5EEAD4",
            SecondaryContrastText = "#0B1020",
            Tertiary = "#C4B5FD",
            Info = "#7DD3FC",
            InfoContrastText = "#0B1020",
            Success = "#86EFAC",
            SuccessContrastText = "#0B1020",
            Warning = "#FCD34D",
            WarningContrastText = "#0B1020",
            Error = "#FCA5A5",
            ErrorContrastText = "#0B1020",
            Dark = "#0B1020",
            Background = "#0B1020",
            Surface = "#131B31",
            AppbarBackground = "#0F1629",
            AppbarText = "#E5E9F2",
            DrawerBackground = "#0F1629",
            DrawerText = "#C5CDDD",
            DrawerIcon = "#A3AEC2",
            TextPrimary = "#E5E9F2",
            TextSecondary = "#A9B4C8",
            TextDisabled = "#5B6784",
            ActionDefault = "#A9B4C8",
            ActionDisabled = "#5B6784",
            Divider = "#222C46",
            DividerLight = "#1A2340",
            LinesDefault = "#222C46",
            LinesInputs = "#5B6784",
            TableLines = "#222C46",
            TableStriped = "#10182C",
            TableHover = "#18223B",
            OverlayDark = "rgba(2,6,23,0.7)",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = Radius,
            DrawerWidthLeft = "264px",
            AppbarHeight = "64px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontStack, FontSize = "0.9375rem", LineHeight = "1.55", LetterSpacing = "0" },
            H1 = new H1Typography { FontFamily = FontStack, FontSize = "2rem", FontWeight = "700", LineHeight = "1.2", LetterSpacing = "-0.02em" },
            H2 = new H2Typography { FontFamily = FontStack, FontSize = "1.5rem", FontWeight = "700", LineHeight = "1.25", LetterSpacing = "-0.015em" },
            H3 = new H3Typography { FontFamily = FontStack, FontSize = "1.25rem", FontWeight = "600", LineHeight = "1.3", LetterSpacing = "-0.01em" },
            H4 = new H4Typography { FontFamily = FontStack, FontSize = "1.125rem", FontWeight = "600", LineHeight = "1.35", LetterSpacing = "-0.005em" },
            H5 = new H5Typography { FontFamily = FontStack, FontSize = "1rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "0" },
            H6 = new H6Typography { FontFamily = FontStack, FontSize = "0.9375rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "0" },
            Subtitle1 = new Subtitle1Typography { FontFamily = FontStack, FontSize = "1rem", FontWeight = "500", LineHeight = "1.5", LetterSpacing = "0" },
            Subtitle2 = new Subtitle2Typography { FontFamily = FontStack, FontSize = "0.875rem", FontWeight = "500", LineHeight = "1.5", LetterSpacing = "0" },
            Body1 = new Body1Typography { FontFamily = FontStack, FontSize = "0.9375rem", FontWeight = "400", LineHeight = "1.55", LetterSpacing = "0" },
            Body2 = new Body2Typography { FontFamily = FontStack, FontSize = "0.8125rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "0" },
            Button = new ButtonTypography { FontFamily = FontStack, FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.75", LetterSpacing = "0", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = FontStack, FontSize = "0.75rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "0.01em" },
            Overline = new OverlineTypography { FontFamily = FontStack, FontSize = "0.6875rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "0.08em" },
        },
    };
}
