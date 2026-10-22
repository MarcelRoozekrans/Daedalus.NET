using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     A JSON-shaped value as a <see cref="JsonElement"/>, written without reflection: a copy of Thalos 0.19.0's
///     <c>WorkflowVariableJson.ToElement</c> for Thalos 0.18.0, which does not ship it. The same type table and the same
///     refusals: a <see cref="string"/>, a number from <see cref="byte"/> through <see cref="decimal"/> through its own
///     <see cref="Utf8JsonWriter.WriteNumberValue(double)"/> overload, a <see cref="bool"/>, <see langword="null"/>, a
///     <see cref="JsonElement"/>, a <see cref="JsonNode"/>, a <c>byte[]</c> as Base64, a string-keyed dictionary in entry
///     order, and any other sequence; everything else, a non-finite <see cref="float"/> or <see cref="double"/>, and a
///     value nested deeper than 64 levels throw <see cref="ArgumentException"/> naming the value's path and type. The bytes
///     are the ones the reflection-based serializer wrote with default options. Task 20 of phase 3.2 replaces this class
///     with the Thalos call.
/// </summary>
internal static class PlainJson
{
    /// <summary>The deepest a value may nest, counting the value itself as one level; the depth <see cref="JsonDocument"/> parses.</summary>
    private const int MaxDepth = 64;

    private const string Allowed = "Allowed: string, number, bool, null, JsonElement, JsonNode, and lists or dictionaries of those.";

