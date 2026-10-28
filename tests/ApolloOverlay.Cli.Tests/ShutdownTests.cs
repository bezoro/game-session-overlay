using ApolloOverlay.Automation;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

internal static class ShutdownTests
{
    private static readonly AutomationReply Ack=new(){Outcome="ShutdownRequested",Terminal=false,RequestId="fixed-request"};
    private static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    public static async Task<int> Main(string[] args)
    {
        if(args is ["--owned-child"]){await Console.In.ReadLineAsync();return 0;}
        if(args is ["--owned-server",var endpoint])return await Server(endpoint);
        if(args is ["--roundtrip",var cli])return await Roundtrip(cli);
        var failures=0;var total=0;
        async Task Test(string name,Func<Task> body){total++;try{await body();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.Error.WriteLine("FAIL "+name+": "+e.GetType().Name+" "+e.Message);}}
        if(args is ["--update-tests"]){await UpdateCommandTests.Run(Test);Console.WriteLine($"TOTAL {total-failures}/{total}");return failures==0?0:1;}
        await Test("access denied observing acknowledged shutdown is Unknown without crash or retry",async()=>{
            var calls=0;var result=await Program.ObserveShutdown(Ack,_=>{calls++;throw new Win32Exception(5);},CancellationToken.None);
            Require(result is{Outcome:"Unknown",Terminal:true,RequestId:"fixed-request"}&&calls==1,"Access failure was retried or claimed completion");
        });
        await Test("cancelled observation never claims completion",async()=>{
            using var cancel=new CancellationTokenSource();cancel.Cancel();var result=await Program.ObserveShutdown(Ack,t=>Task.FromCanceled(t),cancel.Token);
            Require(result.Outcome=="Unknown","Cancellation claimed exit");
        });
        await Test("confirmed observation preserves request and completes",async()=>{
            var result=await Program.ObserveShutdown(Ack,_=>Task.CompletedTask,CancellationToken.None);
            Require(result is{Outcome:"Completed",Terminal:true,RequestId:"fixed-request"},"Confirmed exit missing");
        });
        await Test("limited-query observation waits through active then confirms exit",async()=>{
            var calls=0;await ShutdownProcess.WaitForExit(()=>++calls<3?259u:0u,CancellationToken.None);Require(calls==3,"Exited before confirmed exit code");
        });
        await Test("query access denial is Unknown without repeat or false completion",async()=>{
            var calls=0;var result=await Program.ObserveShutdown(Ack,t=>ShutdownProcess.WaitForExit(()=>{calls++;throw new Win32Exception(5);},t),CancellationToken.None);
            Require(result.Outcome=="Unknown"&&calls==1,"Access denied query retried or completed");
        });
        await Test("deadline on STILL_ACTIVE remains Unknown",async()=>{
            using var deadline=new CancellationTokenSource(80);var result=await Program.ObserveShutdown(Ack,t=>ShutdownProcess.WaitForExit(()=>259,t),deadline.Token);
            Require(result.Outcome=="Unknown","Active or exit-259 process claimed terminated");
        });
        await Test("cancel before observation performs zero native queries",async()=>{
            using var cancel=new CancellationTokenSource();cancel.Cancel();var calls=0;var result=await Program.ObserveShutdown(Ack,t=>ShutdownProcess.WaitForExit(()=>{calls++;return 0;},t),cancel.Token);
            Require(result.Outcome=="Unknown"&&calls==0,"Queried after cancellation");
        });
        await Test("cancel racing exit observation remains Unknown",async()=>{
            using var cancel=new CancellationTokenSource();var result=await Program.ObserveShutdown(Ack,t=>ShutdownProcess.WaitForExit(()=>{cancel.Cancel();return 0;},t),cancel.Token);
            Require(result.Outcome=="Unknown","Concurrent cancellation reported Completed");
        });
        await Test("same PID with different creation identity is refused before observation",()=>{
            using var process=Process.GetCurrentProcess();var refused=false;try{using var wrong=ShutdownProcess.Open(process.Id,process.StartTime.ToUniversalTime().Ticks+1);}catch(System.IO.InvalidDataException){refused=true;}
            Require(refused,"Reused PID/changed start was accepted");return Task.CompletedTask;
        });
        await Test("query-only retained handle confirms exact owned child exit",async()=>{
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};start.ArgumentList.Add("--owned-child");
            using var child=Process.Start(start)??throw new Exception("Owned child not started");var ticks=child.StartTime.ToUniversalTime().Ticks;var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
            try{
                using var observation=ShutdownProcess.Open(child.Id,ticks);using var deadline=new CancellationTokenSource(3000);
                var waiting=Program.ObserveShutdown(Ack,observation.WaitForExit,deadline.Token);Require(!waiting.IsCompleted,"Running child claimed exited");
                await child.StandardInput.WriteLineAsync("exit");await child.StandardInput.FlushAsync();var result=await waiting;
                Require(result.Outcome=="Completed","Query-only native handle failed to observe exit");Console.WriteLine($"OWNED-EXIT pid={child.Id}; started={ticks}; outcome={result.Outcome}; stdout={await output}; stderr={await error}");
            }finally{if(!child.HasExited){await child.StandardInput.WriteLineAsync("exit");await child.StandardInput.FlushAsync();using var cleanup=new CancellationTokenSource(3000);await child.WaitForExitAsync(cleanup.Token);}}
        });
        await UpdateCommandTests.Run(Test);
        Console.WriteLine($"TOTAL {total-failures}/{total}");return failures==0?0:1;
    }
    private static async Task<int> Server(string name)
    {
        using var deadline=new CancellationTokenSource(5000);var token=deadline.Token;
        using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        Console.WriteLine("READY");await Console.Out.FlushAsync();await pipe.WaitForConnectionAsync(token);
        using var self=Process.GetCurrentProcess();var hello=new AutomationReply{Outcome="Handshake",Pid=self.Id,Started=self.StartTime.ToUniversalTime().Ticks,Ticket="owned-shutdown",ExpiresUtc=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+3000};
        await AutomationFrames.Write(pipe,AutomationFrames.Encode(hello),AutomationFrames.ResponseLimit,token);
        var request=JsonSerializer.Deserialize(await AutomationFrames.Read(pipe,AutomationFrames.RequestLimit,token),AutomationJson.Default.AutomationRequest)!;
        Require(request.Verb=="shutdown"&&request.RequestId==hello.Ticket,"Unexpected owned request");
        await AutomationFrames.Write(pipe,AutomationFrames.Encode(Ack with{RequestId=request.RequestId}),AutomationFrames.ResponseLimit,token);
        await Task.Delay(100,token);Require(AutomationPipeSecurity.Alive(pipe),"CLI disconnected before shutdown commit");
        Console.WriteLine("OWNED-COMMIT pipe-alive=True");return 0;
    }
    private static async Task<int> Roundtrip(string cli)
    {
        var endpoint="Bezoro.CliShutdown."+Guid.NewGuid().ToString("N");
        var hostStart=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};hostStart.ArgumentList.Add("--owned-server");hostStart.ArgumentList.Add(endpoint);
        using var host=Process.Start(hostStart)??throw new Exception("Owned server not started");using var deadline=new CancellationTokenSource(8000);
        Require(await host.StandardOutput.ReadLineAsync(deadline.Token)=="READY","Owned server prerequisite failed");
        var hostOutput=host.StandardOutput.ReadToEndAsync();var hostError=host.StandardError.ReadToEndAsync();
        var start=new ProcessStartInfo(cli){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};start.ArgumentList.Add("shutdown");start.ArgumentList.Add("--pipe");start.ArgumentList.Add(endpoint);
        using var client=Process.Start(start)??throw new Exception("CLI not started");var output=client.StandardOutput.ReadToEndAsync();var error=client.StandardError.ReadToEndAsync();
        await client.WaitForExitAsync(deadline.Token);await host.WaitForExitAsync(deadline.Token);var json=await output;var stderr=await error;var observed=JsonSerializer.Deserialize(json,AutomationJson.Default.AutomationReply)!;
        Require(client.ExitCode==0&&host.ExitCode==0&&observed is{Outcome:"Completed",Terminal:true,RequestId:"owned-shutdown"}&&stderr.Length==0,"Exact CLI shutdown roundtrip failed: "+json+stderr+await hostError);
        Console.WriteLine($"PASS CLI shutdown: exact owned PID={host.Id}; exited=True; clientExit={client.ExitCode}; reply={json.Trim()}; host={await hostOutput}");return 0;
    }
}
