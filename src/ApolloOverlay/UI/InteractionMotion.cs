using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ApolloOverlay;
/// <summary>Selection clocks belong to visible controls; no timer or animation survives hiding.</summary>
public static class InteractionMotion
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false, SelectedChanged));
    private static readonly DependencyProperty TrackedProperty = DependencyProperty.RegisterAttached("Tracked", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false));
    private static readonly object PressKey = new();
    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
    internal static bool Allowed => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    private sealed class PressState(ScaleTransform scale)
    {
        internal readonly ScaleTransform Scale = scale;
        internal int Generation;
    }

    private static void SelectedChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is Button button && (bool)args.NewValue)
            Track(button);
    }

    private static void Track(Button button)
    {
        if ((bool)button.GetValue(TrackedProperty))
            return;
        button.SetValue(TrackedProperty, true);
        button.IsVisibleChanged += (_, _) =>
        {
            if (!button.IsVisible)
                Stop(button);
        };
        button.Unloaded += (_, _) => Stop(button);
    }

    internal static void Press(Button button)
    {
        if (!Allowed || !button.IsVisible)
            return;
        Track(button);
        if (button.Resources[PressKey] is not PressState state)
        {
            var scale = new ScaleTransform(1, 1);
            var transforms = new TransformGroup();
            if (button.RenderTransform is { } original && !original.Value.IsIdentity)
                transforms.Children.Add(original);
            transforms.Children.Add(scale);
            button.RenderTransform = transforms;
            button.RenderTransformOrigin = new(.5, .5);
            state = new(scale);
            button.Resources[PressKey] = state;
        }

        var generation = ++state.Generation;
        var duration = PresentationTokens.Duration(button, "SessionMotion.Press", 100).TimeSpan;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            FillBehavior = FillBehavior.Stop
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(state.Scale.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(.987, KeyTime.FromTimeSpan(TimeSpan.FromTicks((long)(duration.Ticks * .45))), new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(duration), new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.Completed += (_, _) =>
        {
            if (state.Generation == generation)
                StopPress(state);
        };
        Timeline.SetDesiredFrameRate(animation, 60);
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static void StopPress(PressState state)
    {
        state.Generation++;
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        state.Scale.ScaleX = state.Scale.ScaleY = 1;
    }

    internal static void Stop(Button button)
    {
        SetIsSelected(button, false);
        if (button.Resources[PressKey] is PressState state)
            StopPress(state);
    }
}
