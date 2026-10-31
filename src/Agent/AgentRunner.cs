using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOffice.Agent
{
/// <summary>One chat run: the server-side agent loop (Phase 4), modeled on opencode's session runner.</summary>
    internal sealed class AgentRunner
    {
        private readonly int _maxSteps; // ProviderSettings.MaxSteps, snapshotted per run
        private const int MaxStepAttempts = 3; // 1 try + 2 retries (retryable pre-stream)

        private const string MaxStepsPrompt =
            "You have reached the maximum number of tool-use steps for this turn. " +
            "Do not call any more tools. Summarize what you have done so far and " +
            "what remains, so the user can decide how to continue.";

        private const string InterruptedToolResult =
            "ERROR: Tool execution interrupted — the run was cancelled before " +
            "this call could execute. Do not retry it unless the user asks.";

        private readonly AgentSession _session;
        private readonly LlmClient _llm;
        private readonly SseEmitter _sse;
        private readonly Action<byte[]> _write;
        private readonly CancellationToken _ct;
        private readonly Action<AgentSession, LlmUsage, double> _onUsage;
        private readonly List<AgentMessage> _runMessages = new List<AgentMessage>();
        private readonly LlmUsage _totalUsage = new LlmUsage();
        private double _totalCost;

/// <summary>Streamed text for the in-flight step; cleared once persisted.</summary>
        private readonly System.Text.StringBuilder _stepText = new System.Text.StringBuilder();

        public AgentRunner(
            AgentSession session, LlmClient llm,
            SseEmitter sse, Action<byte[]> write, CancellationToken ct,
            Action<AgentSession, LlmUsage, double> onUsage = null)
        {
            _session = session;
            _llm = llm;
            _sse = sse;
            _write = write;
            _ct = ct;
            _onUsage = onUsage;
            _maxSteps = ProviderSettings.MaxSteps;
        }

/// <summary>The message_id of the last assistant step (for run.completed).</summary>
        public string LastAssistantId { get; private set; }

/// <summary>The run's appended messages, in order (user + steps + tools).</summary>
        public IEnumerable<AgentMessage> RunMessages
        {
            get { return _runMessages; }
        }

        public void TrackUserMessage(AgentMessage message)
        {
            _runMessages.Add(message);
        }

/// <summary>True when the run ended by user cancellation.</summary>
        public bool WasCancelled { get; private set; }

/// <summary>Run wall-clock start — powers run.completed's duration_ms.</summary>
        private DateTime _runStart;

/// <summary>Boundary for per-step durations.</summary>
        private DateTime _lastStepBoundary;

/// <summary>Host-side app context captured once per user turn.</summary>
        private string _appContext;
        private string _appContextTag;

/// <summary>Streamed text not yet persisted (mid-step).</summary>
        public string UnpersistedPartialText
        {
            get { return _stepText.ToString(); }
        }

        public async Task RunAsync()
        {
            await CaptureAppContextAsync().ConfigureAwait(false);

            _runStart = DateTime.UtcNow;
            _lastStepBoundary = _runStart;
            var step = 1;
            while (true)
            {
                _ct.ThrowIfCancellationRequested();

                StepOutcome outcome;
                try
                {
                    outcome = await RunStepWithRetryAsync(step).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }   // host settles
                catch (LlmException) { throw; }                 // host settles
                catch (Exception ex)
                {
                    // Runner bugs must not kill the add-in — surface as a
                    // provider-style failure so the host emits its error event.
                    Bridge.Log.Error("AgentRunner step failed unexpectedly", ex);
                    throw new LlmException(0, "Agent error: " + ex.Message);
                }

                if (!outcome.NeedsContinuation) return;
                step++;
                // Guard against a pathological provider that ignores
                // tool_choice:"none" on the disarmed step.
                if (step > _maxSteps) return;
            }
        }

        private sealed class StepOutcome
        {
            public bool NeedsContinuation;
        }

/// <summary>Snapshot the active document for this turn's context block (read_workbook_snapshot /.</summary>
        private async Task CaptureAppContextAsync()
        {
            string action, tag;
            switch (AppContext.Current)
            {
                case HostApp.Excel:
                    action = "read_workbook_snapshot"; tag = "EXCEL_CONTEXT"; break;
                case HostApp.Word:
                    action = "read_document_snapshot"; tag = "WORD_CONTEXT"; break;
                case HostApp.PowerPoint:
                    action = "read_presentation_snapshot"; tag = "DECK_CONTEXT"; break;
                default:
                    return;
            }

            try
            {
                var host = Bridge.BridgeHost.Current;
                if (host == null) return;
                var result = await host.ExecuteActionAsync(
                    action,
                    new Dictionary<string, object> { ["maxRows"] = 100 })
                    .ConfigureAwait(false);

                object dataRaw;
                var data = result != null && result.TryGetValue("data", out dataRaw)
                    ? dataRaw as Dictionary<string, object> : null;
                object textRaw;
                var text = data != null && data.TryGetValue("text", out textRaw)
                    ? textRaw as string : null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _appContext = text;
                    _appContextTag = tag;
                }
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("App context capture failed — running without it", ex);
            }
        }

        // ==================================================================
        // One step = one provider stream (+ retries) + its tool executions
        // ==================================================================

        private async Task<StepOutcome> RunStepWithRetryAsync(int step)
        {
            var isLastStep = step >= _maxSteps;
            var tools = isLastStep ? null : Tools.BridgeToolset.DefinitionsFor(AppContext.Current);
            var toolChoice = isLastStep ? "none" : (string)null;
            var model = ModelRegistry.EffectiveModel();
            var maxTokens = EffectiveMaxTokens(model);

            string assistantId;
            lock (_session.Sync) { assistantId = _session.PeekNextMessageId(); }
            LastAssistantId = assistantId;
            _sse.Emit(_write, "message.started", new Dictionary<string, object>
            {
                ["message"] = new Dictionary<string, object>
                {
                    ["id"] = assistantId,
                    ["role"] = "assistant",
                },
            });

            // ---- Pre-flight compaction (opencode compactIfNeeded) ----
            var wire = BuildWireMessages(isLastStep);
            var estimate = Compaction.Estimate(new Dictionary<string, object>
            {
                ["messages"] = wire,
                ["tools"] = tools,
            });
            if (Compaction.NeedsCompaction(estimate, model.ContextWindow, maxTokens))
            {
                await RunCompactionAsync(assistantId, model, "pre-flight").ConfigureAwait(false);
                wire = BuildWireMessages(isLastStep);
            }

            _stepText.Length = 0;
            var requestStart = DateTime.UtcNow;
            var firstDeltaLogged = false;
            Action logFirstDelta = () =>
            {
                if (firstDeltaLogged) return;
                firstDeltaLogged = true;
                // TTFT diagnostic: separates "provider thinking silently"
                // (huge ms) from transport trouble (small ms, late UI).
                Bridge.Log.Info(
                    "Step " + step + " first token after " +
                    (int)(DateTime.UtcNow - requestStart).TotalMilliseconds + " ms");
            };
            var sink = new DeltaSink(
                delta =>
                {
                    logFirstDelta();
                    _stepText.Append(delta);
                    _sse.Emit(_write, "assistant.delta", new Dictionary<string, object>
                    {
                        ["message_id"] = assistantId,
                        ["delta"] = delta,
                    });
                },
                reasoning =>
                {
                    logFirstDelta();
                    _sse.Emit(_write, "tool.progress", new Dictionary<string, object>
                    {
                        ["message_id"] = assistantId,
                        ["tool_name"] = "reasoning",
                        ["delta"] = reasoning,
                    });
                });

            LlmStreamResult result = null;
            Exception failure = null;
            var overflowRetried = false;
            for (var attempt = 1; attempt <= MaxStepAttempts; attempt++)
            {
                try
                {
                    result = await _llm.StreamChatAsync(
                            ProviderSettings.Model, wire, sink, _ct,
                            tools, toolChoice, maxTokens,
                            requestImageOutput: model.OutputImage)
                        .ConfigureAwait(false);
                    failure = null;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failure = ex;
                    var llmEx = ex as LlmException;

                    // Reactive context overflow (opencode runAfterOverflowCompaction):
                    // compact and retry the step EXACTLY once, only when no
                    // assistant content was emitted yet.
                    if (llmEx != null && !overflowRetried && _stepText.Length == 0 &&
                        Compaction.IsContextOverflowError(llmEx.StatusCode, llmEx.Message))
                    {
                        overflowRetried = true;
                        var compacted = await RunCompactionAsync(
                            assistantId, model, "overflow").ConfigureAwait(false);
                        if (compacted)
                        {
                            wire = BuildWireMessages(isLastStep);
                            attempt--; // reactive retry has its own single-shot budget
                            continue;
                        }
                        throw; // nothing to compact — surface the overflow
                    }

                    var retryable = llmEx != null && LlmException.IsRetryable(llmEx.StatusCode);
                    // Mid-stream failures (some text already went out) are
                    // terminal — retrying would duplicate emitted content.
                    if (!retryable || _stepText.Length > 0 || attempt == MaxStepAttempts)
                        throw;
                    Bridge.Log.Info(
                        "Retryable provider failure (" +
                        llmEx.StatusCode + "), attempt " + attempt +
                        " of " + MaxStepAttempts + ": " + ex.Message);
                    await Task.Delay(BackoffWithJitter(attempt), _ct).ConfigureAwait(false);
                }
            }
            if (result == null)
            {
                // Unreachable (the loop either breaks with a result or throws),
                // but keep the compiler honest.
                throw failure ?? new LlmException(0, "Provider did not respond.");
            }

            var text = result.Text;
            if (text.Length == 0 && result.ToolCalls.Count == 0 &&
                result.FinishReason == "length")
            {
                text =
                    "[The model hit its output token limit (finish_reason: length) " +
                    "before producing a reply. With reasoning models, the thinking " +
                    "budget shares that limit — retry the request, or use a model " +
                    "with a larger output budget.]";
                Bridge.Log.Info(
                    "Step ended finish_reason=length with empty content (step " +
                    step + ") — output budget exhausted before reply.");
            }

            // ---- Persist the assistant message BEFORE any side effect ----
            ArrayList toolCallsWire = null;
            if (result.ToolCalls.Count > 0)
            {
                if (isLastStep)
                {
                    // The disarmed step (tool_choice:"none") must never record
                    // tool calls: they would persist without matching tool
                    // messages and poison the next request's history.
                    Bridge.Log.Info(
                        "Provider streamed tool calls on the disarmed max-steps " +
                        "turn (" + result.ToolCalls.Count + ") — dropping them.");
                }
                else
                {
                    toolCallsWire = BuildToolCallsWire(result.ToolCalls);
                }
            }

            var stepCost = CostOf(model, result.Usage);
            var boundaryNow = DateTime.UtcNow;
            var stepMs = (long)(boundaryNow - _lastStepBoundary).TotalMilliseconds;
            _lastStepBoundary = boundaryNow;
            if (result.Usage != null)
            {
                Bridge.Log.Info(
                    "Step " + step + " usage in/out/reason/cache " +
                    result.Usage.AdjustedInput + "/" + result.Usage.VisibleOutput + "/" +
                    result.Usage.ReasoningTokens + "/" + result.Usage.CacheReadTokens +
                    " → cost $" + stepCost.ToString("0.000000") +
                    " (model " + (model != null ? model.Id : "?") +
                    ", prices in/out/cache " +
                    (model != null ? model.PriceInput : 0) + "/" +
                    (model != null ? model.PriceOutput : 0) + "/" +
                    (model != null ? model.PriceCacheRead : 0) +
                    ", source " + (model != null ? model.Source : "?") + ")");
            }
            var usageJson = result.Usage != null
                ? BuildUsageJson(model, result.Usage, stepCost, stepMs)
                : null;
            object contentToPersist = text;
            if (result.Images.Count > 0)
            {
                var parts = new ArrayList();
                if (text.Length > 0)
                {
                    parts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = text,
                    });
                }
                foreach (var imageUrl in result.Images)
                {
                    parts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new Dictionary<string, object>
                        {
                            ["url"] = imageUrl,
                        },
                    });
                }
                contentToPersist = parts;
            }

            AgentMessage assistantMessage;
            lock (_session.Sync)
            {
                assistantMessage = _session.AppendMessage(
                    "assistant", contentToPersist, result.FinishReason, toolCallsWire, usageJson);
                // Feed the generated-image buffer (insert_picture pulls from
                // it — the model references images by recency, never by bytes).
                foreach (var imageUrl in result.Images)
                {
                    _session.GeneratedImages.Add(new AgentSession.GeneratedImage
                    {
                        DataUrl = imageUrl,
                    });
                    while (_session.GeneratedImages.Count > AgentSession.MaxGeneratedImages)
                        _session.GeneratedImages.RemoveAt(0);
                }
            }
            _stepText.Length = 0; // persisted — no longer "unpersisted partial"
            _runMessages.Add(assistantMessage);
            if (result.Usage != null) Accumulate(result.Usage, stepCost);

            var completedPayload = new Dictionary<string, object>
            {
                ["message_id"] = assistantId,
                ["content"] = text,
                ["completed"] = true,
                ["partial"] = false,
                ["interrupted"] = false,
                ["finish_reason"] = result.FinishReason,
                ["usage"] = UsageJson(
                    result.Usage, stepCost, stepMs, model != null ? model.Id : null),
            };
            if (result.Reasoning.Length > 0)
                completedPayload["reasoning_content"] = result.ReasoningText;
            if (result.Images.Count > 0)
                completedPayload["images"] = new ArrayList(result.Images);
            _sse.Emit(_write, "assistant.completed", completedPayload);

            if (result.ToolCalls.Count == 0 || isLastStep)
                return new StepOutcome { NeedsContinuation = false };

            // ---- Execute the calls sequentially, then continue the loop ----
            for (var i = 0; i < result.ToolCalls.Count; i++)
            {
                var call = result.ToolCalls[i];
                var remaining = result.ToolCalls.Count - i - 1;

                if (_ct.IsCancellationRequested)
                {
                    // Settle this call and every unexecuted one (invariant:
                    // no assistant tool_calls without matching tool messages).
                    SettleInterrupted(call);
                    for (var j = i + 1; j < result.ToolCalls.Count; j++)
                        SettleInterrupted(result.ToolCalls[j]);
                    WasCancelled = true;
                    throw new OperationCanceledException();
                }

                await ExecuteOneToolAsync(assistantId, call).ConfigureAwait(false);

                if (_ct.IsCancellationRequested && remaining > 0)
                {
                    for (var j = i + 1; j < result.ToolCalls.Count; j++)
                        SettleInterrupted(result.ToolCalls[j]);
                    WasCancelled = true;
                    throw new OperationCanceledException();
                }
            }

            return new StepOutcome { NeedsContinuation = true };
        }

        private async Task ExecuteOneToolAsync(string assistantId, LlmToolCall call)
        {
            var toolName = string.IsNullOrEmpty(call.Name) ? "unknown" : call.Name;
            object parsedArgs = ParseArgs(call);

            _sse.Emit(_write, "tool.started", new Dictionary<string, object>
            {
                ["message_id"] = assistantId,
                ["tool_name"] = toolName,
            });

            Dictionary<string, object> result;
            try
            {
                result = await Tools.BridgeToolset.ExecuteAsync(call, _session, _ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Tool failures are data — BridgeToolset already never throws,
                // this guards the runner itself.
                Bridge.Log.Error("Tool '" + toolName + "' threw past the toolset", ex);
                result = new Dictionary<string, object>
                {
                    ["status"] = "error",
                    ["summary"] = "Tool execution failed: " + ex.Message,
                };
            }

            // Persist the tool result (parts when a capture came back).
            object content = Tools.BridgeToolset.FormatResultContent(result);
            var imagePart = Tools.BridgeToolset.ExtractImagePart(result);
            if (imagePart != null)
            {
                var parts = new ArrayList
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = content,
                    },
                };
                foreach (var part in imagePart) parts.Add(part);
                content = parts;
            }

            AgentMessage toolMessage;
            lock (_session.Sync)
            {
                toolMessage = _session.AppendToolMessage(call.Id, toolName, content);
            }
            _runMessages.Add(toolMessage);

            _sse.Emit(_write, "tool.completed", new Dictionary<string, object>
            {
                ["message_id"] = assistantId,
                ["tool_name"] = toolName,
                ["preview"] = Tools.BridgeToolset.ResultPreview(result),
                ["args"] = parsedArgs,
            });
        }

