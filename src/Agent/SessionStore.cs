using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using AgentOffice.Agent.Storage;

namespace AgentOffice.Agent
{
/// <summary>One stored message.</summary>
    internal sealed class AgentMessage
    {
        public string Id;                 // "msg_<n>" (per-session sequence)
        public string Role;               // user | assistant | tool
        public object Content;            // string | ArrayList of parts
        public string ToolCallId;
        public object ToolCalls;          // ArrayList of OpenAI tool_call dicts
        public string ToolName;
#pragma warning restore CS0649
        public string FinishReason;
/// <summary>Assistant usage+cost blob (JSON) — set on persisted steps.</summary>
        public string UsageJson;
        public DateTime Timestamp;        // UTC

/// <summary>Text-only rendering (UI display + run.started payload).</summary>
        public string FlattenContent()
        {
            var parts = Content as ArrayList;
            if (parts == null) return Content as string ?? string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                var dict = part as Dictionary<string, object>;
                if (dict == null) continue;
                object typeRaw;
                var type = dict.TryGetValue("type", out typeRaw)
                    ? typeRaw as string : null;
                if (type == "text")
                {
                    object textRaw;
                    if (dict.TryGetValue("text", out textRaw) && textRaw is string)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append((string)textRaw);
                    }
                }
                else if (type == "image_url")
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("[image]");
                }
            }
            return sb.ToString();
        }
    }

