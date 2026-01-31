using System.Collections.Generic;
using Newtonsoft.Json;

namespace StashWatchSync.Services;

public static class GraphQlModels
{
    public sealed class GraphQlResponse<T>
    {
        [JsonProperty("data")]
        public T? Data { get; set; }

        [JsonProperty("errors")]
        public GraphQlError[]? Errors { get; set; }
    }

    public sealed class GraphQlError
    {
        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class FindScenesData
    {
        [JsonProperty("findScenes")]
        public FindScenesResult? FindScenes { get; set; }
    }

    public sealed class FindScenesResult
    {
        [JsonProperty("scenes")]
        public List<SceneStub> Scenes { get; set; } = new();
    }

    public sealed class FindSceneData
    {
        [JsonProperty("findScene")]
        public SceneActivity? FindScene { get; set; }
    }

    public sealed class SceneUpdateData
    {
        [JsonProperty("sceneUpdate")]
        public SceneActivity? SceneUpdate { get; set; }
    }

    public sealed class SceneStub
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("files")]
        public List<FileStub>? Files { get; set; }
    }

    public sealed class FileStub
    {
        [JsonProperty("path")]
        public string Path { get; set; } = string.Empty;
    }

    public sealed class SceneActivity
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("resume_time")]
        public double? ResumeTime { get; set; }

        [JsonProperty("play_duration")]
        public double? PlayDuration { get; set; }
    }

    public sealed class PerformerUpdateData
    {
        [JsonProperty("performerUpdate")]
        public Performer? PerformerUpdate { get; set; }
    }

    public sealed class Performer
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("favorite")]
        public bool? Favorite { get; set; }
    }
}