/// <summary>Persist + emit an error result for a call that never ran.</summary>
        private void SettleInterrupted(LlmToolCall call)
        {
            var toolName = string.IsNullOrEmpty(call.Name) ? "unknown" : call.Name;
            AgentMessage toolMessage;
            lock (_session.Sync)
            {
                toolMessage = _session.AppendToolMessage(
                    call.Id, toolName, InterruptedToolResult);
            }
            _runMessages.Add(toolMessage);

            _sse.Emit(_write, "tool.completed", new Dictionary<string, object>
            {
                ["message_id"] = LastAssistantId,
                ["tool_name"] = toolName,
                ["preview"] = "Error: interrupted",
                ["args"] = ParseArgs(call),
            });
        }

        // ==================================================================
        // Wire assembly
        // ==================================================================

/// <summary>Full history as OpenAI-shaped messages: system prompt (host-owned, legacy-safe), then user/assistant/tool turns.</summary>
        private List<Dictionary<string, object>> BuildWireMessages(bool isLastStep)
        {
            var wire = new List<Dictionary<string, object>>();

            var systemPrompt = AgentPrompt.ForSession(_session);
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                // Read per request (not stored in the session): edits apply
                // to existing conversations too.
                var custom = Taskpane.HostSettings.CustomInstructions;
                if (custom != null)
                {
                    systemPrompt +=
                        "\n\n## Custom instructions\n" +
                        "Standing instructions configured by the user or their administrator. " +
                        "Follow them in every response.\n" + custom;
                }
                if (!string.IsNullOrEmpty(_appContext))
                {
                    // Host-owned context block: fresh per user turn, so the
                    // model reads the current state instead of re-reading.
                    systemPrompt +=
                        "\n\n[" + _appContextTag + "]\n" + _appContext + "\n[/" + _appContextTag + "]\n" +
                        "The block above is a snapshot of the live document taken at the " +
                        "start of this turn. Use it directly — do not re-read what it " +
                        "already shows.";
                }
                wire.Add(new Dictionary<string, object>
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt,
                });
            }

            // Snapshot under lock, then lower without it (appended messages
            // are immutable; the list only grows from this same runner).
            List<AgentMessage> snapshot;
            lock (_session.Sync)
            {
                snapshot = new List<AgentMessage>(_session.Messages);
            }
            var capabilities = ModelRegistry.EffectiveModel();
            string checkpoint;
            var cutIndex = Compaction.FindLatestCheckpoint(snapshot, out checkpoint);
            if (checkpoint != null)
            {
                wire.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = checkpoint,
                });
            }

            for (var i = cutIndex + 1; i < snapshot.Count; i++)
            {
                var message = snapshot[i];
                if (message.Role == "tool")
                {
                    if (message.ToolCallId == null) continue; // malformed — skip
                    wire.Add(new Dictionary<string, object>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = message.ToolCallId,
                        ["content"] = SanitizePartsForModel(message.Content, capabilities, "tool"),
                    });
                    continue;
                }

                if (message.Role != "user" && message.Role != "assistant")
                    continue;

                var entry = new Dictionary<string, object> { ["role"] = message.Role };
                if (message.Role == "assistant" && message.ToolCalls != null)
                {
                    var text = message.FlattenContent();
                    entry["content"] = text.Length > 0 ? (object)text : null;
                    entry["tool_calls"] = message.ToolCalls;
                }
                else
                {
                    entry["content"] = SanitizePartsForModel(message.Content, capabilities, message.Role);
                }
                wire.Add(entry);
            }

            if (isLastStep)
            {
                // Opencode max-steps.ts: disarm tools AND tell the model why.
                wire.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = MaxStepsPrompt,
                });
            }
            return wire;
        }

