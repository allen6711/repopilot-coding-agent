namespace RepoPilot.Api.Middleware;

/// <summary>
/// The identity recorded on an approval, rejection, or cancellation.
/// <para>
/// Attributable, not verified. Authentication is out of scope for this feature,
/// so this value is only as trustworthy as the deployment supplying it — and the
/// system must never present it as verified (FR-015a). Wrapping it in a type
/// rather than passing a bare string keeps that distinction visible at every
/// call site that stores it.
/// </para>
/// </summary>
public readonly record struct ActorIdentity(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Reads and validates the <c>X-Actor</c> header.
/// </summary>
public static class ActorIdentityBinding
{
    public const string HeaderName = "X-Actor";

    /// <summary>
    /// Extracts the actor from the request.
    /// </summary>
    /// <returns>
    /// The identity, or null when absent or blank. A blank actor is rejected
    /// explicitly: it would satisfy a NOT NULL column while leaving the decision
    /// unattributable, which is the thing SC-015 exists to prevent.
    /// </returns>
    public static ActorIdentity? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.TryGetValue(HeaderName, out var values))
        {
            return null;
        }

        var value = values.ToString().Trim();
        return string.IsNullOrEmpty(value) ? null : new ActorIdentity(value);
    }

    /// <summary>
    /// Reads the actor or produces the problem response describing its absence.
    /// </summary>
    public static bool TryRead(HttpRequest request, out ActorIdentity actor, out IResult problem)
    {
        var read = Read(request);

        if (read is null)
        {
            actor = default;
            problem = Results.Problem(
                title: "Actor identity required",
                detail:
                    $"Every approval, rejection, and cancellation must record who made it. " +
                    $"Supply a non-empty {HeaderName} header (FR-015a, SC-015).",
                statusCode: StatusCodes.Status422UnprocessableEntity);
            return false;
        }

        actor = read.Value;
        problem = Results.Empty;
        return true;
    }
}
