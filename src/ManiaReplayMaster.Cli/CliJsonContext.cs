using System.Text.Json.Serialization;
using ManiaReplayMaster.Core.Pipeline;

namespace ManiaReplayMaster.Cli;

[JsonSerializable(typeof(PipelineSnapshot))]
internal partial class CliJsonContext : JsonSerializerContext;