/// <summary>Drop parts the active model cannot process, replacing them with a text note so the model knows something was omitted.</summary>
        private static object SanitizePartsForModel(object content, ModelInfo model, string role)
        {
            var parts = content as ArrayList;
            if (parts == null) return content;

            var isAssistant = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
            ArrayList kept = null;
            var droppedImages = 0;
            var droppedFiles = 0;
            for (var i = 0; i < parts.Count; i++)
            {
                var dict = parts[i] as Dictionary<string, object>;
                var type = dict != null ? ModelRegistry.GetStr(dict, "type") : null;
                var isImage = string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase);
                var isFile = string.Equals(type, "file", StringComparison.OrdinalIgnoreCase);
                var drop =
                    (isImage && (isAssistant || model == null || !model.InputImage)) ||
                    (isFile && (isAssistant || model == null || !model.InputPdf));
                if (drop)
                {
                    if (kept == null)
                    {
                        kept = new ArrayList();
                        for (var j = 0; j < i; j++) kept.Add(parts[j]);
                    }
                    if (isImage) droppedImages++;
                    else droppedFiles++;
                    continue;
                }
                if (kept != null) kept.Add(parts[i]);
            }
            if (droppedImages == 0 && droppedFiles == 0) return content;

            var note = "[";
            if (droppedImages > 0)
            {
                note += droppedImages + " image(s) omitted: " +
                    (isAssistant
                        ? "images the model generated are not sent back to the provider"
                        : "the active model has no vision capability — switch to a vision-capable model to see images");
            }
            if (droppedFiles > 0)
            {
                if (droppedImages > 0) note += "; ";
                note += droppedFiles + " file(s) omitted: " +
                    (isAssistant
                        ? "files are not re-sent from assistant history"
                        : "the active model does not accept this file type natively");
            }
            note += ".]";
            kept.Add(new Dictionary<string, object>
            {
                ["type"] = "text",
                ["text"] = note,
            });
            return kept;
        }

        private static ArrayList BuildToolCallsWire(List<LlmToolCall> calls)
        {
            var wire = new ArrayList();
            foreach (var call in calls)
            {
                var dict = new Dictionary<string, object>
                {
                    ["id"] = string.IsNullOrEmpty(call.Id)
                        ? "call_" + Guid.NewGuid().ToString("N").Substring(0, 9)
                        : call.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object>
                    {
                        ["name"] = call.Name,
                        ["arguments"] = string.IsNullOrEmpty(call.Arguments)
                            ? "{}" : call.Arguments,
                    },
                };
                if (call.ExtraFields != null)
                {
                    foreach (var kvp in call.ExtraFields)
                    {
                        if (kvp.Key != "id" && kvp.Key != "type" && kvp.Key != "function" && kvp.Key != "index")
                        {
                            dict[kvp.Key] = kvp.Value;
                        }
                    }
                }
                wire.Add(dict);
            }
            return wire;
        }

        // ==================================================================
        // Small helpers
        // ==================================================================

        private static object ParseArgs(LlmToolCall call)
        {
            if (string.IsNullOrWhiteSpace(call.Arguments)) return null;
            try
            {
                return new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<object>(call.Arguments);
            }
            catch
            {
                return call.Arguments; // raw string — better than nothing for the UI
            }
        }

        private void Accumulate(LlmUsage usage, double cost)
        {
            _totalUsage.InputTokens += usage.InputTokens;
            _totalUsage.OutputTokens += usage.OutputTokens;
            _totalUsage.TotalTokens += usage.TotalTokens;
            _totalUsage.ReasoningTokens += usage.ReasoningTokens;
            _totalUsage.CacheReadTokens += usage.CacheReadTokens;
            _totalCost += cost;
            if (_onUsage != null)
            {
                // Per-step (not per-run) so cancelled runs still persist the
                // usage of every completed step.
                try { _onUsage(_session, usage, cost); }
                catch (Exception ex) { Bridge.Log.Error("Usage accounting failed", ex); }
            }
        }

