namespace ApolloOverlay;

public sealed class PreviewControllerIdentitySource : IControllerIdentitySource
{
    public IReadOnlyList<ControllerIdentity> Items
    {
        get;
        set;
    } = [];

    public IReadOnlyList<ControllerIdentity> Read() => Items;
}
