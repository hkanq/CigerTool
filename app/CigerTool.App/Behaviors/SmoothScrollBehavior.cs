using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace CigerTool.App.Behaviors;

public static class SmoothScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static double GetAnimatedVerticalOffset(DependencyObject obj) => (double)obj.GetValue(AnimatedVerticalOffsetProperty);

    private static void SetAnimatedVerticalOffset(DependencyObject obj, double value) => obj.SetValue(AnimatedVerticalOffsetProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            scrollViewer.Unloaded += OnUnloaded;
        }
        else
        {
            scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            scrollViewer.Unloaded -= OnUnloaded;
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.BeginAnimation(AnimatedVerticalOffsetProperty, null);
        scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        scrollViewer.Unloaded -= OnUnloaded;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        e.Handled = true;
        var currentOffset = scrollViewer.VerticalOffset;
        var targetOffset = Math.Clamp(currentOffset - (e.Delta * 0.45), 0, scrollViewer.ScrollableHeight);
        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = targetOffset,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        scrollViewer.BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }
}
