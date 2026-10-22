using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ApolloOverlay;

internal static unsafe class GuideAbiTests
{
    // Synthetic native vtable follows the retained official header, not a COM marshalling/reflection stub.
    private static readonly List<string> Calls=[];
    private static delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void> callback;
    private static nint context;
    private static bool registration=true,removal=true;
    private static uint policy;
    private static void Require(bool value) {if(!value)throw new Exception("Native GameInput ABI/lifetime assertion failed: "+string.Join(',',Calls));}
    private static (nint Table,nint Instance) Allocate()
    {
        Calls.Clear();callback=null;context=0;registration=true;removal=true;policy=0;
        var table=(nint*)NativeMemory.AllocZeroed(17,(nuint)sizeof(nint));
        table[2]=(nint)(delegate* unmanaged[Stdcall]<nint,uint>)&Release;
        table[9]=(nint)(delegate* unmanaged[Stdcall]<nint,nint,uint,nint,delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void>,ulong*,int>)&Register;
        table[11]=(nint)(delegate* unmanaged[Stdcall]<nint,ulong,void>)&Stop;
        table[12]=(nint)(delegate* unmanaged[Stdcall]<nint,ulong,byte>)&Unregister;
        table[16]=(nint)(delegate* unmanaged[Stdcall]<nint,uint,void>)&Policy;
        var instance=(nint*)NativeMemory.Alloc((nuint)sizeof(nint));*instance=(nint)table;
        return((nint)table,(nint)instance);
    }
    private static void Free((nint Table,nint Instance) fixture) {NativeMemory.Free((void*)fixture.Instance);NativeMemory.Free((void*)fixture.Table);}
    internal static void Success()
    {
        var fixture=Allocate();var backend=new GameInputBackend(fixture.Instance);var count=0;
        using(var connection=new GuideConnection(backend)) {
            Require(connection.Start(()=>count++));Require(policy==0x80 && context!=0 && callback!=null);
            callback(123,context,0,999,1,0);callback(123,context,0,1000,1,1);callback(123,context,0,1001,0,1);
            Require(count==1 && connection.Observed);
        }
        Require(string.Join(',',Calls)=="policy,register,stop,unregister,release");Free(fixture);
    }
    internal static void RegistrationFailure()
    {
        var fixture=Allocate();registration=false;
        using(var connection=new GuideConnection(new GameInputBackend(fixture.Instance))) Require(!connection.Start(()=>{}));
        Require(string.Join(',',Calls)=="policy,register,release");Free(fixture);
    }
    internal static void UnregisterFailure()
    {
        var fixture=Allocate();removal=false;var backend=new GameInputBackend(fixture.Instance);var count=0;
        var connection=new GuideConnection(backend);Require(connection.Start(()=>count++));connection.Dispose();
        callback(123,context,0,1001,1,0);Require(count==0);Require(!Calls.Contains("release"));
        removal=true;Require(backend.Unregister());backend.Release();Free(fixture);
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static uint Release(nint self) {Calls.Add("release");return 0;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static void Policy(nint self,uint value) {policy=value;Calls.Add("policy");}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int Register(nint self,nint device,uint filter,nint ctx,delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void> cb,ulong* token)
    {
        Calls.Add("register");if(device!=0 || filter!=1)return unchecked((int)0x80070057);
        context=ctx;callback=cb;*token=registration ? 123UL : 0UL;return registration ? 0 : unchecked((int)0x80004005);
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static void Stop(nint self,ulong token) {Calls.Add(token==123 ? "stop" : "bad-token");}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static byte Unregister(nint self,ulong token) {Calls.Add(token==123 ? "unregister" : "bad-token");return removal ? (byte)1 : (byte)0;}
}