/// <summary>One chat session.</summary>
    internal sealed class AgentSession
    {
        public string Id;                 // "ses_<guid32>"
        public string Title;
        public string Model;
        public string SystemPrompt;
        public DateTime CreatedAt;        // UTC
        public DateTime LastActiveAt;     // UTC
        public int InputTokens;
        public int OutputTokens;
/// <summary>Accumulated estimated spend (USD) across all runs.</summary>
        public double Cost;
        public readonly List<AgentMessage> Messages = new List<AgentMessage>();

/// <summary>True while a chat stream runs on this session (one at a time).</summary>
        public bool RunActive;

/// <summary>Images the model generated or found during runs (data URLs), newest last, rolling-capped.</summary>
        public sealed class GeneratedImage
        {
            public string DataUrl;
            public string Attribution; // null for model-generated images
        }

        public readonly List<GeneratedImage> GeneratedImages = new List<GeneratedImage>();
        internal const int MaxGeneratedImages = 8;

/// <summary>Store that persists mutations; null only in unit-less use.</summary>
        internal SessionStore Owner;

        private int _nextMessageNumber = 1;

/// <summary>Serializes message appends + id assignment per session.</summary>
        public readonly object Sync = new object();

/// <summary>The id the next appended message will get (caller holds Sync).</summary>
        public string PeekNextMessageId()
        {
            return "msg_" + _nextMessageNumber;
        }

        public AgentMessage AppendMessage(
            string role, object content, string finishReason = null,
            object toolCalls = null, string usageJson = null)
        {
            // Caller holds Sync.
            var seq = _nextMessageNumber++;
            var message = new AgentMessage
            {
                Id = "msg_" + seq,
                Role = role,
                Content = content,
                ToolCalls = toolCalls,
                FinishReason = finishReason,
                UsageJson = usageJson,
                Timestamp = DateTime.UtcNow,
            };
            Messages.Add(message);
            LastActiveAt = message.Timestamp;
            if (Title == null && role == "user") MaybeAutoTitle(message);
            if (Owner != null) Owner.OnMessageAppended(this, message, seq);
            return message;
        }

/// <summary>First user message becomes the session title (deterministic, zero tokens — no LLM summarization).</summary>
        private const int AutoTitleMaxLength = 48;

        private void MaybeAutoTitle(AgentMessage message)
        {
            var text = message.FlattenContent();
            if (string.IsNullOrWhiteSpace(text)) return;
            var oneLine = System.Text.RegularExpressions.Regex.Replace(
                text.Trim(), @"\s+", " ");
            Title = oneLine.Length <= AutoTitleMaxLength
                ? oneLine
                : oneLine.Substring(0, AutoTitleMaxLength).TrimEnd() + "…";
        }

/// <summary>Append a tool result message (Phase 4 runner).</summary>
        public AgentMessage AppendToolMessage(string toolCallId, string toolName, object content)
        {
            // Caller holds Sync.
            var seq = _nextMessageNumber++;
            var message = new AgentMessage
            {
                Id = "msg_" + seq,
                Role = "tool",
                Content = content,
                ToolCallId = toolCallId,
                ToolName = toolName,
                Timestamp = DateTime.UtcNow,
            };
            Messages.Add(message);
            LastActiveAt = message.Timestamp;
            if (Owner != null) Owner.OnMessageAppended(this, message, seq);
            return message;
        }

/// <summary>Rebuild a message from storage without re-persisting it.</summary>
        internal AgentMessage LoadMessage(
            int seq, string role, object content, string toolCallId,
            object toolCalls, string toolName, string finishReason, DateTime timestamp,
            string usageJson = null)
        {
            var message = new AgentMessage
            {
                Id = "msg_" + seq,
                Role = role,
                Content = content,
                ToolCallId = toolCallId,
                ToolCalls = toolCalls,
                ToolName = toolName,
                FinishReason = finishReason,
                UsageJson = usageJson,
                Timestamp = timestamp,
            };
            Messages.Add(message);
            if (timestamp > LastActiveAt) LastActiveAt = timestamp;
            if (seq >= _nextMessageNumber) _nextMessageNumber = seq + 1;
            return message;
        }

        public Dictionary<string, object> ToApiJson()
        {
            string preview = null;
            lock (Sync)
            {
                var firstUser = Messages.FirstOrDefault(m => m.Role == "user");
                if (firstUser != null)
                {
                    var text = firstUser.FlattenContent();
                    preview = text.Length <= 80 ? text : text.Substring(0, 80) + "…";
                }
            }

            int messageCount;
            lock (Sync) { messageCount = Messages.Count; }

            return new Dictionary<string, object>
            {
                ["id"] = Id,
                ["source"] = "agent-office",
                ["model"] = Model,
                ["title"] = Title,
                ["started_at"] = Iso(CreatedAt),
                ["last_active"] = Iso(LastActiveAt),
                ["message_count"] = messageCount,
                ["input_tokens"] = InputTokens,
                ["output_tokens"] = OutputTokens,
                ["cost"] = Cost,
                ["preview"] = preview,
                ["has_system_prompt"] = !string.IsNullOrEmpty(SystemPrompt),
                ["has_model_config"] = false,
            };
        }

        public static Dictionary<string, object> MessageToApiJson(AgentSession session, AgentMessage message)
        {
            var json = new Dictionary<string, object>
            {
                ["id"] = message.Id,
                ["session_id"] = session.Id,
                ["role"] = message.Role,
                ["content"] = message.FlattenContent(),
                ["timestamp"] = Iso(message.Timestamp),
            };
            var parts = message.Content as ArrayList;
            if (parts != null) json["content_parts"] = parts;
            if (message.ToolCallId != null) json["tool_call_id"] = message.ToolCallId;
            if (message.ToolCalls != null) json["tool_calls"] = message.ToolCalls;
            if (message.ToolName != null) json["tool_name"] = message.ToolName;
            if (message.FinishReason != null) json["finish_reason"] = message.FinishReason;
            if (message.UsageJson != null)
            {
                try
                {
                    json["usage"] = new JavaScriptSerializer().Deserialize<object>(message.UsageJson);
                }
                catch { /* corrupt blob — omit the field, keep the message */ }
            }
            return json;
        }

        public static string Iso(DateTime utc)
        {
            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        public static DateTime ParseIso(string value)
        {
            DateTime parsed;
            if (DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm:ss.fffZ",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed))
            {
                return parsed;
            }
            return DateTime.UtcNow;
        }
    }

/// <summary>Session registry: in-memory cache as the read path, SQLite (%LOCALAPPDATA%\AgentOffice\agent.db, inbox winsqlite3) as.</summary>
    internal sealed class SessionStore : IDisposable
    {
        private const int SchemaVersion = 2;

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            // Image parts (capture_range data URLs) ride inside content.
            MaxJsonLength = 64 * 1024 * 1024,
        };

        private readonly Dictionary<string, AgentSession> _sessions =
            new Dictionary<string, AgentSession>(StringComparer.Ordinal);
        private readonly object _sync = new object();
        private readonly SqliteDb _db;    // null → memory-only fallback

        public SessionStore(string dataDirectory)
        {
            SqliteDb db = null;
            try
            {
                var path = System.IO.Path.Combine(dataDirectory, "agent.db");
                db = SqliteDb.Open(path);
                InitializeSchema(db);
                _db = db;
                LoadAll();
                Bridge.Log.Info("Session store: SQLite at " + path +
                    " (" + _sessions.Count + " session(s) loaded)");
            }
            catch (Exception ex)
            {
                if (db != null) { try { db.Dispose(); } catch { /* best effort */ } }
                Bridge.Log.Error(
                    "Session store: SQLite unavailable, sessions are memory-only this run", ex);
            }
        }

/// <summary>False when running in the degraded memory-only mode.</summary>
        public bool IsPersistent { get { return _db != null; } }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_db != null) _db.Dispose();
            }
        }

        // ==================================================================
        // Public API (unchanged from the in-memory Phase 2 store)
        // ==================================================================

        public AgentSession Create(string requestedId, string model, string systemPrompt, string title)
        {
            lock (_sync)
            {
                var id = !string.IsNullOrWhiteSpace(requestedId)
                    ? requestedId.Trim()
                    : "ses_" + Guid.NewGuid().ToString("N");
                if (_sessions.ContainsKey(id))
                    return null; // 409 session_exists

                var now = DateTime.UtcNow;
                var session = new AgentSession
                {
                    Id = id,
                    Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                    Model = model,
                    SystemPrompt = systemPrompt,
                    CreatedAt = now,
                    LastActiveAt = now,
                    Owner = this,
                };

                if (_db != null)
                {
                    try
                    {
                        using (var stmt = _db.Prepare(
                            "INSERT INTO session (id, title, model, system_prompt," +
                            " created_at, last_active_at, input_tokens, output_tokens)" +
                            " VALUES (?1, ?2, ?3, ?4, ?5, ?6, 0, 0)"))
                        {
                            stmt.Bind(1, session.Id)
                                .Bind(2, session.Title)
                                .Bind(3, session.Model)
                                .Bind(4, session.SystemPrompt)
                                .Bind(5, AgentSession.Iso(session.CreatedAt))
                                .Bind(6, AgentSession.Iso(session.LastActiveAt));
                            stmt.Step();
                        }
                    }
                    catch (Exception ex)
                    {
                        Bridge.Log.Error("Session row insert failed (kept in memory)", ex);
                    }
                }

                _sessions[id] = session;
                return session;
            }
        }

        public AgentSession Get(string id)
        {
            lock (_sync)
            {
                AgentSession session;
                return id != null && _sessions.TryGetValue(id, out session)
                    ? session : null;
            }
        }

        public bool Delete(string id)
        {
            lock (_sync)
            {
                if (id == null || !_sessions.Remove(id)) return false;

                if (_db != null)
                {
                    try
                    {
                        using (var stmt = _db.Prepare("DELETE FROM message WHERE session_id = ?1"))
                        {
                            stmt.Bind(1, id);
                            stmt.Step();
                        }
                        using (var stmt = _db.Prepare("DELETE FROM session WHERE id = ?1"))
                        {
                            stmt.Bind(1, id);
                            stmt.Step();
                        }
                    }
                    catch (Exception ex)
                    {
                        Bridge.Log.Error("Session row delete failed", ex);
                    }
                }
                return true;
            }
        }

