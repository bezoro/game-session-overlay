using System.IO;
using System.Text.Json;
using ApolloOverlay.Automation;

internal static class UpdateCommandTests
{
    private static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    internal static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("CLI version reports the fresh server build without starting a job",async()=>
        {
            var server=new Server();var reply=await server.Run("version");
            Require(reply.Outcome=="Completed"&&reply.Update?.BuildId=="current"&&server.Requests.Count==1,"Version did not come from the server or started work");
            var encoded=JsonSerializer.Serialize(reply,AutomationJson.Default.AutomationReply);
            Require(JsonSerializer.Deserialize(encoded,AutomationJson.Default.AutomationReply)?.Update?.Version==server.State.Version,"Version receipt did not survive protocol serialization");
        });
        await test("CLI history is a read-only request",async()=>{var server=new Server();var reply=await server.Run("history");Require(reply.Update?.History.Single().BuildId=="older"&&server.Requests.All(r=>r.Verb=="update-status"),"History started a network or native operation");});
        await test("CLI update check respects an explicit channel without downloading",async()=>
        {
            var server=new Server();var reply=await server.Run("update","check","--channel","stable");
            Require(reply is{Outcome:"Completed",Update.Phase:"Catalog"}&&server.Requests.Single(r=>r.Verb=="update-check").Channel=="stable"&&!server.Requests.Any(r=>r.Verb is "update-download" or "update-apply"),"Check applied or ignored its channel");
        });
        await test("CLI update prepares once and returns Requested instead of claiming the new version runs",async()=>
        {
            var server=new Server();var reply=await server.Run("update");
            Require(reply is{Outcome:"Requested",Update.TargetBuildId:"next"}&&reply.Update.BuildId=="current","Restart request was falsely reported as the new installation");
            Require(server.Requests.Count(r=>r.Verb=="update-check")==1&&server.Requests.Count(r=>r.Verb=="update-download")==1&&server.Requests.Count(r=>r.Verb=="update-apply")==1,"Mutation was repeated");
            Require(!server.Requests.Single(r=>r.Verb=="update-download").AllowDowngrade,"Automatic latest selection allowed a downgrade");
        });
        await test("CLI current build does not download or restart",async()=>{var server=new Server{AlreadyCurrent=true};var reply=await server.Run("update");Require(reply.Outcome=="Completed"&&!server.Requests.Any(r=>r.Verb is "update-download" or "update-apply"),"Current build was reinstalled");});
        await test("CLI dirty settings and missing installation block updates before network work",async()=>
        {
            foreach(var dirty in new[]{true,false}){var server=new Server();server.State=server.State with{SettingsDirty=dirty,Installed=dirty};var reply=await server.Run("update");Require(reply.Outcome=="Rejected"&&server.Requests.Count==1,"Dirty or portable installation was mutated");}
        });
        await test("CLI cached snapshots cannot authorize mutation",async()=>{var server=new Server{SnapshotOnly=true};var reply=await server.Run("update");Require(reply.Outcome=="Rejected"&&server.Requests.Count==1,"Stale snapshot authorized an update");});
        await test("CLI changed instance or replaced job prevents application",async()=>
        {
            foreach(var instance in new[]{true,false}){var server=new Server{ChangeInstance=instance,ReplaceJob=!instance};var reply=await server.Run("update");Require(reply.Outcome=="Unknown"&&!server.Requests.Any(r=>r.Verb=="update-apply"),"An unrelated job or instance was applied");}
        });
        await test("CLI failed verification cannot request installation",async()=>{var server=new Server{FailDownload=true};var reply=await server.Run("update");Require(reply.Outcome=="Failed"&&!server.Requests.Any(r=>r.Verb=="update-apply"),"Failed download was applied");});
        await test("CLI preparation cancellation cancels only its exact job and never applies",async()=>
        {
            var server=new Server();using var cancellation=new CancellationTokenSource();
            var reply=await UpdateCommands.Execute(["update"],server.Send,cancellation.Token,"fixture",token=>{cancellation.Cancel();return Task.FromCanceled(token);});
            Require(reply.Outcome=="Unknown"&&server.Requests.Count(r=>r.Verb=="update-cancel")==1&&server.Requests.Single(r=>r.Verb=="update-cancel").JobId=="job-1"&&!server.Requests.Any(r=>r.Verb=="update-apply"),"Cancellation touched another job or committed installation");
        });
        await test("CLI uncertain apply is not automatically resent or cancelled",async()=>
        {
            foreach(var failure in new Exception[]{new IOException("Disconnected"),new JsonException("Malformed reply"),new InvalidDataException("Invalid frame")})
            {
                var server=new Server{ApplyFailure=failure};var reply=await server.Run("update");
                Require(reply.Outcome=="Unknown"&&server.Requests.Count(r=>r.Verb=="update-apply")==1&&!server.Requests.Any(r=>r.Verb=="update-cancel"),"An uncertain apply was retried or claimed cancelled");
            }
        });
        await test("CLI explicit rollback uses local history without a catalog query",async()=>
        {
            var server=new Server();var reply=await server.Run("rollback","older");
            Require(reply is{Outcome:"Requested",Update.TargetBuildId:"older"}&&server.Requests.Single(r=>r.Verb=="update-rollback").BuildId=="older"&&!server.Requests.Any(r=>r.Verb=="update-check"),"Rollback queried the network or changed the target");
        });
        await test("CLI explicit build selection alone enables downgrade",async()=>
        {
            var server=new Server();await server.Run("update","install","--build","next");
            Require(server.Requests.Single(r=>r.Verb=="update-download").AllowDowngrade,"Explicit version selection lost its intent");
        });
        await test("CLI dry-run performs only server validation",async()=>
        {
            var server=new Server();var reply=await server.Run("update","--dry-run");
            Require(reply.Outcome=="DryRun"&&server.Requests.Where(r=>r.Verb!="update-status").All(r=>r.DryRun)&&!server.Requests.Any(r=>r.Verb is "update-download" or "update-apply"),"Dry-run downloaded or committed");
        });
        await test("CLI invalid update arguments are rejected without connecting",async()=>
        {
            foreach(var args in new[]{new[]{"update","--url","https://example.invalid/app.exe"},["update","--pipe","\\\\remote\\pipe"],["rollback","../outside"],["update","--channel","other"],["update","--timeout","1"],["update","--build",""],["update","--channel","stable","--channel","testing"]})
            {var server=new Server();var reply=await server.Run(args);Require(reply.Outcome=="Rejected"&&server.Requests.Count==0,"Invalid argument contacted an endpoint");}
        });
    }

    private sealed class Server
    {
        internal readonly List<AutomationRequest> Requests=[];
        internal AutomationUpdateState State=new(){Version="0.2.0-preview.1.1+current",BuildId="current",Commit=new string('a',40),Channel="testing",SelectedChannel="testing",Installed=true,History=[new("older","0.1.0",new string('b',40),"testing")]};
        internal bool AlreadyCurrent,SnapshotOnly,ChangeInstance,ReplaceJob,FailDownload;
        internal Exception? ApplyFailure;
        private int issued;
        internal Task<AutomationReply> Run(params string[] args)=>UpdateCommands.Execute(args,Send,CancellationToken.None,"fixture",_=>Task.CompletedTask);
        internal Task<AutomationReply> Send(string pipe,AutomationRequest request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Requests.Add(request);
            AutomationReply Reply(string outcome="Completed")=>new(){Outcome=outcome,Update=State,Instance=ChangeInstance&&issued>0?"different":"same"};
            if(request.Verb=="update-status")return Task.FromResult(ReplaceJob&&issued>0?Reply() with{Update=State with{JobId="unrelated"}}:Reply(SnapshotOnly?"Snapshot":"Completed"));
            if(request.DryRun)return Task.FromResult(Reply("DryRun"));
            if(request.Verb=="update-cancel"){State=State with{Phase="Cancelled",Terminal=true};return Task.FromResult(Reply("Cancelled"));}
            if(request.Verb=="update-apply")
            {
                if(ApplyFailure is {} failure)throw failure;
                State=State with{Phase="Restarting",Terminal=false};return Task.FromResult(Reply("Requested"));
            }
            var id="job-"+(++issued);
            if(request.Verb=="update-check")State=State with{JobId=id,Phase="Catalog",Terminal=true,Available=[new(AlreadyCurrent?"current":"next","0.2.0-preview.2.1+next",new string('c',40),request.Channel,1000)]};
            else State=State with{JobId=id,Phase=FailDownload?"Failed":"Ready",Terminal=true,TargetBuildId=request.BuildId,TargetVersion=request.BuildId=="older"?"0.1.0":"0.2.0-preview.2.1+next",Message=FailDownload?"Verification failed":"Ready"};
            return Task.FromResult(Reply("Requested") with{Update=State with{Terminal=false}});
        }
    }
}
