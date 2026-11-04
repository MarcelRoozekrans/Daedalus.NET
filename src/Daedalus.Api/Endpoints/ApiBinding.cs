using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
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
///     A parameter marked <c>[Required]</c> (DataAnnotations) is refused when absent, or sent empty or blank as a
///     <c>string</c>, with MVC's "The {name} field is required." for a non-nullable reference parameter; the problem
///     merges with the other parameters' in argument order. Absent otherwise, it binds <c>default</c> with no problem and <see cref="HasValue"/> false, and the handler supplies the
///     controller parameter's default through <see cref="ValueOr"/>: <c>take.ValueOr(20)</c> stands for MVC's
///     <c>int take = 20</c>, since a handler parameter of this type cannot default to 20 (ruling R30). Sent but
///     unparsable, its problem is "The
///     value '{raw}' is not valid.", MVC's message for a top-level parameter (R16); sent empty, "The value '' is
///     invalid.", MVC's <c>ValueMustNotBeNull</c>. An optional parameter, <c>ApiQuery&lt;int&gt;? take = null</c>,
///     stands for MVC's nullable <c>int? take</c>: sent empty, it binds as absent, as MVC binds it null (R53).
///     <c>{raw}</c> is every sent value joined by commas, as MVC quotes them. The key is the parameter's name, which must stay the controller parameter's. A class rather than a
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
        var required = parameter.IsDefined(typeof(RequiredAttribute), inherit: false);
        if (!context.Request.Query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return ValueTask.FromResult<ApiQuery<T>?>(Unsent(context, name, required));
        }

        var raw = values[0];
        ApiQuery<T> bound;
        if (string.IsNullOrWhiteSpace(raw))
        {
            // SimpleTypeModelBinder: an empty value is null; MVC's ConvertEmptyStringToNull makes a string null too,
            // and only a non-nullable value type refuses it. An optional parameter stands for MVC's nullable T? (R53).
            bound = typeof(T) == typeof(string) || parameter.IsOptional
                ? Unsent(context, name, required)
                : Refused(context, name, $"The value '{values}' is invalid.");
        }
        else
        {
            bound = QueryValue.Parse(raw, out T parsed) switch
            {
                QueryParse.Parsed => new ApiQuery<T>(parsed, true, null),
                QueryParse.Malformed => Refused(context, name, $"The value '{values}' is not valid."),
                _ => Refused(context, name, QueryValue.InputNotValid),
            };
        }

        return ValueTask.FromResult<ApiQuery<T>?>(bound);
    }

    /// <summary>No usable value: bound as absent, or refused with MVC's required message for a <c>[Required]</c> parameter.</summary>
    private static ApiQuery<T> Unsent(HttpContext context, string name, bool required)
    {
        if (required)
        {
            return Refused(context, name, ApiBinding.Required(name));
        }

        return new ApiQuery<T>(default!, false, null);
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

    /// <summary>
    ///     <c>TextInputFormatter.SelectCharacterEncoding</c>: UTF-8 when no charset is sent; otherwise the charset's
    ///     <see cref="Encoding"/> must be UTF-8 or UTF-16, the formatter's <c>SupportedEncodings</c>, compared by
    ///     <see cref="Encoding.WebName"/>. A charset <see cref="Encoding.GetEncoding(string)"/> does not know is
    ///     refused like any other, with 415, as the 415 pins in <c>MvcBehaviourContractTests</c> show (R28).
    /// </summary>
    public static bool TrySelectEncoding(MediaTypeHeaderValue mediaType, out Encoding encoding)
    {
        // MVC's MediaType unquotes the parameter, so charset="utf-8" reads as utf-8.
        var charset = HeaderUtilities.RemoveQuotes(mediaType.Charset).Value;
        encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : EncodingNamed(charset) switch
        {
            { WebName: "utf-8" } => Encoding.UTF8,
            { WebName: "utf-16" } => Encoding.Unicode,
            _ => null!,
        };
        return encoding is not null;
    }

    /// <summary>
    ///     The encoding <paramref name="charset"/> names, or <see langword="null"/>. <see cref="Encoding"/> has no
    ///     non-throwing lookup, so this catches its <see cref="ArgumentException"/>, as MVC's <c>MediaType</c> does.
    /// </summary>
    private static Encoding? EncodingNamed(string charset)
    {
        if (charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return null;
        }
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

/// <summary>How <see cref="QueryValue.Parse{T}"/> read a query value.</summary>
internal enum QueryParse
{
    /// <summary>The value parsed.</summary>
    Parsed,

    /// <summary>
    ///     A <c>FormatException</c> or <c>OverflowException</c> in MVC: "The value '{raw}' is not valid.".
    /// </summary>
    Malformed,

    /// <summary>
    ///     Another exception in MVC's converter, which it reports with no message of its own: "The input was not valid.".
    /// </summary>
    InputNotValid,
}

/// <summary>
///     MVC's <c>TypeConverter</c> reading of a query value, with the invariant culture of its query value provider. For
///     <c>double</c>, <c>float</c> and <c>decimal</c> that is <c>NumberStyles.Float</c>; for other non-integer types
///     <c>T.TryParse</c>. For an integer type it is <c>BaseNumberConverter</c>: the value
///     trimmed, then a <c>#</c>, <c>0x</c> or <c>&amp;h</c> prefix in any case read as hexadecimal through
///     <c>Convert.ToInt32(rest, 16)</c> and its siblings, which take a <c>+</c> and a second <c>0x</c>, keep two's
///     complement, and fail with an <c>ArgumentException</c> for an empty rest or a minus sign. The 128-bit types parse
///     their hexadecimal with <c>NumberStyles.HexNumber</c> instead. Pinned against MVC in
///     <c>MvcBehaviourContractTests</c> and against the converters in <c>ApiBindingTests</c> (R37).
/// </summary>
internal static class QueryValue
{
    /// <summary><c>Resources.SerializableError_DefaultError</c>, MVC's text for an error without a message.</summary>
    public const string InputNotValid = "The input was not valid.";

    public static QueryParse Parse<T>(string raw, out T value)
        where T : IParsable<T>
    {
        if (typeof(T) == typeof(double) || typeof(T) == typeof(float) || typeof(T) == typeof(decimal))
        {
            // DoubleConverter, SingleConverter and DecimalConverter parse with NumberStyles.Float, which refuses the
            // thousands separators IParsable's TryParse accepts.
            return TryParseNumber(raw.Trim(), NumberStyles.Float, out value) ? QueryParse.Parsed : QueryParse.Malformed;
        }

        if (!IsInteger(typeof(T)))
        {
            return T.TryParse(raw, CultureInfo.InvariantCulture, out value!) ? QueryParse.Parsed : QueryParse.Malformed;
        }

        var text = raw.Trim();
        var hex = HexDigitsAfterPrefix(text);
        if (hex is null)
        {
            // BaseNumberConverter.FromString: NumberStyles.Integer, stated, since UInt128's IParsable TryParse also
            // takes thousands separators.
            return TryParseNumber(text, NumberStyles.Integer, out value) ? QueryParse.Parsed : QueryParse.Malformed;
        }

        value = default!;
        if (typeof(T) == typeof(Int128) || typeof(T) == typeof(UInt128))
        {
            return TryParseNumber(hex, NumberStyles.HexNumber, out value) ? QueryParse.Parsed : QueryParse.Malformed;
        }

        // Convert.ToInt32(hex, 16): an empty string is ArgumentOutOfRangeException and a sign ArgumentException.
        if (hex.Length == 0 || hex[0] == '-')
        {
            return QueryParse.InputNotValid;
        }

        var digits = hex[0] == '+' ? hex[1..] : hex;
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return TryParseNumber(digits, NumberStyles.AllowHexSpecifier, out value) ? QueryParse.Parsed : QueryParse.Malformed;
    }

    private static string? HexDigitsAfterPrefix(string text)
    {
        if (text.StartsWith('#'))
        {
            return text[1..];
        }

        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("&h", StringComparison.OrdinalIgnoreCase)
            ? text[2..]
            : null;
    }

    private static bool IsInteger(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
        || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte)
        || type == typeof(Int128) || type == typeof(UInt128);

    private static bool TryParseNumber<T>(string digits, NumberStyles styles, out T value)
    {
        var invariant = CultureInfo.InvariantCulture;
        object? parsed = typeof(T) switch
        {
            var t when t == typeof(int) => int.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(long) => long.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(short) => short.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(byte) => byte.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(uint) => uint.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(ulong) => ulong.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(ushort) => ushort.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(sbyte) => sbyte.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(Int128) => Int128.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(UInt128) => UInt128.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(double) => double.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(float) => float.TryParse(digits, styles, invariant, out var v) ? v : null,
            var t when t == typeof(decimal) => decimal.TryParse(digits, styles, invariant, out var v) ? v : null,
            _ => null,
        };
        value = parsed is T typed ? typed : default!;
        return parsed is not null;
    }
}

