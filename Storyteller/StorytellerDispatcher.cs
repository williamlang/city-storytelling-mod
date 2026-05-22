using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;

namespace CityStoryMod.Storyteller
{
    // Owns the in-flight storyteller run (if any) and exposes its status to the
    // main thread for UI consumption.
    //
    // Concurrency is gated by an in-process Task reference rather than a .lock
    // file on disk: a single CS2 process owns the city dir, so there's no
    // inter-process contention to defend against, and if the game crashes the
    // Task dies with it (no stale lock to age out on next launch).
    //
    // HTTP and file I/O are safe to do on a background thread. The hard Unity
    // rule — never touch Unity APIs or ECS state off the main thread — does not
    // apply to the API client's work. Tick() drains run completion onto the
    // main thread; event handlers (AssistantTurn, ToolResults) fire on the
    // background thread — UISystems that subscribe must marshal as needed.
    public class StorytellerDispatcher
    {
        public delegate Task<RunResult> RunFunc(CancellationToken ct);

        readonly ILog _log;
        Task<RunResult> _running;
        CancellationTokenSource _cts;
        DateTime _runStartedAtUtc;
        Conversation _activeConversation;

        public StorytellerDispatcher(ILog log) { _log = log; }

        public bool IsRunning => _running != null && !_running.IsCompleted;
        public RunResult LastResult { get; private set; }
        public DateTime? LastResultAtUtc { get; private set; }

        // Wall-clock duration of the in-flight run, or null when idle. Useful
        // for the status line ("Running… 12s").
        public TimeSpan? RunDuration => IsRunning ? DateTime.UtcNow - _runStartedAtUtc : (TimeSpan?)null;

        // Per-run streaming events for the UI to subscribe to. Forwarded from
        // the active Conversation via AttachConversation; consumers don't need
        // to know about the conversation directly. Fired on the worker thread.
        public event Action<string> RunStarted;
        public event Action<AssistantTurn> AssistantTurn;
        public event Action<IReadOnlyList<ToolResult>> ToolResults;
        public event Action<RunResult> RunFinished;

        // Wires a Conversation's events through to this dispatcher's events.
        // RunFunc implementations call this with the Conversation they're
        // about to drive so UI subscribers see the streaming turns without
        // needing a direct reference. Returns the same instance for chaining.
        public Conversation AttachConversation(Conversation conv)
        {
            if (conv == null) return null;
            _activeConversation = conv;
            conv.AssistantTurnCompleted += ForwardAssistantTurn;
            conv.ToolResultsRecorded += ForwardToolResults;
            return conv;
        }

        void ForwardAssistantTurn(AssistantTurn turn) => AssistantTurn?.Invoke(turn);
        void ForwardToolResults(IReadOnlyList<ToolResult> results) => ToolResults?.Invoke(results);

        // Direct event-emission for run paths that don't drive a Conversation
        // (e.g. ClaudeCliRunner parsing the CLI's --output-format stream-json
        // output and synthesizing turns from the JSONL stream). Same wire as
        // ForwardAssistantTurn / ForwardToolResults but callable from outside.
        public void EmitAssistantTurn(AssistantTurn turn) => AssistantTurn?.Invoke(turn);
        public void EmitToolResults(IReadOnlyList<ToolResult> results) => ToolResults?.Invoke(results);

        public bool Start(string runName, RunFunc func)
        {
            if (IsRunning)
            {
                _log.Info($"Storyteller run '{runName}' rejected: another run is in flight.");
                return false;
            }
            _cts = new CancellationTokenSource();
            _runStartedAtUtc = DateTime.UtcNow;
            CancellationToken token = _cts.Token;
            DateTime startedAt = _runStartedAtUtc;
            RunStarted?.Invoke(runName);
            _running = Task.Run(async () =>
            {
                try
                {
                    RunResult ok = await func(token);
                    return ok.WithDuration(DateTime.UtcNow - startedAt);
                }
                catch (OperationCanceledException)
                {
                    return RunResult.Cancelled().WithDuration(DateTime.UtcNow - startedAt);
                }
                catch (Exception ex)
                {
                    return RunResult.Failed(ex.ToString()).WithDuration(DateTime.UtcNow - startedAt);
                }
            });
            _log.Info($"Storyteller run '{runName}' started.");
            return true;
        }

        public void Cancel()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            _log.Info("Storyteller run cancellation requested.");
        }

        // Called from the main thread (ExportSystem.OnUpdate). Drains a completed
        // Task into LastResult/LastResultAtUtc and clears the live reference so
        // a new run can start. Cheap when no run is in flight (one null check).
        public void Tick()
        {
            if (_running == null || !_running.IsCompleted) return;
            LastResult = _running.Result;
            LastResultAtUtc = DateTime.UtcNow;
            _running = null;
            _cts?.Dispose();
            _cts = null;

            // Detach from the conversation so its events don't keep firing into
            // our forwarders after the run ends.
            if (_activeConversation != null)
            {
                _activeConversation.AssistantTurnCompleted -= ForwardAssistantTurn;
                _activeConversation.ToolResultsRecorded -= ForwardToolResults;
                _activeConversation = null;
            }

            _log.Info($"Storyteller run finished: success={LastResult.Success}, files_written={LastResult.FilesWritten}, duration={LastResult.Duration.TotalSeconds:F1}s");
            RunFinished?.Invoke(LastResult);
        }
    }

    public struct RunResult
    {
        public bool Success;
        public string Message;
        public int FilesWritten;
        public TimeSpan Duration;

        // Accumulated token usage across every model turn in this run. Zero for
        // failed/cancelled runs that never reached the model and for runs through
        // the Claude Code CLI provider until that path learns to parse the CLI's
        // JSON output. Surfaced to the UI / settings status line.
        public TokenUsage TotalUsage;

        public static RunResult Ok(int filesWritten) =>
            new RunResult { Success = true, FilesWritten = filesWritten };

        public static RunResult Failed(string err) =>
            new RunResult { Success = false, Message = err };

        public static RunResult Cancelled() =>
            new RunResult { Success = false, Message = "Cancelled" };

        public RunResult WithDuration(TimeSpan d)
        {
            Duration = d;
            return this;
        }

        public RunResult WithMessage(string m)
        {
            Message = m;
            return this;
        }

        public RunResult WithUsage(TokenUsage u)
        {
            TotalUsage = u;
            return this;
        }
    }
}
