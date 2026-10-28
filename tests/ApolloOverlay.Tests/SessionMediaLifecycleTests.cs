using System.Windows.Threading;
using ApolloOverlay;

internal static class SessionMediaLifecycleTests
{
    internal static void Run(Action<string,Action> test)
    {
        test("SMTC reopening retains a refresh until the previous generation drains",()=>
        {
            var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var old=new TaskCompletionSource<SessionMediaSnapshot>();var published=new TaskCompletionSource<bool>();var reads=0;var changes=0;
                using var media=new SessionMedia(_=>++reads==1?old.Task:Task.FromResult(Snapshot("current")));
                media.Changed+=state=>{changes++;if(state.Items.Single().Title=="current")published.TrySetResult(true);};
                media.SetVisible(true);media.SetVisible(false);media.SetVisible(true);
                for(var i=0;i<8;i++)ReviewFixTests.Complete(media.RefreshAsync());
                if(reads!=1)throw new Exception("Concurrent refresh escaped the owner gate");
                old.SetResult(Snapshot("stale"));ReviewFixTests.Complete(published.Task);
                if(reads!=2||changes!=1||media.Current.Items.Single().Title!="current")throw new Exception("Reopening lost or duplicated its pending refresh");
            }
            finally{SynchronizationContext.SetSynchronizationContext(previous);}
        });
        test("SMTC hiding and disposal discard late snapshots without rescheduling",()=>
        {
            var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                foreach(var dispose in new[]{false,true})
                {
                    var old=new TaskCompletionSource<SessionMediaSnapshot>();var reads=0;var changes=0;
                    using var media=new SessionMedia(_=>{reads++;return old.Task;});media.Changed+=_=>changes++;
                    media.SetVisible(true);ReviewFixTests.Complete(media.RefreshAsync());if(dispose)media.Dispose();else media.SetVisible(false);
                    old.SetResult(Snapshot("late"));ReviewFixTests.Complete(Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.Background).Task);
                    if(reads!=1||changes!=0||media.Current.Items.Count!=0)throw new Exception("Hidden media published a late snapshot or started another read");
                }
            }
            finally{SynchronizationContext.SetSynchronizationContext(previous);}
        });
    }
    private static SessionMediaSnapshot Snapshot(string title)=>new(1,[new("source",title,"","fixture",false,true,false,false)],"source","fixture");
}
