using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AgentOffice.Agent
{
/// <summary>In-proc agent host — replaces the external gateway.</summary>
    internal sealed class AgentHost : IDisposable
    {
        public static AgentHost Current { get; private set; }

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 32 * 1024 * 1024,
        };

        private readonly SessionStore _sessions =
            new SessionStore(Taskpane.HostSettings.DataDirectory);
        private readonly LlmClient _llm = new LlmClient();

/// <summary>Must be called on Excel's main thread (from ThisAddIn).</summary>
        public void Start()
        {
            Current = this;
            ModelRegistry.Warmup(); // background models.dev fetch (non-blocking)
            Bridge.Log.Info(
                "Agent Office in-proc agent ready (provider " +
                (ProviderSettings.Configured
                    ? "configured, model " + ProviderSettings.Model
                    : "NOT configured") +
                ", sessions " + (_sessions.IsPersistent ? "persisted" : "memory-only") + ")");
        }

        public void Dispose()
        {
            Current = null;
            _sessions.Dispose();
        }

        // ==================================================================
        // REST routing (RequestInterceptor → here)
        // ==================================================================

/// <summary>Answer a non-streaming /api/*, /v1/* or /health request in-process.</summary>
        public async Task<KeyValuePair<int, string>> HandleInProcAsync(
            string method, string pathAndQuery, string body)
        {
            string path, query;
            SplitPath(pathAndQuery, out path, out query);

            try
            {
                if (method == "GET" && path == "/health")
                    return Result(200, HandleHealth());
                if (method == "GET" && path == "/v1/toolsets")
                    return Result(200, HandleToolsets());
                if (method == "GET" && path == "/api/settings/provider")
                    return Result(200, HandleGetProvider());
                if ((method == "PUT" || method == "POST") && path == "/api/settings/provider")
                    return Result(200, HandleSaveProvider(body));
                if (method == "GET" && path == "/api/settings/ui")
                    return Result(200, HandleGetUi());
                if ((method == "PUT" || method == "POST") && path == "/api/settings/ui")
                    return Result(200, HandleSaveUi(body));
                if (method == "GET" && path == "/api/settings/provider/models")
                    return await HandleProviderModelsAsync().ConfigureAwait(false);
                if (method == "GET" && path == "/api/sessions")
                    return Result(200, HandleListSessions(query));

                string sessionId, sub;
                if (TryParseSessionRoute(path, out sessionId, out sub))
                {
                    if (sub == null && (method == "GET" || method == "DELETE"))
                    {
                        if (method == "GET")
                        {
                            var session = _sessions.Get(sessionId);
                            return session == null
                                ? Result(404, ErrorJson("Session not found: " + sessionId, "not_found"))
                                : Result(200, HandleGetSession(session));
                        }
                        return Result(200, HandleDeleteSession(sessionId));
                    }
                    if (sub == "messages" && method == "GET")
                    {
                        var session = _sessions.Get(sessionId);
                        return session == null
                            ? Result(404, ErrorJson("Session not found: " + sessionId, "not_found"))
                            : Result(200, HandleGetMessages(session));
                    }
                }

                if (method == "POST" && path == "/api/sessions")
                    return HandleCreateSession(body);

                return Result(404, ErrorJson("Not found: " + method + " " + path, "not_found"));
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("AgentHost failed for " + method + " " + path, ex);
                return Result(500, ErrorJson("Agent error: " + ex.Message, "internal"));
            }
        }

        private static KeyValuePair<int, string> Result(int status, string json)
        {
            return new KeyValuePair<int, string>(status, json);
        }

        // ==================================================================
        // Chat stream (StreamBridge → here)
        // ==================================================================

/// <summary>Pre-flight for POST /api/sessions/{id}/chat/stream.</summary>
        public int ValidateChatStream(string path, out AgentSession session, out string errorJson)
        {
            session = null;
            errorJson = null;

            string sessionId, sub;
            if (!TryParseSessionRoute(path.Split('?')[0], out sessionId, out sub) || sub != "chat/stream")
                return 404;

            session = _sessions.Get(sessionId);
            if (session == null)
            {
                errorJson = ErrorJson("Session not found: " + sessionId, "not_found");
                return 404;
            }

            lock (session.Sync)
            {
                if (session.RunActive)
                {
                    errorJson = ErrorJson(
                        "A run is already active on this session.", "run_active");
                    return 409;
                }
            }

            if (!ProviderSettings.Configured)
            {
                errorJson = ErrorJson(
                    "LLM provider not configured — open Connection settings and enter an API key and model.",
                    "provider_not_configured");
                return 400;
            }
            return 200;
        }

/// <summary>Run one chat turn and emit the full SSE event sequence (run.started → … → done) as UTF-8 frames via .</summary>
        public async Task RunChatStreamAsync(
            AgentSession session, string bodyJson,
            Action<byte[]> write, CancellationToken ct)
        {
            var runId = "run_" + Guid.NewGuid().ToString("N");
            var emitter = new SseEmitter(session.Id, runId);
            lock (session.Sync) { session.RunActive = true; }

            var runner = new AgentRunner(
                session, _llm, emitter, write, ct,
                (s, usage, cost) => _sessions.AddUsage(s, usage, cost));
            try
            {
                // ---- Parse + persist the incoming turn ----
                object messageContent;
                string ephemeralSystem;
                ParseChatBody(bodyJson, out messageContent, out ephemeralSystem);

                AgentMessage userMessage;
                lock (session.Sync)
                {
                    userMessage = session.AppendMessage("user", messageContent);
                }
                runner.TrackUserMessage(userMessage);

                emitter.Emit(write, "run.started", new Dictionary<string, object>
                {
                    ["user_message"] = new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = userMessage.FlattenContent(),
                    },
                });

                // ---- The agent loop (LLM ⇄ tools, multi-step) ----
                await runner.RunAsync().ConfigureAwait(false);

                EmitRunCompleted(write, emitter, session,
                    runner.LastAssistantId, completed: true,
                    usage: runner.TotalUsageJson(), turnMessages: runner.RunMessages);
            }
            catch (OperationCanceledException)
            {
                FlushPartial(session, runner, "cancelled");
                EmitRunCompleted(write, emitter, session,
                    messageId: null, completed: false,
                    usage: runner.TotalUsageJson(), turnMessages: runner.RunMessages);
            }
            catch (Exception ex)
            {
                var message = ex is LlmException ? ex.Message : "Agent error: " + ex.Message;
                if (!(ex is LlmException)) Bridge.Log.Error("Chat run failed", ex);

                FlushPartial(session, runner, "error");
                emitter.Emit(write, "error", new Dictionary<string, object>
                {
                    ["message"] = message,
                });
            }
            finally
            {
                lock (session.Sync) { session.RunActive = false; }
                emitter.Emit(write, "done", new Dictionary<string, object>());
            }
        }

