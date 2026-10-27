using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ApolloOverlay;

internal static class PlayniteBrowserTests
{
    internal static void Run(Action<string,Action> test)
    {
        test("Playnite browser page navigation selects the first folder on the requested page",()=>{
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var root=Path.Combine(Path.GetTempPath(),"ApolloOverlayBrowserTests",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            for(var index=0;index<25;index++)Directory.CreateDirectory(Path.Combine(root,$"Folder{index:00}"));
            var window=ReviewFixTests.Window();
            try{
                window.Left=-12000;window.Top=-12000;window.ShowDiagnostic();window.Page(2);
                var open=typeof(MainWindow).GetMethod("OpenView",BindingFlags.NonPublic|BindingFlags.Instance)??throw new Exception("Browser route not available.");
                open.Invoke(window,[new OverlayViewFrame(OverlayRoute.PlayniteBrowser,SelectedId:"playnite-loading",BrowserFolder:root)]);
                var deadline=System.Diagnostics.Stopwatch.StartNew();
                while(!window.ActionIds.Contains("playnite-page-next")&&deadline.ElapsedMilliseconds<5000){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>frame.Continue=false);Dispatcher.PushFrame(frame);Thread.Sleep(1);}
                if(!window.ActionIds.Contains("playnite-page-next"))throw new Exception("Folder results did not arrive.");
                ReviewFixTests.Button(window,"playnite-page-next").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if(window.SelectedAction!="playnite-folder:20"||!window.ActionIds.Contains("playnite-page-previous"))throw new Exception("Next page did not select its first folder.");
                ReviewFixTests.Button(window,"playnite-page-previous").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if(window.SelectedAction!="playnite-folder:0")throw new Exception("Previous page did not select its first folder.");
            }finally{window.Close();Directory.Delete(root,true);}
        });
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