/// <summary>Most-recently-active first, paginated.</summary>
        public List<AgentSession> List(int limit, int offset)
        {
            lock (_sync)
            {
                return _sessions.Values
                    .OrderByDescending(s => s.LastActiveAt)
                    .Skip(Math.Max(0, offset))
                    .Take(Math.Max(1, limit))
                    .ToList();
            }
        }

        public int Count
        {
            get { lock (_sync) { return _sessions.Count; } }
        }

        // ==================================================================
        // Write-through hooks (called by AgentSession / AgentHost)
        // ==================================================================

/// <summary>Persist an appended message + bump the session's last_active_at.</summary>
        internal void OnMessageAppended(AgentSession session, AgentMessage message, int seq)
        {
            if (_db == null) return;
            lock (_sync)
            {
                try
                {
                    string contentText, contentType;
                    EncodeContent(message.Content, out contentText, out contentType);

                    using (var stmt = _db.Prepare(
                        "INSERT INTO message (session_id, seq, role, content, content_type," +
                        " tool_call_id, tool_calls, tool_name, finish_reason, created_at, usage)" +
                        " VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)"))
                    {
                        stmt.Bind(1, session.Id)
                            .Bind(2, (long)seq)
                            .Bind(3, message.Role)
                            .Bind(4, contentText)
                            .Bind(5, contentType)
                            .Bind(6, message.ToolCallId)
                            .Bind(7, message.ToolCalls != null
                                ? Json.Serialize(message.ToolCalls) : (string)null)
                            .Bind(8, message.ToolName)
                            .Bind(9, message.FinishReason)
                            .Bind(10, AgentSession.Iso(message.Timestamp))
                            .Bind(11, message.UsageJson);
                        stmt.Step();
                    }
                    using (var stmt = _db.Prepare(
                        "UPDATE session SET last_active_at = ?2, title = ?3 WHERE id = ?1"))
                    {
                        stmt.Bind(1, session.Id)
                            .Bind(2, AgentSession.Iso(session.LastActiveAt))
                            .Bind(3, session.Title);
                        stmt.Step();
                    }
                }
                catch (Exception ex)
                {
                    Bridge.Log.Error("Message row insert failed (kept in memory)", ex);
                }
            }
        }

