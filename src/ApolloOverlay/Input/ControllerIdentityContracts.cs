namespace ApolloOverlay;

public enum ControllerFamily
{
    Unknown,
    Xbox,
    PlayStation4,
    PlayStation5,
    PlayStation,
    Nintendo
}

public sealed record ControllerIdentity(string Id, string Name, ControllerFamily Family, bool Wireless, int? BatteryPercent, bool Charging);
public interface IControllerIdentitySource
{
    IReadOnlyList<ControllerIdentity> Read();
}
