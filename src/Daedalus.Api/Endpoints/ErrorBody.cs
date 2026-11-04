namespace Daedalus.Api.Endpoints;

/// <summary>
///     The <c>{ "error": "…" }</c> body the controllers wrote as an anonymous object, named so it has source-generated
///     metadata in <see cref="ApiJsonSerializerContext"/>. The Http <c>JsonOptions</c>' camel-case policy writes the
///     member as <c>error</c>.
/// </summary>
public sealed record ErrorBody(string Error);

/// <summary>
///     The <c>{ "error": "…", "details": "…" }</c> body <c>PrdController</c> wrote for its catch-all, named for the same
///     reason as <see cref="ErrorBody"/> (R44). <c>details</c> is the exception's message, as before.
/// </summary>
public sealed record ErrorDetailBody(string Error, string Details);
