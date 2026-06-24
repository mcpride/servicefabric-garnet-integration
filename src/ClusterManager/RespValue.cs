using System.Globalization;

namespace ClusterManager;

/// <summary>
/// Minimal representation of a RESP reply. Only the bits required by the control
/// plane (text, integer, nested arrays, error flag) are modelled.
/// </summary>
internal sealed class RespValue
{
    public RespKind Kind { get; init; }

    public string? Text { get; init; }

    public long Integer { get; init; }

    public IReadOnlyList<RespValue>? Items { get; init; }

    public bool IsError => Kind == RespKind.Error;

    public string AsString() => Kind switch
    {
        RespKind.Integer => Integer.ToString(CultureInfo.InvariantCulture),
        RespKind.Null => string.Empty,
        _ => Text ?? string.Empty,
    };
}