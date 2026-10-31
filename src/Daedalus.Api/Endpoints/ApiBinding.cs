using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using MediaTypeHeaderValue = Microsoft.Net.Http.Headers.MediaTypeHeaderValue;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     A handler parameter that never fails binding and carries what MVC's model binding would have recorded instead,
///     so <see cref="ZeroAllocValidationEndpointFilter"/> can answer as MVC does (ruling R7). Minimal APIs answer a
///     bare 400 for a body or query value they cannot bind.
/// </summary>
public interface IApiBinding
{
    /// <summary>The bound value, which the validation filter validates when there is no <see cref="Problem"/>.</summary>
    object? BoundValue { get; }

    /// <summary>The validation problem MVC's <c>ModelStateInvalidFilter</c> would answer, or <see langword="null"/>.</summary>
    ValidationProblemDetails? Problem { get; }

    /// <summary>
    ///     Whether MVC would answer 415: no input formatter reads the request's <c>Content-Type</c> or its charset.
    ///     MVC's <c>UnsupportedContentTypeFilter</c> answers that before any binding problem (ruling R28).
    /// </summary>
    bool UnsupportedMediaType { get; }
}

/// <summary>
///     A JSON request body bound as MVC's <c>BodyModelBinder</c> and <c>SystemTextJsonInputFormatter</c> bind it, with
///     the endpoints' Http <c>JsonOptions</c> and the metadata of <see cref="ApiJsonSerializerContext"/>.
/// </summary>
/// <remarks>
///     <para>
///     The problem keys and messages are MVC's, as <c>MvcBehaviourContractTests</c> pins them (R17, R18): a reader
///     error under the JSON path where it stopped, with the reader's message; an empty body or the literal
///     <c>null</c> under <c>""</c> with "A non-empty request body is required."; for each of these the parameter's
///     own "The {name} field is required.", keyed by the parameter's name. A body that reads is then checked for
///     MVC's implicit <c>[Required]</c>: a member whose type is a reference type and whose setter is not annotated
///     nullable is refused when its value is <see langword="null"/>, keyed by its C# name, and nested objects and
///     collection elements are walked with MVC's <c>Items[0].Name</c> keys.
///     </para>
///     <para>
///     The parameter is optional when it has a default value, <c>ApiBody&lt;T&gt;? request = null</c>: an empty body
///     or the literal <c>null</c> then binds <see langword="null"/> with no problem, as MVC binds a nullable body
///     parameter (R17, R29). The handler parameter must keep the controller parameter's name, which the keys use.
///     </para>
/// </remarks>
public sealed class ApiBody<T> : IApiBinding, IBindableFromHttpContext<ApiBody<T>>
    where T : class
{
    private ApiBody(T? value, ValidationProblemDetails? problem, bool unsupportedMediaType)
    {
        Value = value;
        Problem = problem;
        UnsupportedMediaType = unsupportedMediaType;
    }

    /// <summary>The body, or <see langword="null"/> when it did not bind.</summary>
    public T? Value { get; }

    /// <inheritdoc />
    public ValidationProblemDetails? Problem { get; }

    /// <inheritdoc />
    public bool UnsupportedMediaType { get; }

    object? IApiBinding.BoundValue => Value;

    /// <summary>
    ///     Reads the body; never answers <see langword="null"/>, so binding never fails. Implemented explicitly, which
    ///     the request delegate generator and <c>RequestDelegateFactory</c> both call through the interface.
    /// </summary>
    static async ValueTask<ApiBody<T>?> IBindableFromHttpContext<ApiBody<T>>.BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameter);

        var name = parameter.Name ?? string.Empty;
        var optional = parameter.IsOptional;
        var canHaveBody = ApiBinding.CanHaveBody(context);

        // BodyModelBinder: no input formatter reads the Content-Type. An optional parameter with no body binds null.
        if (!ApiBinding.TryParseJsonMediaType(context.Request.ContentType, out var mediaType))
        {
            return new ApiBody<T>(null, null, unsupportedMediaType: !optional || canHaveBody);
        }

        // InputFormatter.ReadAsync: no body is "no value" for a required parameter, the default for an optional one.
        if (!canHaveBody)
        {
            return Empty(context, name, optional);
        }

        // TextInputFormatter.SelectCharacterEncoding: SystemTextJsonInputFormatter reads UTF-8 and UTF-16 only.
        if (!ApiBinding.TrySelectEncoding(mediaType, out var encoding))
        {
            return new ApiBody<T>(null, null, unsupportedMediaType: true);
        }

        var options = context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        T? value;
        try
        {
            value = await ApiBinding.ReadAsync(context, encoding, typeInfo);
        }
        catch (JsonException exception)
        {
            // SystemTextJsonInputFormatter: the reader's message, safe to return, under the path where it stopped.
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            ApiBinding.AddError(errors, exception.Path ?? string.Empty, exception.Message);
            if (!optional)
            {
                ApiBinding.AddError(errors, name, ApiBinding.Required(name));
            }

            return new ApiBody<T>(null, ApiProblem.BindingProblem(context, errors), false);
        }

        if (value is null)
        {
            return Empty(context, name, optional);
        }

        var required = ApiBinding.ImplicitRequired(value, typeInfo, options);
        return new ApiBody<T>(value, required.Count == 0 ? null : ApiProblem.BindingProblem(context, required), false);
    }

    private static ApiBody<T> Empty(HttpContext context, string name, bool optional) =>
        optional
            ? new ApiBody<T>(null, null, false)
            : new ApiBody<T>(null, ApiProblem.BindingProblem(context, ApiBinding.EmptyBodyErrors(name)), false);
}

