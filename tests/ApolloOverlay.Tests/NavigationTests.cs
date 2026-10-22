using System.Windows;
using ApolloOverlay;

internal static class NavigationTests
{
    private static void Require(bool condition){if(!condition)throw new Exception("Fluent navigation assertion failed.");}
    private static void Is(string expected,NavState state)=>Require(state.Id==expected);
    internal static void Run(Action<string,Action> test)
    {
        test("horizontal dpad and stick produce spatial moves",()=>{var p=new PadInterpreter();p.Update(new(true,0),0);Require(p.Update(new(true,PadButtons.Right),1).Direction==2);p.Update(new(true,0),2);Require(p.Update(new(true,0,-24000,0),3).Direction==-2);});
        test("stick diagonal uses dominant axis and vertical tie",()=>{var p=new PadInterpreter();p.Update(new(true,0),0);Require(p.Update(new(true,0,25000,21000),1).Direction==2);p.Update(new(true,0),2);Require(p.Update(new(true,0,24000,24000),3).Direction==-1);});
        test("horizontal repeat stays bounded and neutral resets it",()=>{var p=new PadInterpreter();p.Update(new(true,0),0);Require(p.Update(new(true,PadButtons.Left),1).Direction==-2);Require(p.Update(new(true,PadButtons.Left),200).Direction==0);Require(p.Update(new(true,PadButtons.Left),421).Direction==-2);p.Update(new(true,0),422);Require(p.Update(new(true,PadButtons.Left),423).Direction==-2);});
        test("held horizontal stick cannot arm after reconnect",()=>{var p=new PadInterpreter();p.Update(default,0);Require(p.Update(new(true,0,26000,0),1).Direction==0);p.Update(new(true,0),2);Require(p.Update(new(true,0,26000,0),3).Direction==2);});
        test("home grid edges never wrap rows",()=>{foreach(var row in new[]{("gamebar","steam-overlay"),("taskview","audio")}){Is(row.Item1,Navigation.Home(new(row.Item1),NavDirection.Left));Is(row.Item2,Navigation.Home(new(row.Item1),NavDirection.Right));Is(row.Item2,Navigation.Home(new(row.Item2),NavDirection.Right));Is(row.Item1,Navigation.Home(new(row.Item2),NavDirection.Left));}});
        test("grid up and down preserve column",()=>{Is("taskview",Navigation.Home(new("gamebar"),NavDirection.Down));Is("audio",Navigation.Home(new("steam-overlay",1),NavDirection.Down));Is("gamebar",Navigation.Home(new("taskview"),NavDirection.Up));Is("steam-overlay",Navigation.Home(new("audio",1),NavDirection.Up));});
        test("list enters remembered grid column",()=>{var state=Navigation.Home(new("steam-overlay",1),NavDirection.Up);Is("apps",state);Is("steam-overlay",Navigation.Home(state,NavDirection.Down));});
        test("close returns to remembered grid column",()=>{foreach(var pair in new[]{("taskview",0),("audio",1)}){var state=Navigation.Home(new(pair.Item1,pair.Item2),NavDirection.Down);Is("close",state);Is(pair.Item1,Navigation.Home(state,NavDirection.Up));Is("close",Navigation.Home(state,NavDirection.Left));}});
        test("home graph reaches every action including settings",()=>{var seen=new HashSet<string>();var queue=new Queue<NavState>();queue.Enqueue(new("return"));while(queue.Count>0){var state=queue.Dequeue();if(!seen.Add(state.Id))continue;foreach(var direction in new[]{NavDirection.Up,NavDirection.Down,NavDirection.Left,NavDirection.Right})queue.Enqueue(Navigation.Home(state,direction));}Require(Navigation.HomeIds.All(seen.Contains));});
        test("sequential fallback reaches unavailable tiles",()=>{var state=new NavState("return");var seen=new HashSet<string>();for(var i=0;i<Navigation.HomeIds.Length;i++){seen.Add(state.Id);state=Navigation.Sequential(state,Navigation.HomeIds,1);}Require(seen.Contains("audio") && seen.Contains("steam-overlay") && seen.Contains("settings"));});
        test("stable refresh preserves ID and nearby fallback",()=>{string[] before=["settings","a","b","c"];Require(Navigation.Preserve("b",before,["settings","b","a"])=="b");Require(Navigation.Preserve("b",before,["settings","a","c"])=="c");});
        test("apps pagination returns to first page without overlap",()=>{Require(Navigation.NextAppPage(0,7)==4);Require(Navigation.NextAppPage(4,7)==0);Require(Navigation.NextAppPage(0,0)==0);});
        test("unavailable busy and confirming actions cannot dispatch",()=>{Require(!Navigation.CanActivate(false,false,false));Require(!Navigation.CanActivate(true,false,true));Require(!Navigation.CanActivate(false,true,true));Require(Navigation.CanActivate(false,false,true));});
        test("sidebar physical placement leaves game visible at three scales",()=>{foreach(var d in new[]{new DisplayRef("720",0,0,1280,720,true),new("1080",-1920,0,1920,1080,false),new("4k",0,-2160,3840,2160,false)}){var b=SidebarLayout.Place(d);Require(b.X>=d.X && b.Y>=d.Y && b.X+b.Width<=d.X+d.Width && b.Y+b.Height<=d.Y+d.Height && b.Width<d.Width*.4);}var expected=SidebarLayout.Place(new("dpi",0,0,1920,1080,true));Require(expected==new SidebarBounds(36,27,660,1026));});
        var application=Application.Current ?? new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(application);
        test("native UI preserves app focus through refresh and tabs",()=>{var w=new MainWindow(renderOnly:true);w.Page(1);w.Move(NavDirection.Down);var selected=w.SelectedAction;Require(selected.StartsWith("window:"));w.RefreshRows();Require(w.SelectedAction==selected);w.Page(0);w.Page(1);Require(w.SelectedAction==selected);w.Close();});
        test("native UI spatial audio remains selectable and unavailable",()=>{var w=new MainWindow(renderOnly:true);w.Move(NavDirection.Down);w.Move(NavDirection.Down);w.Move(NavDirection.Down);w.Move(NavDirection.Down);w.Move(NavDirection.Right);w.Move(NavDirection.Down);Require(w.SelectedAction=="audio");w.ActivateEntry();Require(w.Feedback.Contains("indisponível"));w.Close();});
        test("native UI details confirmation and B cancellation stay reachable",()=>{var w=new MainWindow(renderOnly:true);w.Page(1);w.Details();Require(w.SelectedAction=="switch-detail");w.Move(NavDirection.Down);w.Move(NavDirection.Down);Require(w.SelectedAction=="close-detail");w.ActivateEntry();Require(w.Confirming);w.Back();Require(!w.Confirming);w.Close();});
        test("native UI system exit reachable and empty Apps recoverable",()=>{var w=new MainWindow(renderOnly:true);w.Page(2);for(var i=0;i<5;i++)w.Move(NavDirection.Down);Require(w.SelectedAction=="exit");w.SetRenderState("empty-apps");Require(w.ActionIds.Contains("apps-more"));w.Close();});
        test("native Fluent startup follows Windows app theme",()=>{ThemeSupport.FollowSystem(application);Require(ThemeSupport.FollowsSystem(application));});
        test("native Fluent refresh preserves existing focus session and hold",()=>{
            ThemeSupport.ForceForValidation(application,true);var w=new MainWindow(renderOnly:true);
            try {
                w.SetRenderState("confirmation");var selected=w.SelectedAction;var target=w.HeldCloseTarget!;var progress=w.CloseProgress;var lightSurface=w.SurfaceColor;var lightSelection=w.SelectionColor;
                ThemeSupport.ForceForValidation(application,false);w.UpdateLayout();Require(w.Confirming && w.HeldCloseTarget!.Same(target) && w.CloseProgress==progress && w.SelectedAction==selected);
                Require(SystemParameters.HighContrast || (w.SurfaceColor!=lightSurface && w.SelectionColor!=lightSelection));
                ThemeSupport.ForceForValidation(application,true);w.UpdateLayout();Require(w.SurfaceColor==lightSurface && w.SelectionColor==lightSelection && w.CloseProgress==progress && w.HeldCloseTarget!.Same(target) && w.SelectedAction==selected);
            } finally {w.Close();ThemeSupport.FollowSystem(application);}
            Require(ThemeSupport.FollowsSystem(application));
        });
        foreach(var light in new[]{false,true})test($"native confirmation fully occludes actions in {(light ? "Light" : "Dark")}",()=>{
            ThemeSupport.ForceForValidation(application,light);var window=new MainWindow(renderOnly:true);
            try {
                window.SetRenderState("confirmation");var dialog=(System.Windows.Controls.Border)window.FindName("ConfirmPanel");
                var surface=(System.Windows.Media.SolidColorBrush)dialog.Background;
                var coverage=surface.Color.A/255d*surface.Opacity*dialog.Opacity;
                if(coverage!=1)throw new Exception($"Dialog coverage is {coverage:P1}; underlying action rows remain visible.");
                Require(window.Confirming && window.CloseProgress>0 && window.HeldCloseTarget is not null);
            } finally {window.Close();ThemeSupport.FollowSystem(application);}
        });
    }
}
