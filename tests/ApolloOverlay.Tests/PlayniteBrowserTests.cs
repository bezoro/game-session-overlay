using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using ApolloOverlay;

internal static class PlayniteBrowserTests
{
    internal static void Run(Action<string,Action> test)
    {
        test("Playnite browser opens from settings and Back leaves the setting unchanged",()=>{
            var window=ReviewFixTests.Window();
            try{
                window.Page(2);var before=window.Controller.Settings.PlaynitePath;
                ReviewFixTests.Button(window,"configure-playnite").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if(!window.ActionIds.Contains("playnite-browser-back")||!window.ActionIds.Contains("playnite-native")||!window.ActionIds.Contains("playnite-loading"))throw new Exception("Controller browse route did not open.");
                window.Back();
                if(!window.ActionIds.Contains("configure-playnite")||window.Controller.Settings.PlaynitePath!=before)throw new Exception("Back did not restore settings without changing Playnite.");
            }finally{window.Close();}
        });
        test("Playnite browser lists local direct folders and detects its exact executable",()=>{
            var root=Path.Combine(Path.GetTempPath(),"ApolloOverlayBrowserTests",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root,"zeta","nested"));Directory.CreateDirectory(Path.Combine(root,"Alpha"));
            var executable=Path.Combine(root,"Playnite.FullscreenApp.exe");File.WriteAllText(executable,"");
            try{
                var result=MainWindow.ReadPlayniteFolder(root,CancellationToken.None);
                if(result.Error is not null||result.Executable!=executable||!result.Folders.Select(f=>f.Name).SequenceEqual(new[]{"Alpha","zeta"})||result.Truncated)throw new Exception("Unexpected direct folder listing or executable detection.");
                File.Delete(executable);File.WriteAllText(Path.Combine(root,"Playnite.DesktopApp.exe"),"");
                if(MainWindow.ReadPlayniteFolder(root,CancellationToken.None).Executable is not null)throw new Exception("Accepted a different executable.");
            }finally{Directory.Delete(root,true);}
        });
        test("Playnite browser reports missing folders without throwing",()=>{
            var root=Path.Combine(Path.GetTempPath(),"ApolloOverlayBrowserTests",Guid.NewGuid().ToString("N"));
            var result=MainWindow.ReadPlayniteFolder(root,CancellationToken.None);
            if(result.Error is null||result.Executable is not null||result.Folders.Length!=0)throw new Exception("Missing folder did not produce a recoverable error.");
        });
        test("Playnite browser respects cancellation before filesystem access",()=>{
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();var cancelled=false;
            try{MainWindow.ReadPlayniteFolder(null,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
            if(!cancelled)throw new Exception("Cancelled browse still proceeded.");
        });
    }
}
