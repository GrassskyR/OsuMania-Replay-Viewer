using System.Text.Json.Serialization;

namespace ManiaReplayMaster.Game;

[JsonSerializable(typeof(SongsIndexCache))]
[JsonSerializable(typeof(ReplayHeaderIndexCache))]
internal partial class GameJsonContext : JsonSerializerContext;