/// <summary>
///     A top-level query value bound as MVC's <c>SimpleTypeModelBinder</c> binds it, with the invariant culture of
///     MVC's query value provider.
/// </summary>
/// <remarks>
///     Absent, it binds <c>default</c> with no problem and <see cref="HasValue"/> false, and the handler supplies the
///     controller parameter's default through <see cref="ValueOr"/>: <c>take.ValueOr(20)</c> stands for MVC's
///     <c>int take = 20</c>, since a handler parameter of this type cannot default to 20 (ruling R30). Sent but
///     unparsable, its problem is "The
///     value '{raw}' is not valid.", MVC's message for a top-level parameter (R16); sent empty, "The value '' is
///     invalid.", MVC's <c>ValueMustNotBeNull</c>. <c>{raw}</c> is every sent value joined by commas, as MVC quotes
///     them. The key is the parameter's name, which must stay the controller parameter's. A class rather than a
///     struct, because the framework's <see cref="IBindableFromHttpContext{TSelf}"/> requires one (ruling R31).
/// </remarks>
public sealed class ApiQuery<T> : IApiBinding, IBindableFromHttpContext<ApiQuery<T>>
    where T : IParsable<T>
{
    private ApiQuery(T value, bool hasValue, ValidationProblemDetails? problem)
    {
        Value = value;
        HasValue = hasValue;
        Problem = problem;
    }

    /// <summary>The parsed value, or <c>default</c> when none was sent or it did not parse.</summary>
    public T Value { get; }

    /// <summary>Whether a value was sent and parsed; lets a port stand in for a nullable MVC parameter.</summary>
    public bool HasValue { get; }

    /// <inheritdoc />
    public ValidationProblemDetails? Problem { get; }

    object? IApiBinding.BoundValue => Value;

    bool IApiBinding.UnsupportedMediaType => false;

    /// <summary>The parsed value, or <paramref name="fallback"/>, the controller parameter's default, when none was sent.</summary>
    public T ValueOr(T fallback) => HasValue ? Value : fallback;

    /// <summary>Reads the query value; never answers <see langword="null"/>, so binding never fails.</summary>
    static ValueTask<ApiQuery<T>?> IBindableFromHttpContext<ApiQuery<T>>.BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameter);

        var name = parameter.Name ?? string.Empty;
        if (!context.Request.Query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return ValueTask.FromResult<ApiQuery<T>?>(new ApiQuery<T>(default!, false, null));
        }

        var raw = values[0];
        ApiQuery<T> bound;
        if (string.IsNullOrWhiteSpace(raw))
        {
            // SimpleTypeModelBinder: an empty value is null; MVC's ConvertEmptyStringToNull makes a string null too,
            // and only a value type refuses it.
            bound = typeof(T) == typeof(string)
                ? new ApiQuery<T>(default!, false, null)
                : Refused(context, name, $"The value '{values}' is invalid.");
        }
        else
        {
            bound = T.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
                ? new ApiQuery<T>(parsed, true, null)
                : Refused(context, name, $"The value '{values}' is not valid.");
        }

        return ValueTask.FromResult<ApiQuery<T>?>(bound);
    }

    private static ApiQuery<T> Refused(HttpContext context, string name, string message) =>
        new(default!, false, ApiProblem.BindingProblem(context,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [name] = [message] }));
}

