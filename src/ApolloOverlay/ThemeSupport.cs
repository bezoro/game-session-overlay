using System.Windows;

namespace ApolloOverlay;

internal static class ThemeSupport
{
    internal const string Surface="SolidBackgroundFillColorBaseBrush",Card="CardBackgroundFillColorDefaultBrush",Control="ControlFillColorDefaultBrush",Stroke="ControlStrokeColorDefaultBrush",Text="TextFillColorPrimaryBrush",SecondaryText="TextFillColorSecondaryBrush",DisabledText="TextFillColorDisabledBrush",Success="SystemFillColorSuccessBrush",SuccessBackground="SystemFillColorSuccessBackgroundBrush",Critical="SystemFillColorCriticalBrush",CriticalBackground="SystemFillColorCriticalBackgroundBrush";
    internal static void FollowSystem(Application application)
    {
#pragma warning disable WPF0001
        application.ThemeMode=ThemeMode.System;
#pragma warning restore WPF0001
    }
    // Internal render/test composition only. There is no runtime theme picker or persisted override.
    internal static void ForceForValidation(Application application,bool light)
    {
#pragma warning disable WPF0001
        application.ThemeMode=light ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
    }
    internal static bool FollowsSystem(Application application)
    {
#pragma warning disable WPF0001
        return application.ThemeMode==ThemeMode.System;
#pragma warning restore WPF0001
    }
}
