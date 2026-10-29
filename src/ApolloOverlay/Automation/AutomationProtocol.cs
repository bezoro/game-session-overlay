using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApolloOverlay.Automation;

public sealed record AutomationRequest
{
    public int Version
    {
        get;
        init;
    } = 1;
    public string RequestId
    {
        get;
        init;
    } = "";
    public string Verb
    {
        get;
        init;
    } = "status";
    public string ActionId
    {
        get;
        init;
    } = "";
    public string TargetId
    {
        get;
        init;
    } = "";
    public string Intent
    {
        get;
        init;
    } = "activate";
    public string Preset
    {
        get;
        init;
    } = "";
    public long Revision
    {
        get;
        init;
    } = -1;
    public long DeadlineUtc
    {
        get;
        init;
    }
    public bool DryRun
    {
        get;
        init;
    }
    public string BuildId
    {
        get;
        init;
    } = "";
    public string Channel
    {
        get;
        init;
    } = "";
    public string JobId
    {
        get;
        init;
    } = "";
    public bool AllowDowngrade
    {
        get;
        init;
    }
}

public sealed record AutomationTarget(string Id, long Handle, int Pid, long Started, long Generation, string Process);
public sealed record AutomationAction(string Id, bool Available, string Reason, string Capability, string[] Intents);
public sealed record AutomationSnapshot(
    string Instance,
    int Pid,
    long Started,
    long AtUtc,
    long Revision,
    string Route,
    bool Visible,
    bool Busy,
    AutomationTarget[] Targets,
    AutomationAction[] Actions,
    string SourceRevision,
    bool Truncated = false,
    string PendingRequestId = "",
    string Phase = "",
    long TargetsAtUtc = 0
);
public sealed record SteamKeyEvent(ushort Key, bool Up, bool Cleanup, long OffsetMs, bool Started, uint? Accepted);
public sealed record SteamSequenceEvidence(string Configuration, int[] MinimumIntervals, int FocusMs, SteamKeyEvent[] Events, int PossiblyHeld, bool? Released);
public sealed record AutomationUpdateRelease(string BuildId, string Version, string Commit, string Channel, long Size = 0);
public sealed record AutomationUpdateState
{
    public string Version
    {
        get;
        init;
    } = "";
    public string BuildId
    {
        get;
        init;
    } = "";
    public string Commit
    {
        get;
        init;
    } = "";
    public string Channel
    {
        get;
        init;
    } = "";
    public string SelectedChannel
    {
        get;
        init;
    } = "testing";
    public bool Installed
    {
        get;
        init;
    }
    public bool SettingsDirty
    {
        get;
        init;
    }
    public string JobId
    {
        get;
        init;
    } = "";
    public string Phase
    {
        get;
        init;
    } = "Idle";
    public string TargetBuildId
    {
        get;
        init;
    } = "";
    public string TargetVersion
    {
        get;
        init;
    } = "";
    public int Progress
    {
        get;
        init;
    }
    public bool Terminal
    {
        get;
        init;
    } = true;
    public string Message
    {
        get;
        init;
    } = "";
    public AutomationUpdateRelease[] Available
    {
        get;
        init;
    } = [];
    public AutomationUpdateRelease[] History
    {
        get;
        init;
    } = [];
}

public sealed record AutomationReply
{
    public int Version
    {
        get;
        init;
    } = 1;
    public string Outcome
    {
        get;
        init;
    } = "Unavailable";
    public string Reason
    {
        get;
        init;
    } = "";
    public string RequestId
    {
        get;
        init;
    } = "";
    public bool Terminal
    {
        get;
        init;
    } = true;
    public bool? Dispatched
    {
        get;
        init;
    } = false;
    public int? Expected
    {
        get;
        init;
    }
    public uint? Accepted
    {
        get;
        init;
    }
    public int? CleanupExpected
    {
        get;
        init;
    }
    public uint? CleanupAccepted
    {
        get;
        init;
    }
    public AutomationSnapshot? Snapshot
    {
        get;
        init;
    }
    public string Ticket
    {
        get;
        init;
    } = "";
    public long ExpiresUtc
    {
        get;
        init;
    }
    public string Instance
    {
        get;
        init;
    } = "";
    public int Pid
    {
        get;
        init;
    }
    public long Started
    {
        get;
        init;
    }
    public string[] Trace
    {
        get;
        init;
    } = [];
    public SteamSequenceEvidence? Sequence
    {
        get;
        init;
    }
    public AutomationUpdateState? Update
    {
        get;
        init;
    }
    public int Session
    {
        get;
        init;
    }

    public static AutomationReply Error(string outcome, string reason, string id = "") => new()
    {
        Outcome = outcome,
        Reason = reason,
        RequestId = id
    };
}

public sealed record AutomationRecord(string Id, string Canonical, AutomationReply Reply, bool Terminal, long AtUtc);
public sealed record AutomationRecords(AutomationRecord[] Records);
public sealed record AutomationTrace(
    string Instance,
    string RequestId,
    string Phase,
    long AtUtc,
    string SourceRevision,
    string TargetId,
    long Foreground,
    bool? Dispatched,
    int? Expected,
    uint? Accepted,
    string Outcome,
    SteamSequenceEvidence? Sequence = null
);
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(AutomationRequest))]
[JsonSerializable(typeof(AutomationReply))]
[JsonSerializable(typeof(AutomationSnapshot))]
[JsonSerializable(typeof(AutomationRecords))]
[JsonSerializable(typeof(AutomationTrace))]
[JsonSerializable(typeof(AutomationUpdateState))]
public partial class AutomationJson : JsonSerializerContext;
public static class AutomationFrames
{
    public const int RequestLimit = 16 * 1024, ResponseLimit = 64 * 1024;
    public static async Task<byte[]> Read(System.IO.Stream stream, int maximum, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > maximum)
            throw new System.IO.InvalidDataException("Frame outside bounds.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    public static async Task Write(System.IO.Stream stream, byte[] bytes, int maximum, CancellationToken token)
    {
        if (bytes.Length == 0 || bytes.Length > maximum)
            throw new System.IO.InvalidDataException("Frame outside bounds.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static byte[] Encode(AutomationReply reply) => JsonSerializer.SerializeToUtf8Bytes(reply, AutomationJson.Default.AutomationReply);
    public static byte[] Encode(AutomationRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, AutomationJson.Default.AutomationRequest);
}
