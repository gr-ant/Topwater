# Topwater.Replay

Replay [Topwater](https://github.com/gr-ant/Topwater) recordings as regression tests, or against real bash in an
Ubuntu container to find where the simulation differs from Linux.

```bash
dotnet add package Topwater.Replay --prerelease
```

```csharp
// a recorded session as a test (any framework: a mismatch throws); TOPWATER_RERECORD=1 updates the file instead
[Fact]
public Task Todo_session_still_behaves() => Replay.AssertMatches("recordings/todo.jsonl");

// the same session on real bash (needs Docker)
var recording = Recording.Load("recordings/todo.jsonl");
await using var linux = await DockerTarget.StartAsync(recording.Start);
Console.WriteLine(await recording.ReplayAsync(linux, new ReplayOptions { Normalize = ReplayNormalizers.Volatile }));
```

See "Recordings" in the [reference](https://github.com/gr-ant/Topwater/blob/main/docs/reference.md#recordings).
