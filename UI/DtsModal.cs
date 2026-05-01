using System;
using Godot;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

// Self-contained modal dialog
//
// Single use. Once Close() finishes, the backdrop is freed; create a new
// instance for a subsequent show.
internal sealed class DtsModal
{
    // VBox host for caller-provided body content
    public VBoxContainer Content { get; }

    // Full-screen backdrop. Add overlay nodes (e.g. tooltips) here so they
    // can float above the panel without being clipped by any inner scroll
    // container. Positions on this layer are in backdrop-local coordinates.
    public Control OverlayLayer => _backdrop;

    // Fires once the fade-out finishes and the backdrop has been freed
    public event Action? Closed;

    private readonly Control    _backdrop;
    private readonly Control    _panel;
    private readonly ColorRect  _backdropColor;
    private Tween?              _tween;
    private bool                _closing;

    private readonly float _panelW;
    private readonly float _panelH;

    public DtsModal(string title, float panelW = 500f, float panelH = 400f)
    {
        _panelW = panelW;
        _panelH = panelH;

        // Backdrop: full-screen, eats clicks and Esc
        _backdrop = new Control
        {
            AnchorRight  = 1f,
            AnchorBottom = 1f,
            ZIndex       = DtsTheme.ZModalBackdrop,
            MouseFilter  = Control.MouseFilterEnum.Stop,
            // FocusMode=All so the backdrop can grab keyboard focus and
            // receive Esc through GuiInput (GUI phase) before the game's
            // pause handler fires in _Input.
            FocusMode    = Control.FocusModeEnum.All,
        };

        _backdropColor = new ColorRect
        {
            Color        = new Color(0f, 0f, 0f, 0f),
            AnchorRight  = 1f,
            AnchorBottom = 1f,
            MouseFilter  = Control.MouseFilterEnum.Ignore,
        };
        _backdrop.AddChild(_backdropColor);

        // Close on left-click outside the panel or on Esc
        //
        // Esc lives in GuiInput rather than _Input because _Input traverses
        // root to leaf, so a top-level pause handler would consume the key
        // before our deeply nested node ever sees it. GuiInput runs in the
        // GUI phase, ahead of any _Input handler. While the backdrop holds
        // keyboard focus it captures all key events here, and calling
        // SetInputAsHandled would block the pause menu entirely.
        _backdrop.GuiInput += (InputEvent @event) =>
        {
            if (@event is InputEventMouseButton mb && mb.Pressed
                && mb.ButtonIndex == MouseButton.Left)
            {
                Close();
                return;
            }
            if (@event is InputEventKey key && key.Pressed && !key.Echo
                && key.PhysicalKeycode == Key.Escape)
            {
                // Close but do not consume the event: let it propagate to
                // _Input so the game's pause handler can also react.
                Close();
            }
        };

        // Panel.
        _panel = new Control
        {
            AnchorLeft   = 0.5f, AnchorRight  = 0.5f,
            AnchorTop    = 0.5f, AnchorBottom  = 0.5f,
            OffsetLeft   = -panelW / 2f,
            OffsetRight  =  panelW / 2f,
            OffsetTop    = -panelH / 2f,
            OffsetBottom =  panelH / 2f,
            ZIndex       = DtsTheme.ZModalPanel,
            // Stop: the panel consumes clicks so backdrop.GuiInput never
            // fires for clicks landing inside the panel.
            MouseFilter  = Control.MouseFilterEnum.Stop,
        };

        var panelBg = new Panel { AnchorRight = 1f, AnchorBottom = 1f };
        panelBg.AddThemeStyleboxOverride("panel", MakePanelStyleBox());
        _panel.AddChild(panelBg);

        var inner = new VBoxContainer
        {
            AnchorLeft   = 0f, AnchorRight  = 1f,
            AnchorTop    = 0f, AnchorBottom  = 1f,
            OffsetLeft   =  DtsTheme.ModalPadH,
            OffsetRight  = -DtsTheme.ModalPadH,
            OffsetTop    =  DtsTheme.ModalPadV,
            OffsetBottom = -DtsTheme.ModalPadV,
        };
        inner.AddThemeConstantOverride("separation", DtsTheme.ModalTitleSep);
        _panel.AddChild(inner);

        // Title bar.
        var titleBar = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.Fill };

        var titleLabel = new Label
        {
            Text                = title,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment   = VerticalAlignment.Center,
            AutowrapMode        = TextServer.AutowrapMode.Off,
        };
        titleLabel.AddThemeFontSizeOverride("font_size", DtsTheme.ModalTitleFontSize);
        titleLabel.AddThemeColorOverride("font_color",         DtsTheme.Cream);
        titleLabel.AddThemeColorOverride("font_outline_color", DtsTheme.Outline);
        titleLabel.AddThemeConstantOverride("outline_size",    DtsTheme.OutlineSizeSmall);
        titleBar.AddChild(titleLabel);
        titleBar.AddChild(MakeCloseButton());