/// <summary>Persist text that streamed but never settled into an assistant message (cancel/failure mid-step).</summary>
        private static void FlushPartial(AgentSession session, AgentRunner runner, string reason)
        {
            var partial = runner.UnpersistedPartialText;
            if (string.IsNullOrEmpty(partial)) return;

            AgentMessage message;
            lock (session.Sync)
            {
                message = session.AppendMessage("assistant", partial, reason);
            }
            runner.TrackUserMessage(message);
        }

        private void EmitRunCompleted(
            Action<byte[]> write, SseEmitter emitter, AgentSession session,
            string messageId, bool completed, object usage,
            System.Collections.Generic.IEnumerable<AgentMessage> turnMessages)
        {
            var messages = new ArrayList();
            foreach (var message in turnMessages)
            {
                if (message != null)
                    messages.Add(AgentSession.MessageToApiJson(session, message));
            }

            emitter.Emit(write, "run.completed", new Dictionary<string, object>
            {
                ["message_id"] = messageId,
                ["completed"] = completed,
                ["messages"] = messages,
                ["usage"] = usage,
                // Footer metrics (opencode-style): the session's running spend
                // and the context window of the currently configured model.
                ["session_cost"] = session.Cost,
                ["context_window"] = ModelRegistry.EffectiveModel().ContextWindow,
            });
        }

/// <summary>Body: { message: string | ChatMessagePart[], system_message?</summary>
        private static void ParseChatBody(
            string bodyJson, out object messageContent, out string ephemeralSystem)
        {
            messageContent = null;
            ephemeralSystem = null;

            Dictionary<string, object> body = null;
            try
            {
                body = string.IsNullOrWhiteSpace(bodyJson)
                    ? null
                    : Json.Deserialize<Dictionary<string, object>>(bodyJson);
            }
            catch { /* handled below as empty message */ }
            if (body == null)
            {
                messageContent = string.Empty;
                return;
            }

            object messageRaw;
            if (body.TryGetValue("message", out messageRaw))
            {
                if (messageRaw is string) messageContent = messageRaw;
                else if (messageRaw is ArrayList) messageContent = messageRaw;
            }
            if (messageContent == null) messageContent = string.Empty;

            object systemRaw;
            if (body.TryGetValue("system_message", out systemRaw) &&
                systemRaw is string && !string.IsNullOrWhiteSpace((string)systemRaw))
            {
                ephemeralSystem = ((string)systemRaw).Trim();
            }
        }

        // ==================================================================
        // Endpoint handlers
        // ==================================================================

        private static string HandleHealth()
        {
            var model = ModelRegistry.EffectiveModel();
            return Json.Serialize(new Dictionary<string, object>
            {
                ["status"] = "ok",
                ["platform"] = "agent-office",
                ["version"] = Bridge.Protocol.BridgeVersion,
                ["host_app"] = AppContext.CurrentId,
                ["configured"] = ProviderSettings.Configured,
                ["image_model"] = ProviderSettings.ImageModel,
                ["model"] = ProviderSettings.Model,
                ["model_context"] = model.ContextWindow,
                ["model_reasoning"] = model.Reasoning,
                ["model_vision"] = model.Vision,
                ["model_modalities_input"] = model.ToApiJson()["modalities_input"],
                ["model_modalities_output"] = model.ToApiJson()["modalities_output"],
            });
        }

