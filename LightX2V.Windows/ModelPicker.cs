using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Shape = System.Windows.Shapes.Path;

namespace LightX2V.Windows;

internal sealed class ModelPicker
{
    const string FamilyName = "Qwen-Image-2.1-Turbo";
    static readonly Brush Selection = new SolidColorBrush(Color.FromRgb(233, 233, 233));
    readonly MainViewModel vm;
    readonly Button anchor, family, standard;
    readonly Popup popup, variantPopup;
    readonly Border variants;
    readonly TextBlock familyLabel;
    readonly Shape familyCheck, variantCheck;

    internal ModelPicker(Window window, Button anchor, MainViewModel vm)
    {
        this.anchor = anchor;
        this.vm = vm;
        var panels = new Grid { DataContext = vm };
        TextElement.SetFontFamily(panels, window.FontFamily);
        KeyboardNavigation.SetTabNavigation(panels, KeyboardNavigationMode.Cycle);
        var familyContent = new StackPanel();
        var title = SetupUi.Text("Choose model", 11, muted: true);
        title.Margin = new Thickness(9, 2, 9, 6);
        familyContent.Children.Add(title);
        familyLabel = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.Medium,
            FontFamily = window.FontFamily, VerticalAlignment = VerticalAlignment.Center };
        familyCheck = Mark("M1,5 L4,8 9,2");
        variantCheck = Mark("M1,5 L4,8 9,2");
        var familyRow = new DockPanel { LastChildFill = true };
        var chevron = Mark("M3,1 L7,5 3,9");
        chevron.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(chevron, Dock.Right);
        familyRow.Children.Add(chevron);
        DockPanel.SetDock(familyCheck, Dock.Right);
        familyRow.Children.Add(familyCheck);
        familyRow.Children.Add(familyLabel);
        family = PickerButton(window, "ModelFamily", familyRow);
        family.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        family.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(TextBlock.Text)) { Source = familyLabel });
        family.SetBinding(AutomationProperties.HelpTextProperty, "T[Model variants]");
        family.Click += (_, _) => ShowVariants(Keyboard.IsKeyDown(Key.Enter) || Keyboard.IsKeyDown(Key.Space));
        family.MouseEnter += (_, _) => ShowVariants(false);
        family.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Right or Key.Down or Key.Enter or Key.Space)) return;
            e.Handled = true;
            ShowVariants(true);
        };
        familyContent.Children.Add(family);
        var familyCard = Card(familyContent, 276, 10);
        var familyBubble = new Grid { Margin = new Thickness(10, 10, 10, 12), VerticalAlignment = VerticalAlignment.Top };
        familyBubble.Children.Add(familyCard);
        var tip = new Shape
        {
            Width = 14, Height = 7, Data = Geometry.Parse("M0,0 L7,6 14,0"), Fill = Brushes.White,
            Stroke = SetupUi.Line, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -6), IsHitTestVisible = false
        };
        familyBubble.Children.Add(tip);
        panels.Children.Add(familyBubble);

        var variantContent = new StackPanel();
        variantContent.Children.Add(new TextBlock { Text = FamilyName, FontSize = 13, FontWeight = FontWeights.Medium,
            FontFamily = window.FontFamily, Margin = new Thickness(0, 0, 0, 12) });
        var variantRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var standardLabel = SetupUi.Text("Standard", 12);
        standardLabel.FontWeight = FontWeights.Medium;
        variantRow.Children.Add(standardLabel);
        variantCheck.Margin = new Thickness(8, 0, 0, 0);
        variantRow.Children.Add(variantCheck);
        standard = PickerButton(window, "ModelVariantStandard", variantRow);
        standard.SetBinding(AutomationProperties.NameProperty, "T[Standard]");
        standard.Padding = new Thickness(0);
        standard.Height = 32;
        standard.Template = CapsuleTemplate();
        standard.Click += (_, _) =>
        {
            if (vm.CanSelectModel) vm.SelectStandardModel();
            Close(restoreFocus: true);
        };
        standard.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Tab)) return;
            e.Handled = true;
            HideVariants();
            family.Focus();
        };
        variantContent.Children.Add(new Border
        {
            Background = Selection, CornerRadius = new CornerRadius(20), Padding = new Thickness(4), Child = standard
        });
        variants = Card(variantContent, 302, 16);
        var variantPanel = new Grid { DataContext = vm };
        TextElement.SetFontFamily(variantPanel, window.FontFamily);
        variants.Margin = new Thickness(10);
        variantPanel.Children.Add(variants);
        var sideTip = new Shape
        {
            Width = 8, Height = 16, Data = Geometry.Parse("M8,0 L0,8 8,16"), Fill = Brushes.White,
            Stroke = SetupUi.Line, Margin = new Thickness(3,0,3,0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false
        };
        variantPanel.Children.Add(sideTip);
        variantPopup = new Popup
        {
            Child = variantPanel, PlacementTarget = family, Placement = PlacementMode.Custom,
            StaysOpen = true, AllowsTransparency = true
        };
        variantPopup.CustomPopupPlacementCallback = (size, target, _) => [
            new(new Point(target.Width, (target.Height-size.Height)/2), PopupPrimaryAxis.Vertical),
            new(new Point(-size.Width, (target.Height-size.Height)/2), PopupPrimaryAxis.Vertical)
        ];
        variantPopup.Opened += (_, _) =>
        {
            var left = variants.PointToScreen(new Point()).X < family.PointToScreen(new Point()).X;
            sideTip.HorizontalAlignment = left ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            sideTip.RenderTransformOrigin = new Point(.5,.5);
            sideTip.RenderTransform = new ScaleTransform(left ? -1 : 1, 1);
        };
        variantPanel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(restoreFocus: true); }
        };
        popup = new Popup
        {
            Child = panels, PlacementTarget = anchor, Placement = PlacementMode.Custom, StaysOpen = true,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None
        };
        // Both sizes come from WPF in the same coordinate system, including on scaled displays.
        popup.CustomPopupPlacementCallback = (size, target, _) => [
            new(new Point((target.Width-size.Width)/2, -size.Height), PopupPrimaryAxis.Horizontal),
            new(new Point((target.Width-size.Width)/2, target.Height), PopupPrimaryAxis.Horizontal)
        ];
        popup.Opened += (_, _) =>
        {
            var below = family.PointToScreen(new Point()).Y > anchor.PointToScreen(new Point()).Y;
            tip.VerticalAlignment = below ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            tip.Margin = below ? new Thickness(0,-6,0,0) : new Thickness(0,0,0,-6);
            tip.RenderTransformOrigin = new Point(.5,.5);
            tip.RenderTransform = new ScaleTransform(1, below ? -1 : 1);
            family.Focus();
        };
        popup.Closed += (_, _) => HideVariants();
        panels.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(restoreFocus: true);
        };
        anchor.IsEnabledChanged += (_, _) => { if (!anchor.IsEnabled) Close(); };
        window.PreviewMouseDown += (_, e) =>
        {
            if (!anchor.IsMouseOver || e.ChangedButton != MouseButton.Left) Close();
        };
        window.Deactivated += (_, _) => Close();
        window.LocationChanged += (_, _) => Close();
        window.SizeChanged += (_, _) => Close();
        window.Closed += (_, _) => Close();
    }

    internal void Toggle()
    {
        if (popup.IsOpen) { Close(restoreFocus: true); return; }
        familyLabel.Text = vm.HasSelectedModel ? vm.ModelSelectorLabel : FamilyName;
        familyCheck.Opacity = variantCheck.Opacity = vm.HasSelectedModel ? 1 : 0;
        standard.Background = vm.HasSelectedModel ? Brushes.White : Brushes.Transparent;
        popup.PopupAnimation = SystemParameters.ClientAreaAnimation ? PopupAnimation.Fade : PopupAnimation.None;
        popup.IsOpen = true;
    }

    void ShowVariants(bool focus)
    {
        if (!popup.IsOpen) return;
        if (!variantPopup.IsOpen)
        {
            family.Background = Selection;
            variantPopup.IsOpen = true;
            if (SystemParameters.ClientAreaAnimation)
                variants.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new SineEase(), FillBehavior = FillBehavior.Stop });
        }
        if (focus) standard.Focus();
    }

    void HideVariants()
    {
        variants.BeginAnimation(UIElement.OpacityProperty, null);
        variantPopup.IsOpen = false;
        family.Background = Brushes.Transparent;
    }

    void Close(bool restoreFocus = false)
    {
        popup.IsOpen = false;
        if (restoreFocus) anchor.Focus();
    }

    static Button PickerButton(Window window, string id, UIElement content)
    {
        var button = new Button { Content = content, Padding = new Thickness(9, 6, 9, 6),
            Style = (Style)window.FindResource(typeof(Button)) };
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    static Shape Mark(string data) => new()
    {
        Data = Geometry.Parse(data), Width = 10, Height = 10, Stroke = SetupUi.Ink,
        StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center
    };

    static Border Card(UIElement content, double width, double padding) => new()
    {
        Child = content, Width = width, Padding = new Thickness(padding), VerticalAlignment = VerticalAlignment.Top,
        CornerRadius = new CornerRadius(10), Background = Brushes.White, BorderBrush = SetupUi.Line, BorderThickness = new Thickness(1),
        Effect = new DropShadowEffect { BlurRadius = 13, ShadowDepth = 2, Opacity = .25 }
    };

    static ControlTemplate CapsuleTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var face = new FrameworkElementFactory(typeof(Border), "Face");
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        face.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        face.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background))
        { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(presenter);
        template.VisualTree = face;
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, SetupUi.Muted, "Face"));
        template.Triggers.Add(focus);
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.WhiteSmoke, "Face"));
        template.Triggers.Add(hover);
        return template;
    }
}