/// <summary>Accumulate token usage + spend on the session object + its row.</summary>
        public void AddUsage(AgentSession session, LlmUsage usage, double cost)
        {
            lock (session.Sync)
            {
                session.InputTokens += usage != null ? usage.InputTokens : 0;
                session.OutputTokens += usage != null ? usage.OutputTokens : 0;
                if (cost != 0) session.Cost += cost;
            }

            if (_db == null) return;
            lock (_sync)
            {
                try
                {
                    using (var stmt = _db.Prepare(
                        "UPDATE session SET input_tokens = ?2, output_tokens = ?3, cost = ?4" +
                        " WHERE id = ?1"))
                    {
                        stmt.Bind(1, session.Id)
                            .Bind(2, (long)session.InputTokens)
                            .Bind(3, (long)session.OutputTokens)
                            .Bind(4, session.Cost);
                        stmt.Step();
                    }
                }
                catch (Exception ex)
                {
                    Bridge.Log.Error("Session usage update failed", ex);
                }
            }
        }

        // ==================================================================
        // SQLite plumbing
        // ==================================================================

        private static void InitializeSchema(SqliteDb db)
        {
            db.Exec("PRAGMA journal_mode = WAL;");
            db.Exec("PRAGMA busy_timeout = 3000;");
            db.Exec("PRAGMA foreign_keys = ON;");

            var version = 0;
            using (var stmt = db.Prepare("PRAGMA user_version"))
            {
                if (stmt.Step()) version = (int)stmt.ColInt64(0);
            }

            if (version < 1)
            {
                // v1 — initial schema. Message id is derived ("msg_" + seq);
                // content holds raw text or JSON parts per content_type.
                db.Exec(
                    "CREATE TABLE IF NOT EXISTS session (" +
                    "  id TEXT PRIMARY KEY," +
                    "  title TEXT," +
                    "  model TEXT," +
                    "  system_prompt TEXT," +
                    "  created_at TEXT NOT NULL," +
                    "  last_active_at TEXT NOT NULL," +
                    "  input_tokens INTEGER NOT NULL DEFAULT 0," +
                    "  output_tokens INTEGER NOT NULL DEFAULT 0" +
                    ");" +
                    "CREATE TABLE IF NOT EXISTS message (" +
                    "  session_id TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE," +
                    "  seq INTEGER NOT NULL," +
                    "  role TEXT NOT NULL," +
                    "  content TEXT," +
                    "  content_type TEXT NOT NULL DEFAULT 'text'," +
                    "  tool_call_id TEXT," +
                    "  tool_calls TEXT," +
                    "  tool_name TEXT," +
                    "  finish_reason TEXT," +
                    "  created_at TEXT NOT NULL," +
                    "  PRIMARY KEY (session_id, seq)" +
                    ");");
            }
            if (version < 2)
            {
                // v2 — Phase 5 context management: per-session spend +
                // per-message usage blob (footer metrics, cost accounting).
                db.Exec(
                    "ALTER TABLE session ADD COLUMN cost REAL NOT NULL DEFAULT 0;" +
                    "ALTER TABLE message ADD COLUMN usage TEXT;");
            }
            if (version != SchemaVersion)
                db.Exec("PRAGMA user_version = " + SchemaVersion + ";");
        }

