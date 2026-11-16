using System.Collections;
using System.Collections.Generic;
using System.Text;
using AgentOffice.Agent;

namespace AgentOffice.Tests
{
    internal static class AgentTests
    {
        internal static void Run()
        {
            var frames = new List<string>();
            var sse = new SseEmitter("s1", "r1");
            sse.Emit(b => frames.Add(Encoding.UTF8.GetString(b)), "delta",
                new Dictionary<string, object> { ["text"] = "hi" });
            sse.Emit(b => frames.Add(Encoding.UTF8.GetString(b)), "done",
                new Dictionary<string, object>());

            Program.True("frame header", frames[0].StartsWith("event: delta\ndata: {"));
            Program.True("frame terminator", frames[0].EndsWith("}\n\n"));
            Program.True("frame keeps payload", frames[0].Contains("\"text\":\"hi\""));
            Program.True("frame is stamped",
                frames[0].Contains("\"session_id\":\"s1\"") && frames[0].Contains("\"run_id\":\"r1\""));
            Program.True("seq starts at zero", frames[0].Contains("\"seq\":0"));
            Program.True("seq increments", frames[1].Contains("\"seq\":1"));

            var list = new ArrayList { 1, 2 };
            Program.True("ArrayList passes through", ReferenceEquals(list, JsonCompat.ToArrayList(list)));
            Program.Equal("array is wrapped", 3, JsonCompat.ToArrayList(new object[] { 1, 2, 3 }).Count);
            Program.Equal("other shapes rejected", null, JsonCompat.ToArrayList("x"));
        }
    }
}