/// <summary>Usage accumulated across all steps (for run.completed).</summary>
        public object TotalUsageJson()
        {
            if (_totalUsage.TotalTokens == 0 &&
                _totalUsage.InputTokens == 0 && _totalUsage.OutputTokens == 0)
            {
                return null;
            }
            return UsageJson(_totalUsage, _totalCost,
                (long)(DateTime.UtcNow - _runStart).TotalMilliseconds);
        }

/// <summary>Spend accumulated across all steps (USD estimate).</summary>
        public double TotalCost
        {
            get { return _totalCost; }
        }

        // ==================================================================
        // Cost + compaction helpers
        // ==================================================================

/// <summary>Opencode getUsage(): adjusted input (minus cache reads) + visible output at output rate + REASONING at output rate +.</summary>
        private static double CostOf(ModelInfo model, LlmUsage usage)
        {
            if (model == null || usage == null) return 0;
            if (model.PriceInput == 0 && model.PriceOutput == 0 &&
                model.PriceCacheRead == 0) return 0;
            return (
                usage.AdjustedInput * model.PriceInput +
                usage.VisibleOutput * model.PriceOutput +
                usage.ReasoningTokens * model.PriceOutput +
                usage.CacheReadTokens * model.PriceCacheRead) / 1000000.0;
        }

