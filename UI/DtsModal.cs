using System;
using Godot;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

/// <summary>
/// Self-contained modal dialog:
/// <list type="bullet">
///   <item>Full-screen semi-transparent backdrop with tween fade</item>
///   <item>Centered panel (dark bg, rounded corners, cream border)</item>
///   <item>Title bar with label + X close button</item>
///   <item>Click outside panel or X to close (with fade-out)</item>
/// </list>
/// Usage: <c>new DtsModal("Title").Show(topBar);</c>
/// <para>
/// <b>Single-use:</b> once <see cref="Close"/> completes the backdrop node is
/// freed. Do not call <see cref="Show"/> again on the same instance and create a
/// new one instead.
/// </para>
/// </summary>
internal sealed class DtsModal
{
    /// <summary>VBox for dialog content</summary>
    public VBoxContainer Content { get; }

    /// <summary>Fires after the modal has faded out and been freed</summary>
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

        // Backdrop
        _backdrop = new Control
        {
            AnchorRight  = 1f,
            AnchorBottom = 1f,
            ZIndex       = DtsTheme.ZModalBackdrop,
            MouseFilter  = Control.MouseFilterEnum.Stop,
        };

        // Dark tinted rect (starts fully transparent, tweened to target alpha)
        _backdropColor = new ColorRect
        {
            Color        = new Color(0f, 0f, 0f, 0f),
            AnchorRight  = 1f,
            AnchorBottom = 1f,
            MouseFilter  = Control.MouseFilterEnum.Ignore,
        };
        _backdrop.AddChild(_backdropColor);

        // Close when clicking the backdrop (outside the panel)
        _backdrop.GuiInput += (InputEvent @event) =>
        {
            if (@event is InputEventMouseButton mb && mb.Pressed
                && mb.ButtonIndex == MouseButton.Left)
                Close();
        };

        // Panel
        _panel = new Control
        {
            AnchorLeft   = 0.5f, AnchorRight  = 0.5f,
            AnchorTop    = 0.5f, AnchorBottom  = 0.5f,
            OffsetLeft   = -panelW / 2f,
            OffsetRight  =  panelW / 2f,
            OffsetTop    = -panelH / 2f,
            OffsetBottom =  panelH / 2f,
            ZIndex       = DtsTheme.ZModalPanel,
            // Stop: panel consumes clicks -> backdrop.GuiInput never fires for intra-panel clicks
            MouseFilter  = Control.MouseFilterEnum.Stop,
        };

        var panelBg = new Panel { AnchorRight = 1f, AnchorBottom = 1f };
        panelBg.AddThemeStyleboxOverride("panel", MakeStyleBox());
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

        // Title bar
        var titleBar = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.Fill };

        var titleLabel = new Label
        {
            Text                = title,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment   = VerticalAlignment.Center,
            AutowrapMode        = TextServer.AutowrapMode.Off,
        };
        titleLabel.AddThemeFontSizeOverride("font_size", DtsTheme.ModalTitleFontSize);
        titleLabel.AddThemeColorOverride("font_color", DtsTheme.Cream);
        titleLabel.AddThemeColorOverride("font_outline_color", DtsTheme.Outline);
        titleLabel.AddThemeConstantOverride("outline_size", DtsTheme.OutlineSizeSmall);
        titleBar.AddChild(titleLabel);

        var closeBtn = new Button
        {
            Text                = "×",
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter,
            FocusMode           = Control.FocusModeEnum.None,
            TooltipText         = "Close",
        };
        closeBtn.AddThemeFontSizeOverride("font_size", 22);
        closeBtn.AddThemeColorOverride("font_color", DtsTheme.Cream);
        closeBtn.Pressed += Close;
        titleBar.AddChild(closeBtn);

        inner.AddChild(titleBar);

        // Separator under title
        inner.AddChild(new ColorRect
        {
            Color               = DtsTheme.SeparatorLine,
            CustomMinimumSize   = new Vector2(0, 1),
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
        });

        // Content
        Content = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
        };
        Content.AddThemeConstantOverride("separation", DtsTheme.ModalContentSep);
        inner.AddChild(Content);

        _backdrop.AddChild(_panel);
    }

    // Public API

    /// <summary>
    /// Adds the modal to <paramref name="host"/> (typically NTopBar, which is full-screen)
    /// and fades in the backdrop.
    /// </summary>
    public void Show(Control host)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(host)) return;
            host.AddChild(_backdrop);
            _tween?.Kill();
            _tween = host.CreateTween();
            _tween.TweenProperty(_backdropColor, "color:a", DtsTheme.ModalBackdropAlpha,
                                 DtsTheme.ModalFadeDuration)
                  .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
        catch (Exception ex) { ModLog.Error("DtsModal.Show", ex); }
    }

    /// <summary>Fades out the backdrop then frees the modal</summary>
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

    // Internal

    private static StyleBoxFlat MakeStyleBox() => new StyleBoxFlat
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