/// <summary>The type-independent half of <see cref="ApiBody{T}"/>: MVC's media types, charsets and implicit required.</summary>
internal static class ApiBinding
{
    private const string NonEmptyBody = "A non-empty request body is required.";

    /// <summary><c>SystemTextJsonInputFormatter.SupportedMediaTypes</c>.</summary>
    private static readonly MediaTypeHeaderValue[] JsonMediaTypes =
    [
        MediaTypeHeaderValue.Parse("application/json"),
        MediaTypeHeaderValue.Parse("text/json"),
        MediaTypeHeaderValue.Parse("application/*+json"),
    ];

    public static bool CanHaveBody(HttpContext context) =>
        context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? context.Request.ContentLength != 0;

    public static bool TryParseJsonMediaType(string? contentType, out MediaTypeHeaderValue mediaType)
    {
        if (MediaTypeHeaderValue.TryParse(contentType, out var parsed) && JsonMediaTypes.Any(parsed.IsSubsetOf))
        {
            mediaType = parsed;
            return true;
        }

        mediaType = null!;
        return false;
    }

    /// <summary>UTF-8 when no charset is sent, and UTF-8 or UTF-16 by name; any other charset MVC refuses with 415.</summary>
    public static bool TrySelectEncoding(MediaTypeHeaderValue mediaType, out Encoding encoding)
    {
        var charset = mediaType.Charset.Value;
        if (string.IsNullOrEmpty(charset) || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            encoding = Encoding.UTF8;
            return true;
        }

        if (charset.Equals("utf-16", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("utf-16le", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("unicode", StringComparison.OrdinalIgnoreCase))
        {
            encoding = Encoding.Unicode;
            return true;
        }

        encoding = null!;
        return false;
    }

    public static async ValueTask<T?> ReadAsync<T>(HttpContext context, Encoding encoding, JsonTypeInfo<T> typeInfo)
    {
        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            return await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted);
        }

        await using var transcoded = Encoding.CreateTranscodingStream(context.Request.Body, encoding, Encoding.UTF8, leaveOpen: true);
        return await JsonSerializer.DeserializeAsync(transcoded, typeInfo, context.RequestAborted);
    }

    public static Dictionary<string, string[]> EmptyBodyErrors(string name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddError(errors, string.Empty, NonEmptyBody);
        AddError(errors, name, Required(name));
        return errors;
    }

    /// <summary>
    ///     MVC's implicit <c>[Required]</c> on non-nullable reference members, as its <c>ValidationVisitor</c> applies
    ///     it: on the bound value, member by member, into nested objects and collection elements.
    /// </summary>
    public static Dictionary<string, string[]> ImplicitRequired(object value, JsonTypeInfo typeInfo, JsonSerializerOptions options)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        CollectImplicitRequired(value, typeInfo, options, string.Empty, errors);
        return errors;
    }

    public static string Required(string name) => $"The {name} field is required.";

    public static void AddError(Dictionary<string, string[]> errors, string key, string message) =>
        errors[key] = errors.TryGetValue(key, out var existing) ? [.. existing, message] : [message];

    private static void CollectImplicitRequired(
        object value, JsonTypeInfo typeInfo, JsonSerializerOptions options, string prefix, Dictionary<string, string[]> errors)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.Get is null)
            {
                continue;
            }

            // MVC keys and words the error with the CLR member name; the JSON name is camel-cased.
            var name = (property.AttributeProvider as MemberInfo)?.Name ?? property.Name;
            var member = property.Get(value);
            if (member is null)
            {
                if (!property.PropertyType.IsValueType && !property.IsSetNullable)
                {
                    AddError(errors, prefix + name, Required(name));
                }

                continue;
            }

            Walk(member, options.GetTypeInfo(property.PropertyType), options, prefix + name, errors);
        }
    }

    private static void Walk(
        object value, JsonTypeInfo typeInfo, JsonSerializerOptions options, string key, Dictionary<string, string[]> errors)
    {
        if (typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            CollectImplicitRequired(value, typeInfo, options, key + ".", errors);
            return;
        }

        if (typeInfo.Kind != JsonTypeInfoKind.Enumerable || typeInfo.ElementType is not { } elementType)
        {
            return;
        }

        var elementInfo = options.GetTypeInfo(elementType);
        if (elementInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var index = 0;
        foreach (var element in (IEnumerable)value)
        {
            if (element is not null)
            {
                CollectImplicitRequired(element, elementInfo, options,
                    key + "[" + index.ToString(CultureInfo.InvariantCulture) + "].", errors);
            }

            index++;
        }
    }
}
