namespace ApolloOverlay;

internal enum ControllerPowerState
{
    Unknown,
    Battery,
    Wired,
    Charging
}

/// <summary>Presentation policy independent of WPF, device enumeration and input ownership.</summary>
internal sealed record ControllerBadgeModel(
    bool Visible,
    string Name,
    string IconKey,
    string BatteryText,
    double BatteryFraction,
    bool LowBattery,
    ControllerPowerState Power,
    string Description
)
{
    internal static ControllerBadgeModel Create(int connected, IReadOnlyList<ControllerIdentity> identities, ControllerDeviceSnapshot? battery, string status)
    {
        // RawGameController does not expose an XInput slot mapping. Multiple candidates
        // must remain unassociated rather than displaying another device's model/charge.
        var known = connected <= 1 && identities.Count == 1 ? identities[0] : null;
        var count = Math.Max(connected, identities.Count);
        var name = known?.Name ?? (count > 1 ? $"{count} controles" : "Controle XInput");
        var percent = known?.BatteryPercent is >= 0 and <= 100 ? known.BatteryPercent : null;
        var category = battery?.Connected == true ? battery.BatteryLevel : null;
        var wired = percent is null && (known is { Wireless: false } || battery?.BatteryType == ControllerBatteryType.Wired);
        var text = percent is { } value ? $"{value}%" : wired ? "USB" : category switch
        {
            ControllerBatteryLevel.Empty => "Vazia",
            ControllerBatteryLevel.Low => "Baixa",
            ControllerBatteryLevel.Medium => "Média",
            ControllerBatteryLevel.Full => "Cheia",
            _ => "—"
        };
        var fraction = percent is { } measured ? measured / 100d : !wired && category is { } level ? (int)level / 3d : 0;
        var low = percent is { } charge ? charge <= 20 : !wired && category is ControllerBatteryLevel.Empty or ControllerBatteryLevel.Low;
        var power = known?.Charging == true ? ControllerPowerState.Charging : wired ? ControllerPowerState.Wired : percent is not null || category is not null ? ControllerPowerState.Battery : ControllerPowerState.Unknown;
        var source = known is null ? "Modelo não associado ao controle ativo." : "Identificação informada pelo Windows; em streaming pode corresponder ao controle virtual.";
        var chargeDescription = percent is { } capacity ? $"Bateria {capacity} por cento" + (known?.Charging == true ? ", carregando" : "") : wired ? "Alimentação por USB ou dispositivo virtual; carga da bateria não informada" : category is not null ? "Bateria " + text.ToLowerInvariant() : "Bateria não informada pelo dispositivo";
        var description = name + " · " + chargeDescription + ". " + source + " " + (connected > 0 ? status : "Navegação por XInput deste dispositivo não confirmada.");
        return new(count > 0, name, known?.Family == ControllerFamily.Xbox ? "gamebar" : "controller", text, fraction, low, power, description);
    }
}