/// <summary>Eagerly load every session + message into the cache.</summary>
        private void LoadAll()
        {
            using (var stmt = _db.Prepare(
                "SELECT id, title, model, system_prompt, created_at, last_active_at," +
                " input_tokens, output_tokens, cost FROM session"))
            {
                while (stmt.Step())
                {
                    var session = new AgentSession
                    {
                        Id = stmt.ColText(0),
                        Title = stmt.ColText(1),
                        Model = stmt.ColText(2),
                        SystemPrompt = stmt.ColText(3),
                        CreatedAt = AgentSession.ParseIso(stmt.ColText(4)),
                        LastActiveAt = AgentSession.ParseIso(stmt.ColText(5)),
                        InputTokens = (int)stmt.ColInt64(6),
                        OutputTokens = (int)stmt.ColInt64(7),
                        Cost = stmt.ColDouble(8),
                        Owner = this,
                    };
                    _sessions[session.Id] = session;
                }
            }

            using (var stmt = _db.Prepare(
                "SELECT session_id, seq, role, content, content_type, tool_call_id," +
                " tool_calls, tool_name, finish_reason, created_at, usage" +
                " FROM message ORDER BY session_id, seq"))
            {
                while (stmt.Step())
                {
                    AgentSession session;
                    if (!_sessions.TryGetValue(stmt.ColText(0), out session))
                        continue; // orphan row — ignore

                    var contentText = stmt.ColText(3);
                    var contentType = stmt.ColText(4) ?? "text";
                    object content = contentText;
                    if (contentType == "parts" && contentText != null)
                    {
                        // Deserialize<object> yields object[] for root arrays —
                        // normalize to ArrayList so downstream consumers
                        // (FlattenContent, vision sanitize) work as in memory.
                        try
                        {
                            var parsedContent = JsonCompat.ToArrayList(
                                Json.Deserialize<object>(contentText));
                            content = (object)parsedContent ?? contentText;
                        }
                        catch { content = contentText; }
                    }

                    object toolCalls = null;
                    var toolCallsText = stmt.ColText(6);
                    if (toolCallsText != null)
                    {
                        try
                        {
                            toolCalls = JsonCompat.ToArrayList(
                                Json.Deserialize<object>(toolCallsText));
                        }
                        catch { /* keep null */ }
                    }

                    session.LoadMessage(
                        (int)stmt.ColInt64(1), stmt.ColText(2), content,
                        stmt.ColText(5), toolCalls, stmt.ColText(7), stmt.ColText(8),
                        AgentSession.ParseIso(stmt.ColText(9)), stmt.ColText(10));
                }
            }
        }

        private static void EncodeContent(object content, out string text, out string type)
        {
            if (content == null || content is string)
            {
                text = (string)content;
                type = "text";
            }
            else
            {
                text = Json.Serialize(content);
                type = "parts";
            }
        }
    }
}
