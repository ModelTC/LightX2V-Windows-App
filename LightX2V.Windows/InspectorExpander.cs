using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LightX2V.Windows;

public sealed class InspectorExpander : Expander
{
    static readonly DependencyProperty RevealProgressProperty = DependencyProperty.Register(
        "RevealProgress", typeof(double), typeof(InspectorExpander),
        new PropertyMetadata(0d, (element, change) => ((InspectorExpander)element).ApplyProgress((double)change.NewValue)));

    InspectorReveal? body;
    ToggleButton? header;
    RotateTransform? chevron;

    public InspectorExpander()
    {
        IsEnabledChanged += (_, _) => UpdateDisclosure(animate: false);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        body = GetTemplateChild("SectionBody") as InspectorReveal;
        header = GetTemplateChild("SectionHeader") as ToggleButton;
        header?.ApplyTemplate();
        var path = header?.Template?.FindName("Chevron", header) as System.Windows.Shapes.Path;
        chevron = path?.RenderTransform as RotateTransform;
        if (path is not null && chevron?.IsFrozen == true)
        {
            chevron = chevron.Clone();
            path.RenderTransform = chevron;
        }
        UpdateDisclosure(animate: false);
        ApplyProgress((double)GetValue(RevealProgressProperty));
    }

    protected override void OnExpanded()
    {
        base.OnExpanded();
        UpdateDisclosure(animate: true);
    }

    protected override void OnCollapsed()
    {
        base.OnCollapsed();
        UpdateDisclosure(animate: true);
    }

    void UpdateDisclosure(bool animate)
    {
        var show = IsExpanded && IsEnabled;
        if (body is not null)
        {
            if (!show && body.IsKeyboardFocusWithin)
                header?.Focus();
            body.IsHitTestVisible = show;
            body.IsEnabled = show;
        }

        var current = (double)GetValue(RevealProgressProperty);
        var target = show ? 1d : 0d;
        if (animate && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation && current != target)
        {
            BeginAnimation(RevealProgressProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop,
            });
            SetValue(RevealProgressProperty, target);
        }
        else
        {
            BeginAnimation(RevealProgressProperty, null);
            SetValue(RevealProgressProperty, target);
        }
    }

    void ApplyProgress(double progress)
    {
        if (chevron is not null)
            chevron.Angle = 90 * progress;
        body?.SetProgress(progress);
    }
}

public sealed class InspectorReveal : Decorator
{
    double progress;

    internal void SetProgress(double value)
    {
        if (progress == value)
            return;
        progress = value;
        Opacity = value;
        Visibility = value > 0 ? Visibility.Visible : Visibility.Collapsed;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
            return default;
        // Keep the controls at their natural height and reveal them inside a changing clip.
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * progress);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is not null)
            Child.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        return arrangeSize;
    }
}
