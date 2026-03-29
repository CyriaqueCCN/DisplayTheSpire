using Godot;

namespace DisplayTheSpire.UI;

internal static class DtsTheme
{
    // Colors
    public static readonly Color Cream         = new Color("FFF6E2");
    public static readonly Color KeyLabel      = new Color("7A8FA8");
    public static readonly Color Outline       = new Color("18282F");
    public static readonly Color TooltipBg     = new Color(0.09f, 0.15f, 0.21f);  // #172436
    public static readonly Color SeparatorLine = new Color(1f, 0.965f, 0.886f, 0.35f);
    public static readonly Color Border        = new Color(1f, 0.965f, 0.886f, 0.18f);
    public static readonly Color EliteYellow   = new Color("E8C840");

    // Textures
    public const string BackdropTexture = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_char_backdrop.tres";
    public const int    BackdropPatch   = 32;

    // Tooltip dimensions / padding
    public const int   CornerRadius  = 10;
    public const float TooltipAlpha  = 0.97f;
    public const float TooltipPadH   = 16f;
    public const float TooltipPadV   = 12f;

    // Font sizes
    public const int FontSizeTitle      = 32;
    public const int FontSizeValue      = 15;
    public const int FontSizeKey        = 13;
    public const int FontSizeIcon       = 12;
    public const int OutlineSizeLarge   = 12;
    public const int OutlineSizeSmall   = 6;

    // Z-indices (modals only - widgets use default z=0 to stay under NTransition overlay)
    public const int ZModalBackdrop = 99;
    public const int ZModalPanel    = 100;

    // Modal
    public const float ModalBackdropAlpha = 0.6f;
    public const float ModalFadeDuration  = 0.25f;
    public const int   ModalTitleFontSize = 18;
    public const int   ModalPadH         = 20;
    public const int   ModalPadV         = 16;
    public const int   ModalTitleSep     = 10;
    public const int   ModalContentSep   = 8;
}