/// <summary>GET /api/settings/provider/models — the provider's model list, enriched with catalog metadata (context window, output.</summary>
        private static async Task<KeyValuePair<int, string>> HandleProviderModelsAsync()
        {
            Bridge.Log.Info(
                "GET /api/settings/provider/models (baseUrl: " +
                ProviderSettings.BaseUrl + ")");
            try
            {
                var models = await ModelRegistry.ListProviderModelsAsync().ConfigureAwait(false);
                var data = new ArrayList();
                foreach (var model in models) data.Add(model.ToApiJson());

                return Result(200, Json.Serialize(new Dictionary<string, object>
                {
                    ["object"] = "list",
                    ["data"] = data,
                }));
            }
            catch (Exception ex)
            {
                var message = ex is LlmException ? ex.Message : "Could not load models: " + ex.Message;
                // Log ALL failures (LlmException included) — the picker needs
                // this trail when a provider's /models misbehaves.
                Bridge.Log.Error("Provider /models failed: " + message, ex);
                return Result(502, ErrorJson(message, "models_unavailable"));
            }
        }

        private static string HandleToolsets()
        {
            // All three hosts have toolsets now (Excel 34, Word 7, PowerPoint 11).
            var names = Bridge.BridgeHost.Current != null
                ? Bridge.BridgeHost.Current.ActionNames
                : new string[0];
            return Json.Serialize(new Dictionary<string, object>
            {
                ["object"] = "list",
                ["data"] = new ArrayList
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = AppContext.CurrentId,
                        ["enabled"] = true,
                        ["configured"] = true,
                        ["tools"] = new ArrayList(names),
                    },
                },
            });
        }

        private static string HandleGetProvider()
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                ["object"] = "agentoffice.provider.settings",
                ["provider"] = ProviderSettings.ToApiJson(),
            });
        }

        private static string HandleSaveProvider(string body)
        {
            var parsed = !string.IsNullOrWhiteSpace(body)
                ? Json.Deserialize<Dictionary<string, object>>(body)
                : new Dictionary<string, object>();

            ProviderSettings.Save(
                GetStringOrNull(parsed, "apiKey"),
                GetStringOrNull(parsed, "baseUrl"),
                GetStringOrNull(parsed, "model"),
                GetIntOrNull(parsed, "contextWindow"),
                GetIntOrNull(parsed, "maxTokens"),
                GetIntOrNull(parsed, "maxSteps"),
                GetStringOrNull(parsed, "imageModel"),
                GetStringOrNull(parsed, "imageBaseUrl"),
                GetStringOrNull(parsed, "imageApiKey"),
                GetStringOrNull(parsed, "imageProvider"));

            Bridge.Log.Info("Provider settings updated (model: " +
                (ProviderSettings.Model ?? "<none>") + ", baseUrl: " +
                ProviderSettings.BaseUrl + ")");
            // The new provider's pricing lives in its /models response —
            // refresh the run path's cache so step costs compute correctly.
            _ = ModelRegistry.RefreshProviderCacheAsync();
            return HandleGetProvider();
        }

/// <summary>GET /api/settings/ui — UI language and user-defined translation overrides.</summary>
        private static string HandleGetUi()
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                ["object"] = "agentoffice.ui.settings",
                ["ui"] = new Dictionary<string, object>
                {
                    ["language"] = Taskpane.HostSettings.Language ?? string.Empty,
                    ["translations"] = Taskpane.HostSettings.Translations,
                },
            });
        }