    /// <summary>
    ///     <paramref name="value"/> as a <see cref="JsonElement"/>. Throws <see cref="ArgumentException"/>, with nothing
    ///     written, when the value is not JSON-shaped; the message's path starts at the root, as <c>$</c>, <c>[2]</c> or
    ///     <c>files[2]</c>.
    /// </summary>
    internal static JsonElement ToElement(object? value)
    {
        var refusal = Walk(null, value, depth: 1);
        if (refusal is not null)
            throw refusal.ToException(nameof(value));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Walk(writer, value, depth: 1);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    ///     Checks <paramref name="value"/> when <paramref name="writer"/> is <see langword="null"/>, and writes it otherwise.
    ///     Returns the refusal for the first value that is not JSON-shaped, its path collected on the way out.
    /// </summary>
    private static Refusal? Walk(Utf8JsonWriter? writer, object? value, int depth)
    {
        if (value is null)
        {
            writer?.WriteNullValue();
            return null;
        }

        if ((value is double d && !double.IsFinite(d)) || (value is float f && !float.IsFinite(f)))
            return Refusal.NonFinite(value);

        if (TryWriteNumber(writer, value) || TryWriteLeaf(writer, value))
            return null;

        if (depth > MaxDepth)
            return Refusal.TooDeep();

        return value switch
        {
            IReadOnlyDictionary<string, object?> map => WalkEntries(writer, map, depth),
            IDictionary<string, object?> map => WalkEntries(writer, map, depth),
            IDictionary map => WalkDictionary(writer, map, depth),
            IEnumerable sequence => WalkSequence(writer, sequence, depth),
            _ => Refusal.ForType(value.GetType()),
        };
    }

    private static bool TryWriteLeaf(Utf8JsonWriter? writer, object value)
    {
        switch (value)
        {
            case string s:
                writer?.WriteStringValue(s);
                return true;
            case bool b:
                writer?.WriteBooleanValue(b);
                return true;
            case byte[] bytes:
                writer?.WriteBase64StringValue(bytes);
                return true;
            case JsonElement element:
                if (writer is not null)
                    element.WriteTo(writer);
                return true;
            case JsonNode node:
                if (writer is not null)
                    node.WriteTo(writer);
                return true;
            default:
                return false;
        }
    }

    private static bool TryWriteNumber(Utf8JsonWriter? writer, object value)
    {
        switch (value)
        {
            case byte n:
                writer?.WriteNumberValue(n);
                return true;
            case sbyte n:
                writer?.WriteNumberValue(n);
                return true;
            case short n:
                writer?.WriteNumberValue(n);
                return true;
            case ushort n:
                writer?.WriteNumberValue(n);
                return true;
            case int n:
                writer?.WriteNumberValue(n);
                return true;
            case uint n:
                writer?.WriteNumberValue(n);
                return true;
            case long n:
                writer?.WriteNumberValue(n);
                return true;
            case ulong n:
                writer?.WriteNumberValue(n);
                return true;
            case float n:
                writer?.WriteNumberValue(n);
                return true;
            case double n:
                writer?.WriteNumberValue(n);
                return true;
            case decimal n:
                writer?.WriteNumberValue(n);
                return true;
            default:
                return false;
        }
    }

    private static Refusal? WalkEntries(Utf8JsonWriter? writer, IEnumerable<KeyValuePair<string, object?>> entries, int depth)
    {
        writer?.WriteStartObject();
        foreach (var (key, member) in entries)
        {
            writer?.WritePropertyName(key);
            if (Walk(writer, member, depth + 1) is { } refusal)
                return refusal.Under(key);
        }

        writer?.WriteEndObject();
        return null;
    }

    private static Refusal? WalkDictionary(Utf8JsonWriter? writer, IDictionary map, int depth)
    {
        writer?.WriteStartObject();
        foreach (DictionaryEntry entry in map)
        {
            if (entry.Key is not string key)
                return Refusal.ForType(map.GetType());

            writer?.WritePropertyName(key);
            if (Walk(writer, entry.Value, depth + 1) is { } refusal)
                return refusal.Under(key);
        }

        writer?.WriteEndObject();
        return null;
    }

    private static Refusal? WalkSequence(Utf8JsonWriter? writer, IEnumerable sequence, int depth)
    {
        writer?.WriteStartArray();
        var index = 0;
        foreach (var item in sequence)
        {
            if (Walk(writer, item, depth + 1) is { } refusal)
            {
                // A custom IDictionary<string, T> enumerates as KeyValuePair members: name the dictionary, at its own
                // path, rather than a pair inside it.
                return refusal.IsPair(item) ? Refusal.ForType(sequence.GetType()) : refusal.Under(index);
            }

            index++;
        }

        writer?.WriteEndArray();
        return null;
    }

    /// <summary>Why a value was refused, and its path below the root, innermost segment first.</summary>
    private sealed class Refusal
    {
        private readonly Type? _type;
        private readonly List<object> _segments = [];
        private object? _value;

        private Refusal(Type? type) => _type = type;

        public static Refusal ForType(Type type) => new(type);

        public static Refusal TooDeep() => new(type: null);

        public static Refusal NonFinite(object value) => new(type: null) { _value = value };

        /// <summary>Whether this refuses <paramref name="item"/> itself, a <see cref="KeyValuePair{TKey,TValue}"/>.</summary>
        public bool IsPair(object? item) =>
            _segments.Count == 0 && _type is { IsGenericType: true } type && item is not null
            && type == item.GetType() && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);

        public Refusal Under(string key)
        {
            _segments.Add(key);
            return this;
        }

        public Refusal Under(int index)
        {
            _segments.Add(index);
            return this;
        }

        /// <summary>The exception, its path starting at the root, written <c>$</c> when the root itself is refused.</summary>
        public ArgumentException ToException(string paramName)
        {
            var path = new StringBuilder();
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                _ = _segments[i] switch
                {
                    int index => path.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']'),
                    var key when path.Length == 0 => path.Append(key),
                    var key => path.Append('.').Append(key),
                };
            }

            var where = path.Length == 0 ? "$" : path.ToString();
            var message = (_type, _value) switch
            {
                (null, null) => $"Workflow variable '{where}' nests deeper than {MaxDepth} levels.",
                (null, IFormattable number) => $"Workflow variable '{where}' is {number.ToString(null, CultureInfo.InvariantCulture)}, which is not a JSON number. A float or double must be finite.",
                _ => $"Workflow variable '{where}' has type '{_type?.FullName}', which is not JSON-shaped. {Allowed}",
            };
            return new ArgumentException(message, paramName);
        }
    }
}