/// <summary>Effective max_tokens: user override, else catalog limit capped at 32K.</summary>
        private static int EffectiveMaxTokens(ModelInfo model)
        {
            if (ProviderSettings.MaxTokens > 0) return ProviderSettings.MaxTokens;
            if (model != null && model.MaxOutput > 0)
                return Math.Min(model.MaxOutput, 32768);
            return 0;
        }

/// <summary>Emit a compaction as a visible tool card and run it.</summary>
        private async Task<bool> RunCompactionAsync(
            string assistantId, ModelInfo model, string reason)
        {
            _sse.Emit(_write, "tool.started", new Dictionary<string, object>
            {
                ["message_id"] = assistantId,
                ["tool_name"] = "compact_context",
            });

            var result = await Compaction.CompactAsync(
                _session, _llm, model,
                usage => Accumulate(usage, CostOf(model, usage)), _ct)
                .ConfigureAwait(false);

            _sse.Emit(_write, "tool.completed", new Dictionary<string, object>
            {
                ["message_id"] = assistantId,
                ["tool_name"] = "compact_context",
                ["preview"] = result.Compacted
                    ? "Context compacted (" + reason + "): " + result.Detail
                    : "Compaction skipped (" + reason + "): " + result.Detail,
                ["args"] = null,
            });
            return result.Compacted;
        }