        inner.AddChild(titleBar);

        // Separator under the title.
        inner.AddChild(new ColorRect
        {
            Color               = DtsTheme.SeparatorLine,
            CustomMinimumSize   = new Vector2(0, 1),
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            MouseFilter         = Control.MouseFilterEnum.Ignore,
        });

        // Body.
        Content = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
        };
        Content.AddThemeConstantOverride("separation", DtsTheme.ModalContentSep);
        inner.AddChild(Content);

        _backdrop.AddChild(_panel);
    }

    // Adds the modal under host (typically the full-screen NTopBar) and
    // tweens the backdrop alpha in.
    public void Show(Control host)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(host)) return;
            host.AddChild(_backdrop);
            // Defer the focus grab by one frame so GuiInput receives Esc
            // ahead of the game's _Input pause handler.
            _backdrop.CallDeferred(Control.MethodName.GrabFocus);
            _tween?.Kill();
            _tween = host.CreateTween();
            _tween.TweenProperty(_backdropColor, "color:a", DtsTheme.ModalBackdropAlpha,
                                 DtsTheme.ModalFadeDuration)
                  .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
        catch (Exception ex) { ModLog.Error("DtsModal.Show", ex); }
    }

    // Tweens the backdrop out and frees the modal at the end of the tween.
    public void Close()
    {
        try
        {
            if (_closing) return;
            _closing = true;
            if (!GodotObject.IsInstanceValid(_backdrop)) { Closed?.Invoke(); return; }

            _tween?.Kill();
            _tween = _backdrop.CreateTween();
            _tween.TweenProperty(_backdropColor, "color:a", 0f, DtsTheme.ModalFadeDuration)
                  .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            _tween.TweenCallback(Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(_backdrop))
                    _backdrop.QueueFree();
                Closed?.Invoke();
            }));
        }
        catch (Exception ex) { ModLog.Error("DtsModal.Close", ex); }
    }

    // Cream "x" glyph at rest, gold on hover, with a quick scale-up on enter
    // and a softer Expo-Out spring back on exit. Mirrors the NButton hover
    // contract used by NBackButton and NCloseButton.
    private Control MakeCloseButton()
    {
        const float Size = 28f;

        var btn = new Control
        {
            CustomMinimumSize   = new Vector2(Size, Size),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter,
            MouseFilter         = Control.MouseFilterEnum.Stop,
            // Pivot at the center so the scale tween radiates outward.
            PivotOffset         = new Vector2(Size / 2f, Size / 2f),
        };

        var lbl = new Label
        {
            Text                = "x",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            AnchorRight         = 1f,
            AnchorBottom        = 1f,
            AutowrapMode        = TextServer.AutowrapMode.Off,
        };
        lbl.AddThemeFontSizeOverride("font_size", 16);
        lbl.AddThemeColorOverride("font_color",         DtsTheme.Cream);
        lbl.AddThemeColorOverride("font_outline_color", DtsTheme.Outline);
        lbl.AddThemeConstantOverride("outline_size",    DtsTheme.OutlineSizeSmall);
        btn.AddChild(lbl);

        Tween? t = null;
        btn.MouseEntered += () =>
        {
            lbl.AddThemeColorOverride("font_color", DtsTheme.EliteYellow);
            t?.Kill();
            t = btn.CreateTween();
            t.TweenProperty(btn, "scale", Vector2.One * 1.1f, 0.05f);
        };
        btn.MouseExited += () =>
        {
            lbl.AddThemeColorOverride("font_color", DtsTheme.Cream);
            t?.Kill();
            t = btn.CreateTween();
            t.TweenProperty(btn, "scale", Vector2.One, 0.5f)
             .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
        };
        btn.GuiInput += (@event) =>
        {
            if (@event is InputEventMouseButton mb && mb.Pressed
                && mb.ButtonIndex == MouseButton.Left)
                Close();
        };

        return btn;
    }

    private static StyleBoxFlat MakePanelStyleBox() => new StyleBoxFlat
    {
        BgColor                 = DtsTheme.TooltipBg,
        CornerRadiusTopLeft     = DtsTheme.CornerRadius,
        CornerRadiusTopRight    = DtsTheme.CornerRadius,
        CornerRadiusBottomLeft  = DtsTheme.CornerRadius,
        CornerRadiusBottomRight = DtsTheme.CornerRadius,
        BorderWidthLeft   = 1, BorderWidthRight  = 1,
        BorderWidthTop    = 1, BorderWidthBottom  = 1,
        BorderColor  = DtsTheme.Border,
        AntiAliasing = true,
    };

}
