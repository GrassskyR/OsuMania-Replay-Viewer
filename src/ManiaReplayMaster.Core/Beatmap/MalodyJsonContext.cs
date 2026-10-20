using System.Text.Json.Serialization;

namespace ManiaReplayMaster.Core.Beatmap;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(McModel))]
internal partial class MalodyJsonContext : JsonSerializerContext;
