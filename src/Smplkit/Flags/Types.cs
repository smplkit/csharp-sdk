namespace Smplkit.Flags;

/// <summary>
/// Describes a flag declaration for buffered registration.
/// </summary>
/// <remarks>
/// Used by <c>client.Flags.Register</c> to queue declarations for bulk
/// registration. <see cref="Service"/> and <see cref="Environment"/> default
/// to <c>null</c>; the runtime client fills them from the active
/// <see cref="Smplkit.SmplClient"/> when it forwards declarations.
/// </remarks>
/// <param name="Id">The flag identifier.</param>
/// <param name="Type">The flag type (<c>BOOLEAN</c>, <c>STRING</c>, <c>NUMERIC</c>, <c>JSON</c>).</param>
/// <param name="Default">The default value.</param>
/// <param name="Service">Owning service identifier (optional).</param>
/// <param name="Environment">Owning environment key (optional).</param>
public sealed record FlagDeclaration(
    string Id,
    string Type,
    object? Default,
    string? Service = null,
    string? Environment = null);
