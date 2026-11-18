using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using FluentValidation;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Application.Settings;

public sealed record SettingsGroup(string Group, string Version, IReadOnlyDictionary<string, JsonElement> Values);
public sealed record GetSettings(string Group) : IQuery<SettingsGroup>;
public sealed record PutSettings(string Group, string Version, JsonElement Values, Guid ActorId) : ICommand<SettingsGroup>;

public interface ISettingsStore
{
    Task<IReadOnlyDictionary<string, string>> ReadAsync(string group, bool forUpdate, CancellationToken ct);
    void SetValue(string key, string value, Guid actorId, DateTime updatedAt);
}

public sealed class GetSettingsValidator : AbstractValidator<GetSettings>
{
    public GetSettingsValidator() => RuleFor(x => x.Group).Must(SettingsCatalog.IsApiGroup);
}

public sealed class PutSettingsValidator : AbstractValidator<PutSettings>
{
    public PutSettingsValidator()
    {
        RuleFor(x => x.Group).Must(SettingsCatalog.IsApiGroup);
        RuleFor(x => x.Version).NotNull().Matches("^[0-9a-f]{64}$");
        RuleFor(x => x.ActorId).NotEmpty();
        RuleFor(x => x.Values).Custom((values, context) =>
        {
            foreach (var error in SettingsCatalog.Validate(context.InstanceToValidate.Group, values))
                context.AddFailure("values", error);
        });
    }
}

public sealed class SettingsHandlers(ISettingsStore store, IClock clock) :
    IQueryHandler<GetSettings, SettingsGroup>, ICommandHandler<PutSettings, SettingsGroup>
{
    public async Task<Result<SettingsGroup>> Handle(GetSettings query, CancellationToken cancellationToken)
        => Result.Success(View(query.Group, await store.ReadAsync(query.Group, false, cancellationToken)));

    public async Task<Result<SettingsGroup>> Handle(PutSettings command, CancellationToken cancellationToken)
    {
        // The decorator owns the transaction; the adapter locks BEFORE reading current values.
        var current = await store.ReadAsync(command.Group, true, cancellationToken);
        if (View(command.Group, current).Version != command.Version)
            return Result.Failure<SettingsGroup>(new("concurrency_conflict", "Cấu hình đã thay đổi; tải lại trước khi lưu.", ErrorType.Conflict));
        var next = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var item in command.Values.EnumerateObject())
        {
            var value = SettingsJson.Canonical(item.Value);
            if (current.TryGetValue(item.Name, out var previous) && SettingsJson.Canonical(previous) == value) continue;
            store.SetValue(item.Name, value, command.ActorId, clock.UtcNow.UtcDateTime);
            next[item.Name] = value;
        }
        return Result.Success(View(command.Group, next));
    }

    private static SettingsGroup View(string group, IReadOnlyDictionary<string, string> values)
    {
        var safe = values.Where(x => SettingsCatalog.IsKnown(x.Key))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => JsonSerializer.Deserialize<JsonElement>(x.Value), StringComparer.Ordinal);
        var version = SettingsJson.Hash(SettingsJson.Serialize(new object[] { group, safe }));
        return new(group, version, safe);
    }
}

// Canonical JSON is also used for fingerprints; it never logs the input or JSON property names.
public static class SettingsJson
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Serialize<T>(T value) => Canonical(JsonSerializer.SerializeToElement(value, Options));
    public static string Canonical(string json) => Canonical(JsonSerializer.Deserialize<JsonElement>(json));
    public static string Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    public static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(HasDuplicateProperties);
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(x => !seen.Add(x.Name) || HasDuplicateProperties(x.Value));
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var item in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(item.Name); Write(writer, item.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
