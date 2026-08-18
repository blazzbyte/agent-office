using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace AgentOffice.Agent
{
/// <summary>SSE framing for a single chat run: stamps every payload with session_id / run_id / monotonic seq /.</summary>
    internal sealed class SseEmitter
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 32 * 1024 * 1024,
        };

        private readonly string _sessionId;
        private readonly string _runId;
        private int _seq;

        public SseEmitter(string sessionId, string runId)
        {
            _sessionId = sessionId;
            _runId = runId;
        }

        public void Emit(
            Action<byte[]> write, string eventName, Dictionary<string, object> payload)
        {
            payload["session_id"] = _sessionId;
            payload["run_id"] = _runId;
            payload["seq"] = _seq++;
            payload["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var frame = "event: " + eventName + "\ndata: " +
                Json.Serialize(payload) + "\n\n";
            write(Encoding.UTF8.GetBytes(frame));
        }
    }
}
