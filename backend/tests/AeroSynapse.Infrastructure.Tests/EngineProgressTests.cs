using System.Collections.Concurrent;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Infrastructure.Operations;
using Xunit;

namespace AeroSynapse.Infrastructure.Tests;

public sealed class EngineProgressTests
{
    private static SimulationRequest Request() => new("1.0", Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
        DateTimeOffset.UtcNow, "UTC", 10, 42, 20, 2, [new(Guid.NewGuid(), 1, 1)]);

    [Fact]
    public async Task Progress_is_saved_before_post_finishes_and_terminal_get_releases_the_wait_without_resubmitting()
    {
        var request = Request();
        var client = new WaitingClient(request);
        var recorder = new Recorder();
        var execution = EngineProgressSession.StartAsync(client, request, "trace-progress", recorder, 10, TestContext.Current.CancellationToken);
        await recorder.Recorded.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(execution.IsCompleted);
        Assert.Contains(recorder.Frames, frame => frame.SimulationMinute == 5);
        client.Complete = true;
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("completed", result.Status);
        Assert.Equal(1, client.Starts);
        Assert.True(client.Cancelled);
        Assert.Equal(10, recorder.Frames.Last().SimulationMinute);
    }

    [Fact]
    public async Task Rejected_progress_cancels_and_observes_the_original_post()
    {
        var request = Request();
        var client = new WaitingClient(request);
        var error = await Assert.ThrowsAsync<IntelligenceException>(() => EngineProgressSession.StartAsync(client,
            request, "trace-invalid", new Recorder { Reject = true }, 10, TestContext.Current.CancellationToken));
        Assert.Equal("engine_invalid_response", error.Code);
        Assert.Equal(1, client.Starts);
        Assert.True(client.Cancelled);
    }

    private sealed class Recorder : IRunProgressRecorder
    {
        public bool Reject { get; init; }
        public ConcurrentQueue<EngineProgress> Frames { get; } = new();
        public TaskCompletionSource Recorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RecordAsync(Guid runId, EngineProgress progress, CancellationToken token)
        {
            if (Reject) throw new InvalidOperationException("Progress differs from the captured run.");
            Frames.Enqueue(progress);
            Recorded.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class WaitingClient(SimulationRequest request) : IIntelligenceClient
    {
        public bool Complete { get; set; }
        public bool Cancelled { get; private set; }
        public int Starts { get; private set; }
        public async Task<EngineExecution> StartAsync(SimulationRequest input, string correlationId, CancellationToken token)
        {
            Starts++;
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("The response must be recovered through GET.");
        }
        public Task<EngineExecution?> GetAsync(Guid runId, string correlationId, CancellationToken token) =>
            Task.FromResult<EngineExecution?>(new(request.RunId, Complete ? "completed" : "running",
                Complete ? new("1.0", request.RunId, "completed", 10, [], [], null) : null,
                new(Complete ? 10 : 5, [new(request.Checkpoints[0].CheckpointId, 0, 10)])));
    }
}
