using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ApolloOverlay;

/// <summary>All RCWs and registrations belong to the creating MTA worker. Callbacks retain raw references only until that owner drains them.</summary>
internal sealed class CoreAudioSessionCatalog : IApplicationAudioCatalog
{
    private sealed class Endpoint : IDisposable
    {
        public required string Id;
        public required IMMDevice Device;
        public required IHubAudioSessionManager2 Manager;
        public required HubSessionCreated Callback;
        public bool Registered;
        public readonly Dictionary<string,Session> Sessions=new(StringComparer.Ordinal);
        public void Dispose()
        {
            Callback.Stop();
            if(Registered)try{Manager.UnregisterSessionNotification(Callback);}catch(COMException) { }
            foreach(var session in Sessions.Values)session.Dispose();Sessions.Clear();
            Callback.ReleasePending();
            Release(Manager);Release(Device);
        }
    }
    private sealed class Session : IApplicationAudioSession,IDisposable
    {
        private readonly IHubAudioSessionControl2 control;
        private readonly IHubSimpleAudioVolume volume;
        private readonly HubSessionEvents callback;
        private readonly IHubAudioSessionControl acquired;
        private bool registered;
        public string Key {get;}
        public Session(string key,IHubAudioSessionControl acquired,IHubAudioSessionControl2 control,IHubSimpleAudioVolume volume,Action changed)
        {
            Key=key;this.acquired=acquired;this.control=control;this.volume=volume;callback=new(changed);
            Check(control.RegisterAudioSessionNotification(callback));registered=true;
        }
        public int ProcessId(out uint pid)=>control.GetProcessId(out pid);
        public int State(out int state)=>control.GetState(out state);
        public int ReadMute(out bool muted)=>volume.GetMute(out muted);
        public int WriteMute(bool muted){var context=Guid.Empty;return volume.SetMute(muted,ref context);}
        public void Dispose(){callback.Stop();if(registered)try{control.UnregisterAudioSessionNotification(callback);}catch(COMException) { }registered=false;Release(acquired);}
    }
    private readonly IHubDeviceEnumerator enumerator;
    private readonly HubEndpointEvents endpointEvents;
    private readonly Dictionary<string,Endpoint> endpoints=new(StringComparer.Ordinal);
    private readonly Action changed;
    private bool registered,disposed,initialized;
    internal CoreAudioSessionCatalog(Action changed,Func<bool> continuing)
    {
        this.changed=changed;
        var initializedResult=CoInitializeEx(0,0);if(initializedResult<0)Marshal.ThrowExceptionForHR(initializedResult);initialized=true;
        try
        {
            Continue(continuing);
            var clsid=new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");var iid=new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
            Check(CoCreateInstance(ref clsid,0,1,ref iid,out var created));enumerator=(IHubDeviceEnumerator)created;
            Continue(continuing);
            endpointEvents=new(changed);Check(enumerator.RegisterEndpointNotificationCallback(endpointEvents));registered=true;
        }
        catch{if(enumerator is not null)Release(enumerator);CoUninitialize();initialized=false;throw;}
    }
    private static void Check(int hr){if(hr!=0)throw new COMException("Core Audio did not return S_OK.",hr);}
    private static void Continue(Func<bool> continuing){if(!continuing())throw new COMException("Observation invalidated.",unchecked((int)0x80004004));}
    private static void Release(object value){if(Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    public ApplicationAudioCatalogSnapshot Discover(Func<bool> continuing)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var gaps=0;var found=new HashSet<string>(StringComparer.Ordinal);var activeSessions=new List<IApplicationAudioSession>();
        IHubDeviceCollection? devices=null;
        try
        {
            Continue(continuing);Check(enumerator.EnumAudioEndpoints(0,1,out devices));Continue(continuing);Check(devices.GetCount(out var count));
            if(count>32)gaps++;
            for(uint i=0;i<Math.Min(count,32);i++)
            {
                if(!continuing()){gaps++;break;}
                IMMDevice? device=null;
                try
                {
                    Check(devices.Item(i,out device));Continue(continuing);Check(device.GetId(out var id));found.Add(id);
                    if(!endpoints.TryGetValue(id,out var endpoint))
                    {
                        Continue(continuing);var iid=new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");Check(device.Activate(ref iid,23,0,out var activated));
                        var manager=(IHubAudioSessionManager2)activated;
                        var callback=new HubSessionCreated(changed);
                        endpoint=new(){Id=id,Device=device,Manager=manager,Callback=callback};device=null;
                        try{Continue(continuing);Check(manager.RegisterSessionNotification(callback));endpoint.Registered=true;endpoints.Add(id,endpoint);}
                        catch{endpoint.Dispose();throw;}
                    }
                    gaps+=Enumerate(endpoint,continuing);
                    foreach(var session in endpoint.Sessions.Values)
                    {
                        if(!continuing()){gaps++;break;}
                        if(session.State(out var state)!=0){gaps++;continue;}
                        if(state is 0 or 1)activeSessions.Add(session);
                        else if(state!=2)gaps++;
                    }
                }
                catch(Exception e)when(e is COMException or InvalidCastException){gaps++;}
                finally{if(device is not null)Release(device);}
            }
        }
        catch(COMException){gaps++;}
        finally{if(devices is not null)Release(devices);}
        // Removed endpoints never remain in the command's intended session set.
        foreach(var id in endpoints.Keys.Where(id=>!found.Contains(id)).ToArray()){endpoints[id].Dispose();endpoints.Remove(id);}
        return new(activeSessions,gaps);
    }
    private int Enumerate(Endpoint endpoint,Func<bool> continuing)
    {
        var gaps=0;IHubAudioSessionEnumerator? sessions=null;
        try
        {
            // Registration precedes GetSessionEnumerator/GetCount: GetCount enables session-created notifications.
            Continue(continuing);Check(endpoint.Manager.GetSessionEnumerator(out sessions));Continue(continuing);Check(sessions.GetCount(out var count));
            if(count<0||count>256)gaps++;
            for(var i=0;i<Math.Clamp(count,0,256);i++)
            {
                if(!continuing()){gaps++;break;}
                IHubAudioSessionControl? session=null;
                try{Check(sessions.GetSession(i,out session));Continue(continuing);Add(endpoint,session,continuing);session=null;}
                catch(Exception e)when(e is COMException or InvalidCastException){gaps++;}
                finally{if(session is not null)Release(session);}
            }
            // Retain sessions reported during startup or since enumeration. Enumeration alone is insufficient.
            while(endpoint.Callback.TryTake(out var pointer))
            {
                try
                {
                    if(!continuing()||endpoint.Sessions.Count>=256){gaps++;continue;}
                    var acquired=(IHubAudioSessionControl)Marshal.GetObjectForIUnknown(pointer);
                    try{Add(endpoint,acquired,continuing);acquired=null!;}finally{if(acquired is not null)Release(acquired);}
                }
                catch(Exception e)when(e is COMException or InvalidCastException){gaps++;}
                finally{Marshal.Release(pointer);}
            }
            gaps+=endpoint.Callback.TakeDropped();
            foreach(var key in endpoint.Sessions.Keys.ToArray())
            {
                if(!continuing()){gaps++;break;}
                var session=endpoint.Sessions[key];
                if(session.State(out var state)==0&&state==2){session.Dispose();endpoint.Sessions.Remove(key);}
            }
        }
        finally{if(sessions is not null)Release(sessions);}
        return gaps;
    }
    private void Add(Endpoint endpoint,IHubAudioSessionControl acquired,Func<bool> continuing)
    {
        var control=(IHubAudioSessionControl2)acquired;
        Continue(continuing);Check(control.GetSessionInstanceIdentifier(out var id));
        if(string.IsNullOrEmpty(id))throw new COMException("Session identity unavailable.");
        var key=endpoint.Id+"\0"+id;
        if(endpoint.Sessions.ContainsKey(key)){Release(acquired);return;}
        if(endpoint.Sessions.Count>=256)throw new COMException("Session retention limit reached.");
        Continue(continuing);var session=new Session(key,acquired,control,(IHubSimpleAudioVolume)acquired,changed);
        endpoint.Sessions.Add(key,session);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;endpointEvents.Stop();
        if(registered)try{enumerator.UnregisterEndpointNotificationCallback(endpointEvents);}catch(COMException) { }
        foreach(var endpoint in endpoints.Values)endpoint.Dispose();endpoints.Clear();
        Release(enumerator);if(initialized){CoUninitialize();initialized=false;}
    }
    [DllImport("ole32.dll",ExactSpelling=true)]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll",ExactSpelling=true)]private static extern void CoUninitialize();
    [DllImport("ole32.dll",ExactSpelling=true)]private static extern int CoCreateInstance(ref Guid clsid,nint outer,uint context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object instance);
}

[ComVisible(true),Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IHubSessionCreated
{
    [PreserveSig]int OnSessionCreated(nint session);
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class HubSessionCreated(Action changed) : IHubSessionCreated
{
    private readonly object gate=new();
    private readonly ConcurrentQueue<nint> pending=new();
    private bool stopped;
    private int count,dropped;
    public int OnSessionCreated(nint session)
    {
        lock(gate)
        {
            if(stopped||session==0)return 0;
            if(count>=256){dropped++;return 0;}
            Marshal.AddRef(session);pending.Enqueue(session);count++;
        }
        changed();return 0;
    }
    internal bool TryTake(out nint pointer){lock(gate){if(!pending.TryDequeue(out pointer))return false;count--;return true;}}
    internal int TakeDropped(){lock(gate){var result=dropped;dropped=0;return result;}}
    internal void Stop(){lock(gate)stopped=true;}
    internal void ReleasePending(){while(TryTake(out var pointer))Marshal.Release(pointer);}
}
[ComVisible(true),Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IHubSessionEvents
{
    [PreserveSig]int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)]string name,ref Guid context);
    [PreserveSig]int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)]string path,ref Guid context);
    [PreserveSig]int OnSimpleVolumeChanged(float volume,[MarshalAs(UnmanagedType.Bool)]bool muted,ref Guid context);
    [PreserveSig]int OnChannelVolumeChanged(uint count,nint volumes,uint channel,ref Guid context);
    [PreserveSig]int OnGroupingParamChanged(ref Guid grouping,ref Guid context);
    [PreserveSig]int OnStateChanged(int state);
    [PreserveSig]int OnSessionDisconnected(int reason);
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class HubSessionEvents(Action changed) : IHubSessionEvents
{
    private int stopped;
    internal void Stop()=>Interlocked.Exchange(ref stopped,1);
    private int Signal(){if(Volatile.Read(ref stopped)==0)changed();return 0;}
    public int OnDisplayNameChanged(string name,ref Guid context)=>Signal();
    public int OnIconPathChanged(string path,ref Guid context)=>Signal();
    public int OnSimpleVolumeChanged(float volume,bool muted,ref Guid context)=>Signal();
    public int OnChannelVolumeChanged(uint count,nint volumes,uint channel,ref Guid context)=>Signal();
    public int OnGroupingParamChanged(ref Guid grouping,ref Guid context)=>Signal();
    public int OnStateChanged(int state)=>Signal();
    public int OnSessionDisconnected(int reason)=>Signal();
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class HubEndpointEvents(Action changed) : IAudioDeviceCallback
{
    private int stopped;
    internal void Stop()=>Interlocked.Exchange(ref stopped,1);
    private int Signal(){if(Volatile.Read(ref stopped)==0)changed();return 0;}
    public int OnDeviceStateChanged(string id,uint state)=>Signal();
    public int OnDeviceAdded(string id)=>Signal();
    public int OnDeviceRemoved(string id)=>Signal();
    public int OnDefaultDeviceChanged(int flow,int role,string? id)=>Signal();
    public int OnPropertyValueChanged(string id,AudioPropertyKey key)=>Signal();
}

// Explicit flattened vtables avoid managed COM inheritance ambiguity. Every HRESULT is preserved.
[ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHubDeviceEnumerator
{
    [PreserveSig]int EnumAudioEndpoints(int flow,uint states,out IHubDeviceCollection devices);
    [PreserveSig]int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice device);
    [PreserveSig]int GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out IMMDevice device);
    [PreserveSig]int RegisterEndpointNotificationCallback(IAudioDeviceCallback callback);
    [PreserveSig]int UnregisterEndpointNotificationCallback(IAudioDeviceCallback callback);
}
[ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHubDeviceCollection
{
    [PreserveSig]int GetCount(out uint count);
    [PreserveSig]int Item(uint index,out IMMDevice device);
}
[ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHubAudioSessionManager2
{
    [PreserveSig]int GetAudioSessionControl(ref Guid session,uint flags,out IHubAudioSessionControl control);
    [PreserveSig]int GetSimpleAudioVolume(ref Guid session,uint flags,out IHubSimpleAudioVolume volume);
    [PreserveSig]int GetSessionEnumerator(out IHubAudioSessionEnumerator sessions);
    [PreserveSig]int RegisterSessionNotification(IHubSessionCreated callback);
    [PreserveSig]int UnregisterSessionNotification(IHubSessionCreated callback);
    [PreserveSig]int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)]string sessionId,nint callback);
    [PreserveSig]int UnregisterDuckNotification(nint callback);
}
[ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHubAudioSessionEnumerator
{
    [PreserveSig]int GetCount(out int count);
    [PreserveSig]int GetSession(int index,out IHubAudioSessionControl session);
}
[ComImport,Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHubAudioSessionControl
{
    [PreserveSig]int GetState(out int state);
    [PreserveSig]int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)]out string name);
    [PreserveSig]int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)]string name,ref Guid context);
    [PreserveSig]int GetIconPath([MarshalAs(UnmanagedType.LPWStr)]out string path);
    [PreserveSig]int SetIconPath([MarshalAs(UnmanagedType.LPWStr)]string path,ref Guid context);
    [PreserveSig]int GetGroupingParam(out Guid grouping);
    [PreserveSig]int SetGroupingParam(ref Guid grouping,ref Guid context);
    [PreserveSig]int RegisterAudioSessionNotification(IHubSessionEvents callback);
    [PreserveSig]int UnregisterAudioSessionNotification(IHubSessionEvents callback);
}
[ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHubAudioSessionControl2
{
    [PreserveSig]int GetState(out int state);
    [PreserveSig]int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)]out string name);
    [PreserveSig]int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)]string name,ref Guid context);
    [PreserveSig]int GetIconPath([MarshalAs(UnmanagedType.LPWStr)]out string path);
    [PreserveSig]int SetIconPath([MarshalAs(UnmanagedType.LPWStr)]string path,ref Guid context);
    [PreserveSig]int GetGroupingParam(out Guid grouping);
    [PreserveSig]int SetGroupingParam(ref Guid grouping,ref Guid context);
    [PreserveSig]int RegisterAudioSessionNotification(IHubSessionEvents callback);
    [PreserveSig]int UnregisterAudioSessionNotification(IHubSessionEvents callback);
    [PreserveSig]int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string id);
    [PreserveSig]int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string id);
    [PreserveSig]int GetProcessId(out uint pid);
    [PreserveSig]int IsSystemSoundsSession();
    [PreserveSig]int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)]bool optOut);
}
[ComImport,Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHubSimpleAudioVolume
{
    [PreserveSig]int SetMasterVolume(float volume,ref Guid context);
    [PreserveSig]int GetMasterVolume(out float volume);
    [PreserveSig]int SetMute([MarshalAs(UnmanagedType.Bool)]bool muted,ref Guid context);
    [PreserveSig]int GetMute([MarshalAs(UnmanagedType.Bool)]out bool muted);
}
