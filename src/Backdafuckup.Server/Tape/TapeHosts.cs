using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Tape.Devices;

namespace Backdafuckup.Server.Tape;

/// <summary>The hosts tape devices can be attached to: this server; tape agents come later.</summary>
public sealed class TapeHosts(ITapeHost local)
{
    public IReadOnlyList<TapeHostDto> List() => [new TapeHostDto(TapeHostIds.Local, local.Name, true)];

    public ITapeHost Get(string id) =>
        id == TapeHostIds.Local ? local : throw new TapeRequestException($"There is no tape host '{id}'. Devices can be attached to this server ('{TapeHostIds.Local}').", StatusCodes.Status404NotFound);

    public string NameOf(string id) => id == TapeHostIds.Local ? local.Name : $"tape agent {id}";
}

/// <summary>A request that cannot be done as asked; the message says why, for the console.</summary>
public sealed class TapeRequestException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