/// <summary>PUT /api/settings/ui — persist the language choice (empty = automatic).</summary>
        private static string HandleSaveUi(string body)
        {
            var parsed = !string.IsNullOrWhiteSpace(body)
                ? Json.Deserialize<Dictionary<string, object>>(body)
                : new Dictionary<string, object>();
            var language = GetStringOrNull(parsed, "language");
            if (language != null) Taskpane.HostSettings.SaveLanguage(language);
            return HandleGetUi();
        }

        private string HandleListSessions(string query)
        {
            int limit, offset;
            ParsePaging(query, out limit, out offset);

            var page = _sessions.List(limit, offset);
            var data = new ArrayList();
            foreach (var session in page) data.Add(session.ToApiJson());

            return Json.Serialize(new Dictionary<string, object>
            {
                ["object"] = "list",
                ["data"] = data,
                ["limit"] = limit,
                ["offset"] = offset,
                ["has_more"] = offset + page.Count < _sessions.Count,
            });
        }

        private KeyValuePair<int, string> HandleCreateSession(string body)
        {
            var parsed = !string.IsNullOrWhiteSpace(body)
                ? Json.Deserialize<Dictionary<string, object>>(body)
                : new Dictionary<string, object>();

            var session = _sessions.Create(
                GetStringOrNull(parsed, "id"),
                GetStringOrNull(parsed, "model") ?? ProviderSettings.Model,
                GetStringOrNull(parsed, "system_prompt"),
                GetStringOrNull(parsed, "title"));

            if (session == null)
            {
                return Result(409, Json.Serialize(new Dictionary<string, object>
                {
                    ["message"] = "Session already exists",
                    ["error"] = "session_exists",
                }));
            }

            return Result(201, Json.Serialize(new Dictionary<string, object>
            {
                ["object"] = "agentoffice.session",
                ["session"] = session.ToApiJson(),
            }));
        }

        private static string HandleGetSession(AgentSession session)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                ["object"] = "agentoffice.session",
                ["session"] = session.ToApiJson(),
            });
        }

        private string HandleDeleteSession(string sessionId)
        {
            var deleted = _sessions.Delete(sessionId);
            return Json.Serialize(new Dictionary<string, object>
            {
                ["object"] = "agentoffice.session.deleted",
                ["id"] = sessionId,
                ["deleted"] = deleted,
            });
        }

        private static string HandleGetMessages(AgentSession session)
        {
            var data = new ArrayList();
            lock (session.Sync)
            {
                foreach (var message in session.Messages)
                    data.Add(AgentSession.MessageToApiJson(session, message));
            }

            return Json.Serialize(new Dictionary<string, object>
            {
                ["object"] = "list",
                ["session_id"] = session.Id,
                ["data"] = data,
            });
        }

        // ==================================================================
        // Small helpers
        // ==================================================================

        private static void SplitPath(string pathAndQuery, out string path, out string query)
        {
            var q = (pathAndQuery ?? string.Empty).IndexOf('?');
            if (q < 0)
            {
                path = pathAndQuery ?? "/";
                query = string.Empty;
            }
            else
            {
                path = pathAndQuery.Substring(0, q);
                query = pathAndQuery.Substring(q + 1);
            }
        }

/// <summary>/api/sessions/{id}[/{sub}] — sub is null for the bare session route, or the trailing segment ("messages",</summary>
        private static bool TryParseSessionRoute(string path, out string sessionId, out string sub)
        {
            sessionId = null;
            sub = null;
            const string prefix = "/api/sessions/";
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var rest = path.Substring(prefix.Length);
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                sessionId = Uri.UnescapeDataString(rest);
            }
            else
            {
                sessionId = Uri.UnescapeDataString(rest.Substring(0, slash));
                sub = rest.Substring(slash + 1);
            }
            return sessionId.Length > 0;
        }

        private static void ParsePaging(string query, out int limit, out int offset)
        {
            limit = 50;
            offset = 0;
            if (string.IsNullOrEmpty(query)) return;

            foreach (var pair in query.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = pair.Substring(0, eq);
                var value = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                int parsed;
                if (key == "limit" && int.TryParse(value, out parsed) && parsed > 0)
                    limit = Math.Min(parsed, 200);
                else if (key == "offset" && int.TryParse(value, out parsed) && parsed >= 0)
                    offset = parsed;
            }
        }

        private static string GetStringOrNull(Dictionary<string, object> dict, string key)
        {
            object raw;
            if (dict.TryGetValue(key, out raw) && raw is string)
            {
                var value = ((string)raw).Trim();
                if (value.Length > 0) return value;
            }
            return null;
        }

        private static int? GetIntOrNull(Dictionary<string, object> dict, string key)
        {
            object raw;
            if (!dict.TryGetValue(key, out raw) || raw == null) return null;
            if (raw is int) return (int)raw;
            if (raw is long) return (int)(long)raw;
            return null;
        }

        private static string ErrorJson(string message, string code)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                ["message"] = message,
                ["error"] = code,
            });
        }
    }
}