/// <summary>Per-message usage blob (persisted on the assistant row).</summary>
        private static string BuildUsageJson(
            ModelInfo model, LlmUsage usage, double cost, long durationMs)
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(
                new Dictionary<string, object>
                {
                    ["model"] = model != null ? model.Id : null,
                    ["input_tokens"] = usage.InputTokens,
                    ["output_tokens"] = usage.OutputTokens,
                    ["reasoning_tokens"] = usage.ReasoningTokens,
                    ["cache_read_tokens"] = usage.CacheReadTokens,
                    ["total_tokens"] = usage.TotalTokens,
                    ["cost"] = Math.Round(cost, 6),
                    ["duration_ms"] = durationMs,
                });
        }

        private static object UsageJson(
            LlmUsage usage, double cost, long durationMs = -1, string modelId = null)
        {
            if (usage == null) return null;
            var json = new Dictionary<string, object>
            {
                ["input_tokens"] = usage.InputTokens,
                ["output_tokens"] = usage.OutputTokens,
                ["reasoning_tokens"] = usage.ReasoningTokens,
                ["cache_read_tokens"] = usage.CacheReadTokens,
                ["total_tokens"] = usage.TotalTokens,
                ["cost"] = Math.Round(cost, 6),
            };
            if (durationMs >= 0) json["duration_ms"] = durationMs;
            if (modelId != null) json["model"] = modelId;
            return json;
        }

        private static readonly Random Jitter = new Random();

        private static int BackoffWithJitter(int attempt)
        {
            // 1.5s, 3s — ±20% jitter (opencode executor.ts spirit).
            var baseMs = attempt * 1500;
            var delta = (int)(baseMs * 0.2);
            return baseMs + Jitter.Next(-delta, delta + 1);
        }

        private sealed class DeltaSink : ILlmStreamSink
        {
            private readonly Action<string> _emit;
            private readonly Action<string> _emitReasoning;

            public DeltaSink(Action<string> emit, Action<string> emitReasoning)
            {
                _emit = emit;
                _emitReasoning = emitReasoning;
            }

            public void OnTextDelta(string delta)
            {
                try { _emit(delta); }
                catch { /* a dead page must not kill the run */ }
            }

            public void OnReasoningDelta(string delta)
            {
                try { _emitReasoning(delta); }
                catch { /* a dead page must not kill the run */ }
            }
        }
    }
}
