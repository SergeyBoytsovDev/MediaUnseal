using System.Text.Json.Serialization;

namespace MediaUnseal.Services;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UiPreferences))]
[JsonSerializable(typeof(FfmpegTools.ToolPaths))]
[JsonSerializable(typeof(MediaGuideDiagramFile))]
internal partial class AppJsonContext : JsonSerializerContext { }
