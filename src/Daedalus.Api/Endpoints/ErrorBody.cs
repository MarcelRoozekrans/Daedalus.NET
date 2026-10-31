namespace Daedalus.Api.Endpoints;

/// <summary>
///     The <c>{ "error": "…" }</c> body the controllers write as an anonymous object, named so it has source-generated
///     metadata in <see cref="ApiJsonSerializerContext"/>. The Http <c>JsonOptions</c>' camel-case policy writes the
///     member as <c>error</c>.
/// </summary>
public sealed record ErrorBody(string Error);
