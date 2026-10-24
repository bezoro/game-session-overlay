using System.Windows;
using System.Windows.Media;

namespace ApolloOverlay;

internal static class ThemeSupport
{
    internal const string Surface="ApplicationBackgroundBrush",Card="CardBackgroundFillColorDefaultBrush",Control="ControlFillColorDefaultBrush",Stroke="ControlStrokeColorDefaultBrush",Text="TextFillColorPrimaryBrush",SecondaryText="TextFillColorSecondaryBrush",DisabledText="TextFillColorDisabledBrush",Success="SystemFillColorSuccessBrush",SuccessBackground="SystemFillColorSuccessBackgroundBrush",Critical="SystemFillColorCriticalBrush",CriticalBackground="SystemFillColorCriticalBackgroundBrush";
    private static ResourceDictionary? validation;
    private static ResourceDictionary? contrast;
    internal static void ApplySystemPalette(Application? application)
    {
        if(application is null)return;
        if(contrast is not null){application.Resources.MergedDictionaries.Remove(contrast);contrast=null;}
        if(!SystemParameters.HighContrast)return;
        contrast=new ResourceDictionary();
        foreach(var key in new[]{Surface,Card,Control,SuccessBackground,CriticalBackground})contrast[key]=SystemColors.WindowBrush;
        foreach(var key in new[]{Text,SecondaryText,Stroke})contrast[key]=SystemColors.WindowTextBrush;
        contrast[DisabledText]=SystemColors.GrayTextBrush;contrast[Success]=SystemColors.HighlightBrush;contrast[Critical]=SystemColors.WindowTextBrush;
        application.Resources.MergedDictionaries.Add(contrast);
    }
    internal static void FollowSystem(Application application)
    {
        if(validation is not null){application.Resources.MergedDictionaries.Remove(validation);validation=null;}
        if(!application.Resources.MergedDictionaries.Any(d=>d.Source?.ToString()=="pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml"))application.Resources.MergedDictionaries.Add(new(){Source=new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml",UriKind.Absolute)});
    }
    internal static void ForceForValidation(Application application,bool light)
    {
        FollowSystem(application);validation=new ResourceDictionary();
        foreach(var pair in new[]{(Surface,light?"#FFF5F5F5":"#FF181B20"),(Card,light?"#FFE5E8E8":"#FF252B31"),(Control,light?"#FFE8EBE9":"#FF2B3238"),(Stroke,light?"#FFBAC3BE":"#FF414A50"),(Text,light?"#FF17211D":"#FFF4F6F5"),(SecondaryText,light?"#FF526158":"#FFB5C0BA"),(DisabledText,light?"#FF6F7972":"#FF87928C"),(Success,light?"#FF117F25":"#FF58E96B"),(SuccessBackground,light?"#FFDCF3DE":"#FF273E2B"),(Critical,light?"#FFB32430":"#FFFF777B"),(CriticalBackground,light?"#FFFBE2E3":"#FF472B30")})validation[pair.Item1]=(SolidColorBrush)new BrushConverter().ConvertFromString(pair.Item2)!;
        application.Resources.MergedDictionaries.Add(validation);
    }
    internal static bool FollowsSystem(Application application)
    {
        return validation is null;
    }
}
