using ApolloOverlay;
using ApolloOverlay.Automation;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

internal static class AutomationTests
{
    private static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    private static string Temp(){var root=Path.Combine(Path.GetTempPath(),"BezoroAutomationTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
    private static string Cli=>File.Exists(Path.Combine(AppContext.BaseDirectory,"ApolloOverlay.Cli.exe"))?Path.Combine(AppContext.BaseDirectory,"ApolloOverlay.Cli.exe"):Path.Combine(Environment.CurrentDirectory,"tools/ApolloOverlay.Cli/bin/Release/net10.0-windows/ApolloOverlay.Cli.exe");
    private static (int Exit,string Out,string Error) Command(params string[] arguments)
    {
        var start=new ProcessStartInfo(Cli){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};foreach(var argument in arguments)start.ArgumentList.Add(argument);using var process=Process.Start(start)??throw new Exception("CLI did not start");var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();Require(process.WaitForExit(15000),"CLI process deadline");return(process.ExitCode,output.GetAwaiter().GetResult(),error.GetAwaiter().GetResult());
    }
    internal static void Prerequisites(Action<string,Action> test)
    {
        test("automation harmless CLI startup and local identity prerequisites",()=>{var result=Command("--help");Require(result.Exit==0&&result.Out.Contains("gso version|history|update")&&result.Out.Contains("rollback BUILD_ID"),"CLI startup/dependency failed: "+result.Error);var peer=AutomationPipeSecurity.Current();Require(peer.Pid==Environment.ProcessId&&peer.Logon.StartsWith("S-1-5-5-",StringComparison.Ordinal),"Local logon identity unavailable");Task.Run(async()=>{var name="BezoroPrerequisite."+Guid.NewGuid().ToString("N");using var server=AutomationPipeSecurity.Create(name,false,true);using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);var accept=server.WaitForConnectionAsync();await client.ConnectAsync(1000);await accept;var locality=AutomationPipeSecurity.ClientLocalityError(server);Require(locality==229&&AutomationPipeSecurity.ClientAllowed(server),"Native local-channel prerequisite failed: "+locality);Console.WriteLine("NATIVE-PIPE-LOCALITY error="+locality+"; peer-auth=True");}).GetAwaiter().GetResult();var root=Temp();File.WriteAllText(Path.Combine(root,"ready.txt"),"ready");Console.WriteLine("AUTOMATION-PREREQUISITES: console host/dependencies/output/logon SID ready; no GUI or input mutation.");});
    }
    internal static void Run(Action<string,Action> test)
    {
        Prerequisites(test);
        test("corrupt automation ledger leaves overlay usable and evidence intact",()=>{
            var previous=SynchronizationContext.Current;
            try{
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            foreach(var invalid in new[]{"{", "{}", "{\"Records\":[null]}", "{\"Records\":[{}]}", new string(' ',1024*1024+1)}){
                var root=Temp();var ledgerRoot=Path.Combine(root,"automation",Process.GetCurrentProcess().SessionId.ToString());Directory.CreateDirectory(ledgerRoot);
                var path=Path.Combine(ledgerRoot,"automation-ledger.json");File.WriteAllText(path,invalid);
                var window=new MainWindow(renderOnly:true,platform:new PreviewPlatform(),diagnostics:new(root));
                try{
                    var startup=window.StartAutomation();var watch=Stopwatch.StartNew();
                    while(!startup.IsCompleted&&watch.ElapsedMilliseconds<5000){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);}
                    Require(startup.IsCompleted,"Startup deadline");startup.GetAwaiter().GetResult();
                    Require(window.Controller.Operation?.State=="Indisponível"&&File.ReadAllText(path)==invalid,"Corrupt history was erased or overlay startup failed");
                    var bridge=window.StartAutomation(grant:new AutomationGrant("fixed",false));watch.Restart();
                    while(!bridge.IsCompleted&&watch.ElapsedMilliseconds<5000){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);}
                    Require(bridge.IsCompleted,"Bridge startup deadline");var refused=false;try{bridge.GetAwaiter().GetResult();}catch(InvalidOperationException){refused=true;}
                    Require(refused&&File.ReadAllText(path)==invalid,"Explicit bridge failed open or changed evidence");
                }finally{window.Close();}
            }
            }finally{SynchronizationContext.SetSynchronizationContext(previous);}
        });
        test("automation ledger retained duplicates and altered payload reject",()=>{var ledger=new AutomationLedger(Temp());var id=ledger.Issue("one");Require(ledger.Accept(id,"one","payload")=="Accepted","Intent not accepted");Require(ledger.Accept(id,"one","payload")=="Duplicate","Duplicate redispatched");Require(ledger.Accept(id,"one","changed")=="Rejected"&&ledger.Accept(id,"other","payload")=="Rejected","Payload or connection transferred");});
        test("automation retired watermark rejects evicted result without effects",()=>{var ledger=new AutomationLedger(Temp());string first="";var effects=0;for(var i=0;i<257;i++){var id=ledger.Issue("c"+i);if(i==0)first=id;if(ledger.Accept(id,"c"+i,"same")=="Accepted")effects++;ledger.Update(id,AutomationReply.Error("Completed","",id));}Require(ledger.Find(first) is null&&ledger.Accept(first,"c0","same")=="Rejected"&&effects==257,"Evicted ID became executable");});
        test("automation expires and invalidates older unused tickets",()=>{long now=0;var ledger=new AutomationLedger(Temp(),()=>now);var old=ledger.Issue("old");var fresh=ledger.Issue("fresh");Require(ledger.Accept(fresh,"fresh","x")=="Accepted"&&ledger.Accept(old,"old","x")=="Rejected","Older unused ticket accepted");var expired=ledger.Issue("expired");now=10001;Require(ledger.Accept(expired,"expired","x")=="Rejected","Expired ticket accepted");ledger.Retire("fresh");Require(ledger.Accept(fresh,"fresh","x")=="Rejected","Disconnected ticket accepted");});
        foreach(var dispatched in new[]{false,true}){var sent=dispatched;test("automation restart pending intent recovers Unknown dispatched="+sent,()=>{var root=Temp();var first=new AutomationLedger(root);var id=first.Issue("c");Require(first.Accept(id,"c","effect")=="Accepted","Intent missing");var effects=sent?1:0;var recovered=new AutomationLedger(root);Require(recovered.Find(id)?.Reply is{Outcome:"Unknown",Dispatched:null}&&recovered.Accept(id,"c","effect")=="Rejected"&&effects==(sent?1:0),"Recovery redispatched or claimed no effects");});}
        test("automation intent write failure rejects before any effect",()=>{var root=Temp();var ledger=new AutomationLedger(root);Directory.CreateDirectory(Path.Combine(root,"automation-ledger.json.tmp"));var id=ledger.Issue("c");var failed=false;try{ledger.Accept(id,"c","effect");}catch(Exception e)when(e is IOException or UnauthorizedAccessException){failed=true;}Require(failed&&ledger.Find(id) is null,"Failed persistence accepted intent");});
        test("automation request framing rejects oversized and invalid prefix",()=>{foreach(var size in new[]{0,-1,AutomationFrames.RequestLimit+1}){using var stream=new MemoryStream(BitConverter.GetBytes(size));var rejected=false;try{AutomationFrames.Read(stream,AutomationFrames.RequestLimit,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException){rejected=true;}Require(rejected,"Bad frame accepted");}});
        test("automation peer user logon and session isolation",()=>{var p=AutomationPipeSecurity.Current();Require(!AutomationPipeSecurity.SameSession(p,p with{User="different"})&&!AutomationPipeSecurity.SameSession(p,p with{Logon="different"})&&!AutomationPipeSecurity.SameSession(p,p with{Session=p.Session+1}),"Peer boundary broadened");});
        test("automation locality accepts only explicit ERROR_PIPE_LOCAL",()=>{Require(AutomationPipeSecurity.LocalChannel(229),"Local pipe refused");foreach(var code in new[]{0,5,109,122,233,99999})Require(!AutomationPipeSecurity.LocalChannel(code),"Unknown/remote locality accepted");});
        test("automation owned pipe dry-run cancellation disconnect Busy and malformed input",()=>Task.Run(PipeChecks).GetAwaiter().GetResult());
        test("automation independent update jobs do not exhaust the request ledger",()=>Task.Run(UpdateAcceptanceLedger).GetAwaiter().GetResult());
        test("automation shutdown cancellation before Close terminalizes and remains evictable",()=>Task.Run(ShutdownCancellation).GetAwaiter().GetResult());
        test("automation real console CLI owned preview routes and observed exact shutdown",CliRoundtrip);
    }
    private static async Task<(NamedPipeClientStream Pipe,AutomationReply Hello)> Connect(string name)
    {
        var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);await pipe.ConnectAsync(2000);var hello=JsonSerializer.Deserialize(await AutomationFrames.Read(pipe,AutomationFrames.ResponseLimit,CancellationToken.None),AutomationJson.Default.AutomationReply)!;Require(AutomationPipeSecurity.ServerAllowed(pipe,hello),"Server identity not verified");return(pipe,hello);
    }
    internal static async Task<AutomationReply> Exchange(string name,AutomationRequest request)
    {
        var connection=await Connect(name);using var pipe=connection.Pipe;if(request.Verb is "execute" or "shutdown")request=request with{RequestId=connection.Hello.Ticket,DeadlineUtc=connection.Hello.ExpiresUtc};await AutomationFrames.Write(pipe,AutomationFrames.Encode(request),AutomationFrames.RequestLimit,CancellationToken.None);return JsonSerializer.Deserialize(await AutomationFrames.Read(pipe,AutomationFrames.ResponseLimit,CancellationToken.None),AutomationJson.Default.AutomationReply)!;
    }
    private static async Task PipeChecks()
    {
        var root=Temp();var name="BezoroFixture."+Guid.NewGuid().ToString("N");var backend=new Backend();using var host=new AutomationHost(backend,root,name);
        var dry=await Exchange(name,new(){Verb="execute",ActionId="effect",DryRun=true});Require(dry.Outcome=="DryRun"&&backend.Effects==0&&!File.Exists(Path.Combine(root,"automation-ledger.json")),"Dry run mutated/wrote intent");
        var preset=await Exchange(name,new(){Verb="execute",ActionId="steam-overlay",Preset="diagnostic-dwell"});Require(preset.Outcome=="Unsupported"&&backend.Effects==0,"Unsupported preset mutated");
        var connection=await Connect(name);var request=new AutomationRequest{Verb="execute",ActionId="wait",RequestId=connection.Hello.Ticket,DeadlineUtc=connection.Hello.ExpiresUtc};await AutomationFrames.Write(connection.Pipe,AutomationFrames.Encode(request),AutomationFrames.RequestLimit,CancellationToken.None);Require(backend.Entered.Wait(1000),"Owner did not enter");
        var busy=await Exchange(name,new(){Verb="execute",ActionId="effect"});Require(busy.Outcome=="Busy"&&backend.Effects==0,"Second mutation queued/executed");connection.Pipe.Dispose();Require(backend.Cancelled.Wait(2000),"Disconnect did not cancel owner");
        await Task.Delay(50);var result=await Exchange(name,new(){Verb="result",RequestId=request.RequestId});Require(result.Outcome=="Cancelled"&&backend.Effects==0,"Disconnected owner mutated");
        var malformed=await Connect(name);using(malformed.Pipe){await AutomationFrames.Write(malformed.Pipe,"{bad"u8.ToArray(),AutomationFrames.RequestLimit,CancellationToken.None);var buffer=new byte[1];Require(await malformed.Pipe.ReadAsync(buffer)==0,"Malformed request was not closed");}Require(backend.Effects==0,"Malformed request mutated");
        using var restricted=new AutomationHost(new Backend(),Temp(),name+".restricted",new("fixed",false));var denied=await Exchange(name+".restricted.bridge",new(){Verb="execute",ActionId="launch",TargetId="fixed"});Require(denied.Outcome=="Denied","Bridge granted launch");var other=await Exchange(name+".restricted.bridge",new(){Verb="execute",ActionId="focus",TargetId="other"});Require(other.Outcome=="Denied","Bridge target expanded");
        var ui=new Backend();using var uiHost=new AutomationHost(ui,Temp(),name+".ui",new("fixed",false));var refused=await Exchange(name+".ui.bridge",new(){Verb="cancel",RequestId="ui-other"});Require(refused.Outcome!="Cancelled"&&ui.UiCancelled==0,"Bridge cancelled outside its fixed target");var uiCancelled=await Exchange(name+".ui.bridge",new(){Verb="cancel",RequestId="ui-fixed"});Require(uiCancelled.Outcome=="Cancelled"&&ui.UiCancelled==1,"Shared UI owner cancellation did not reach granted target");
        foreach(var deadline in new[]{false,true}){
            var actor=new Backend();var endpoint=name+(deadline?".deadline":".cancel");using var controlled=new AutomationHost(actor,Temp(),endpoint);var pair=await Connect(endpoint);using var channel=pair.Pipe;var command=new AutomationRequest{Verb="execute",ActionId="wait",RequestId=pair.Hello.Ticket,DeadlineUtc=deadline?DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+500:pair.Hello.ExpiresUtc};await AutomationFrames.Write(channel,AutomationFrames.Encode(command),AutomationFrames.RequestLimit,CancellationToken.None);Require(actor.Entered.Wait(1000),"Controlled request never entered");if(!deadline){var cancelled=await Exchange(endpoint,new(){Verb="cancel",RequestId=command.RequestId});Require(cancelled.Outcome=="Cancelled","Explicit cancel not accepted");}Require(actor.Cancelled.Wait(2000)&&actor.Effects==0,"Deadline/cancel allowed late effects");
        }
    }
    private static async Task UpdateAcceptanceLedger()
    {
        var root=Temp();var name="BezoroUpdateLedger."+Guid.NewGuid().ToString("N");var backend=new Backend();
        using var host=new AutomationHost(backend,root,name);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string first="";
        for(var index=0;index<260;index++)
        {
            var pair=await Connect(name);using var pipe=pair.Pipe;
            var request=new AutomationRequest{Verb="update-check",RequestId=pair.Hello.Ticket,DeadlineUtc=pair.Hello.ExpiresUtc};
            if(index==0)first=request.RequestId;
            await AutomationFrames.Write(pipe,AutomationFrames.Encode(request),AutomationFrames.RequestLimit,deadline.Token);
            var reply=JsonSerializer.Deserialize(await AutomationFrames.Read(pipe,AutomationFrames.ResponseLimit,deadline.Token),AutomationJson.Default.AutomationReply)!;
            Require(reply is{Outcome:"Requested",Terminal:true,Update.Terminal:false}&&reply.Update.JobId==request.RequestId,"Job acceptance exhausted the ledger or claimed job completion at index "+index);
            var end=new byte[1];Require(await pipe.ReadAsync(end,deadline.Token)==0,"Completed request did not close its channel");
        }
        Require(backend.UpdateJobs==260,"An accepted job was lost or dispatched repeatedly");
        var effect=await Exchange(name,new(){Verb="execute",ActionId="effect"});
        Require(effect.Outcome=="Completed"&&backend.Effects==1,"Finished update requests blocked session actions");
        var ledger=new AutomationLedger(root);
        Require(ledger.Find(first) is null&&ledger.Accept(first,"replay","different")=="Rejected","Evicted update intent became replayable");
    }
    private sealed class Backend:IAutomationBackend
    {
        internal int Effects,UiCancelled,ShutdownCalls,UpdateJobs;internal readonly ManualResetEventSlim Entered=new(),Cancelled=new();
        public Task<AutomationReply> Update(AutomationRequest request,CancellationToken token,Func<bool> continuing)
        {
            if(request.DryRun)return Task.FromResult(AutomationReply.Error("DryRun",""));
            UpdateJobs++;
            return Task.FromResult(new AutomationReply{Outcome="Requested",Terminal=false,Update=new(){JobId=request.RequestId,Phase="Checking",Terminal=false}});
        }
        public Task<bool> Cancel(string id,string? grantedTarget,CancellationToken token){if(id is not("ui-fixed" or "ui-other")||grantedTarget is not null&&grantedTarget!=(id=="ui-fixed"?"fixed":"other"))return Task.FromResult(false);UiCancelled++;return Task.FromResult(true);}
        public Task<AutomationSnapshot> Snapshot(CancellationToken token,bool refreshTargets=false)=>Task.FromResult(new AutomationSnapshot("",0,0,0,0,"Fixture",false,false,[],[],"fixture"));
        public async Task<AutomationReply> Execute(AutomationRequest request,CancellationToken token,Action<AutomationReply> phase,Func<bool> continuing){if(request.DryRun)return AutomationReply.Error("DryRun","");if(request.ActionId=="wait"){Entered.Set();try{await Task.Delay(5000,token);}catch(OperationCanceledException){Cancelled.Set();return AutomationReply.Error("Cancelled","");}}token.ThrowIfCancellationRequested();if(!continuing())return AutomationReply.Error("Cancelled","");Effects++;return AutomationReply.Error("Completed","");}
        public Task Shutdown(CancellationToken token,Func<bool> continuing){ShutdownCalls++;return Task.CompletedTask;}
    }
    private static async Task ShutdownCancellation()
    {
        foreach(var explicitCancel in new[]{false,true}){
            var root=Temp();var name="BezoroShutdown."+Guid.NewGuid().ToString("N");var backend=new Backend();string id;
            using(var host=new AutomationHost(backend,root,name)){
                var pair=await Connect(name);using var pipe=pair.Pipe;id=pair.Hello.Ticket;await AutomationFrames.Write(pipe,AutomationFrames.Encode(new AutomationRequest{Verb="shutdown",RequestId=id,DeadlineUtc=pair.Hello.ExpiresUtc}),AutomationFrames.RequestLimit,CancellationToken.None);
                var ack=JsonSerializer.Deserialize(await AutomationFrames.Read(pipe,AutomationFrames.ResponseLimit,CancellationToken.None),AutomationJson.Default.AutomationReply)!;Require(ack.Outcome=="ShutdownRequested"&&!ack.Terminal,"Shutdown acknowledgement missing");
                if(explicitCancel)Require((await Exchange(name,new(){Verb="cancel",RequestId=id})) is{Outcome:"Cancelled",Terminal:false},"Shutdown cancellation incorrectly confirmed terminal cleanup");else pipe.Dispose();
                await Task.Delay(150);var result=await Exchange(name,new(){Verb="result",RequestId=id});Require(result is{Outcome:"Cancelled",Terminal:true}&&backend.ShutdownCalls==0,"Cancelled pre-commit shutdown remained pending or called Close");
            }
            var ledger=new AutomationLedger(root);for(var i=0;i<257;i++){var ticket=ledger.Issue("terminal"+i);Require(ledger.Accept(ticket,"terminal"+i,"effect")=="Accepted","Terminal ledger exhausted");ledger.Update(ticket,AutomationReply.Error("Completed","",ticket));}Require(ledger.Find(id) is null,"Cancelled terminal shutdown could not be evicted");
        }
    }
    private static void CliRoundtrip()
    {
        var root=Temp();var name="BezoroCliFixture."+Guid.NewGuid().ToString("N");var config=Path.Combine(root,"host.txt");var ready=Path.Combine(root,"ready");File.WriteAllLines(config,[name,root,ready]);
        var start=new ProcessStartInfo("C:/Program Files/dotnet/dotnet.exe"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"ApolloOverlay.Tests.dll"));start.ArgumentList.Add(config);start.ArgumentList.Add("--automation-host");using var server=Process.Start(start)??throw new Exception("Owned host not started");Console.WriteLine($"OWNED-CLI-HOST pid={server.Id}; started={server.StartTime.ToUniversalTime().Ticks}; endpoint={name}");var stdout=server.StandardOutput.ReadToEndAsync();var stderr=server.StandardError.ReadToEndAsync();
        try{
            var wait=Stopwatch.StartNew();while(!File.Exists(ready)&&!server.HasExited&&wait.ElapsedMilliseconds<5000)Thread.Sleep(10);Require(File.Exists(ready),"Owned host startup failed");
            AutomationReply Run(params string[] arguments){var response=Command([..arguments,"--pipe",name]);Require(response.Exit==0,"CLI failed: "+response.Out+response.Error);return JsonSerializer.Deserialize(response.Out,AutomationJson.Default.AutomationReply)!;}
            var status=Run("status");Require(status.Snapshot is{Pid:>0}&&status.Snapshot.Actions.Length>0,"Missing actual catalog");var target=Run("targets").Snapshot!.Targets.First().Id;
            foreach(var route in new[]{"home","apps","system","audio","integrations","options","details","customize","setup","result"}){Run("execute","route:"+route,"--target",target);var actions=Run("actions").Snapshot!.Actions;Require(actions.Length>0&&actions.All(a=>a.Capability is "Execute" or "RequiresInteractive" or "RequiresGesture"),"Route catalog incomplete");}
            Run("execute","route:options","--target",target);Run("execute","choose-session");var chooser=Run("actions").Snapshot!.Actions.First(a=>a.Id.StartsWith("window:",StringComparison.Ordinal));Require(chooser.Capability=="Execute","Session chooser not executable");Run("execute",chooser.Id);
            var before=Run("status").Snapshot!;var dry=Run("execute","focus","--target",target,"--dry-run");Require(dry.Outcome=="DryRun"&&Run("status").Snapshot!.Revision==before.Revision,"CLI dry-run changed UI");
            var stale=Command("execute","focus","--target",new string('f',32),"--pipe",name);Require(stale.Exit!=0&&JsonSerializer.Deserialize(stale.Out,AutomationJson.Default.AutomationReply)!.Outcome=="Rejected","Stale explicit target silently retargeted");
            var timer=Stopwatch.StartNew();var steam=Run("execute","steam-overlay","--target",target);Require(steam.Outcome=="Simulated"&&timer.ElapsedMilliseconds>=100,"Async handler completion inferred before observation");Require(Run("result",steam.RequestId).Outcome=="Simulated"&&Run("trace").Trace.Length>0,"Correlated result/trace missing");
            var shutdown=Run("shutdown");Require(shutdown.Outcome=="Completed"&&server.WaitForExit(3000),"Exact owned host shutdown not observed");
        }finally{
            if(!server.HasExited){server.StandardInput.WriteLine("shutdown");server.StandardInput.Flush();if(!server.WaitForExit(3000))throw new Exception($"Owned host {server.Id}/{server.StartTime.ToUniversalTime().Ticks} still running; no force kill performed.");}
            File.WriteAllText(Path.Combine(root,"host.stdout.log"),stdout.GetAwaiter().GetResult());File.WriteAllText(Path.Combine(root,"host.stderr.log"),stderr.GetAwaiter().GetResult());Console.WriteLine("OWNED-CLI-LOGS "+root);
        }
    }
    internal static int Host(string config)
    {
        var lines=File.ReadAllLines(config);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);var owned=new Window{Title="Owned CLI fixture target",Width=160,Height=100,Left=-12000,Top=-12000,ShowInTaskbar=false,ShowActivated=false};owned.Show();var target=new WindowRef(new WindowInteropHelper(owned).Handle,Environment.ProcessId,Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,"Owned fixture","fixture");var platform=new Preview(target);var window=new MainWindow(renderOnly:true,platform:platform,diagnostics:new(lines[1]));window.AutomationInputReader=()=>new(new PadFrame[4],Environment.TickCount64);app.MainWindow=window;window.Closed+=(_,_)=>{owned.Close();app.Shutdown();};app.Startup+=async(_,_)=>{try{await window.StartAutomation(lines[0]);File.WriteAllText(lines[2],"ready");_ = Task.Run(async()=>{if(await Console.In.ReadLineAsync()=="shutdown")await window.Dispatcher.InvokeAsync(window.ExitFromTray);});}catch(Exception e){Console.Error.WriteLine(e);app.Shutdown(1);}};return app.Run();
    }
    private sealed class Preview(WindowRef target):IPlatform
    {
        public bool IsPreview=>true;public WindowRef? Foreground()=>target;public IReadOnlyList<WindowRef> Windows()=>[target];public bool Valid(WindowRef w)=>target.Same(w);public IReadOnlyList<DisplayRef> Displays()=>[new("fixture",0,0,1280,720,true)];public ModuleEvidence SteamEvidence(WindowRef? w)=>ModuleEvidence.Loaded;
        public Task<ActionResult> Switch(WindowRef w)=>Task.FromResult(new ActionResult(ResultKind.Simulated,"Owned preview focus"));public Task<ActionResult> Shortcut(WindowRef w,string keys)=>Task.FromResult(new ActionResult(ResultKind.Simulated,"Owned preview shortcut"));public async Task<ActionResult> Shortcut(WindowRef w,string keys,CancellationToken token,Func<bool> continuing){await Task.Delay(120,token);return new(continuing()?ResultKind.Simulated:ResultKind.Cancelled,"Owned preview only; no input");}public Task<ActionResult> Close(WindowRef w,CancellationToken token)=>throw new Exception("No close target expected");public ActionResult Launch(bool playnite,string? path)=>throw new Exception("No launch expected");public void Dispose(){}
    }
}

