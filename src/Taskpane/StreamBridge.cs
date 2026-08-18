using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace AgentOffice.Taskpane
{
/// <summary>SSE streaming bridge over chrome.webview.postMessage.</summary>
    internal sealed class StreamBridge
    {
/// <summary>Fetch shim injected before any page script.</summary>
        public const string InjectScript =
@"(function () {
  if (!window.chrome || !window.chrome.webview || !window.chrome.webview.postMessage) return;
  var STREAM_RE = /\/api\/sessions\/[^\/?]+\/chat\/stream$/;
  var pending = new Map();
  var nativeFetch = window.fetch.bind(window);

  window.chrome.webview.addEventListener('message', function (ev) {
    var m = ev.data;
    if (!m || typeof m.id !== 'string' || typeof m.type !== 'string') return;
    if (m.type.indexOf('agentoffice.stream.') !== 0) return;
    var p = pending.get(m.id);
    if (!p) return;
    if (m.type === 'agentoffice.stream.head') {
      var h = new Headers();
      h.set('Content-Type', m.contentType || 'text/event-stream');
      p.resolved = true;
      p.resolve(new Response(p.stream, {
        status: m.status,
        statusText: m.status === 200 ? 'OK' : 'Error',
        headers: h
      }));
    } else if (m.type === 'agentoffice.stream.data') {
      var bin = atob(m.b64);
      var bytes = new Uint8Array(bin.length);
      for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      try { p.controller.enqueue(bytes); } catch (e) {}
    } else if (m.type === 'agentoffice.stream.end') {
      try { p.controller.close(); } catch (e) {}
      pending.delete(m.id);
    } else if (m.type === 'agentoffice.stream.error') {
      try { p.controller.error(new Error(m.message || 'stream failed')); } catch (e) {}
      pending.delete(m.id);
    }
  });

  window.fetch = function (input, init) {
    var url = typeof input === 'string' ? input : (input && input.url) || '';
    var method = ((init && init.method) || (typeof input !== 'string' && input && input.method) || 'GET').toUpperCase();
    var abs;
    try { abs = new URL(url, window.location.href); } catch (e) { return nativeFetch(input, init); }
    if (method !== 'POST' || !STREAM_RE.test(abs.pathname)) return nativeFetch(input, init);

    return new Promise(function (resolve, reject) {
      var id = 'hs' + Date.now().toString(36) + Math.random().toString(36).slice(2);
      var entry = { resolve: resolve, reject: reject, controller: null, stream: null, resolved: false };
      entry.stream = new ReadableStream({
        start: function (c) { entry.controller = c; },
        cancel: function () {
          pending.delete(id);
          try { window.chrome.webview.postMessage({ type: 'agentoffice.stream.cancel', id: id }); } catch (e) {}
        }
      });
      pending.set(id, entry);

      function onAbort() {
        if (!pending.has(id)) return;
        pending.delete(id);
        try { window.chrome.webview.postMessage({ type: 'agentoffice.stream.cancel', id: id }); } catch (e) {}
        if (!entry.resolved) reject(new DOMException('The operation was aborted.', 'AbortError'));
        else { try { entry.controller.error(new Error('aborted')); } catch (e) {} }
      }
      if (init && init.signal) {
        if (init.signal.aborted) { pending.delete(id); reject(new DOMException('The operation was aborted.', 'AbortError')); return; }
        init.signal.addEventListener('abort', onAbort);
      }

      try {
        var headers = (init && init.headers) || {};
        window.chrome.webview.postMessage({
          type: 'agentoffice.stream.start',
          id: id,
          path: abs.pathname + abs.search,
          authorization: headers['Authorization'] || headers['authorization'] || null,
          contentType: headers['Content-Type'] || headers['content-type'] || 'application/json',
          body: (init && typeof init.body === 'string') ? init.body : null
        });
      } catch (e) {
        pending.delete(id);
        reject(e);
      }
    });
  };
})();";

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private readonly Control _marshaller;
        private readonly ConcurrentDictionary<string, StreamHandle> _streams =
            new ConcurrentDictionary<string, StreamHandle>();
        private CoreWebView2 _core;

        private sealed class StreamHandle
        {
            public CancellationTokenSource Cts;
        }

        public StreamBridge(Control marshaller)
        {
            _marshaller = marshaller;
        }

        public void Attach(CoreWebView2 core)
        {
            _core = core;
            core.WebMessageReceived += OnWebMessageReceived;
        }

/// <summary>Abort every in-flight stream (taskpane shutdown).</summary>
        public void CancelAll()
        {
            foreach (var kvp in _streams)
            {
                try { kvp.Value.Cts.Cancel(); } catch { /* ignore */ }
            }
            _streams.Clear();
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try
            {
                msg = Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
            }
            catch
            {
                return; // Not JSON / not ours.
            }
            if (msg == null) return;

            var type = Get(msg, "type");
            var id = Get(msg, "id");
            if (type == null || id == null) return;
            if (!type.StartsWith("agentoffice.stream.", StringComparison.Ordinal)) return;

            if (type == "agentoffice.stream.cancel")
            {
                StreamHandle h;
                if (_streams.TryGetValue(id, out h))
                {
                    try { h.Cts.Cancel(); } catch { /* ignore */ }
                }
                return;
            }
            if (type != "agentoffice.stream.start") return;

            var path = Get(msg, "path");
            if (string.IsNullOrEmpty(path)) return;

            var handle = new StreamHandle { Cts = new CancellationTokenSource() };
            _streams[id] = handle;
            Task.Run(() => PumpAsync(
                id, path, Get(msg, "authorization"), Get(msg, "contentType"),
                Get(msg, "body"), handle));
        }

        private async Task PumpAsync(
            string id, string path, string authorization, string contentTypeHeader,
            string bodyText, StreamHandle handle)
        {
            var ct = handle.Cts.Token;
            var headSent = false;
            try
            {
                var host = Agent.AgentHost.Current;
                if (host == null)
                {
                    SendSyntheticError(id, 503,
                        "{\"message\":\"Agent host not initialized\",\"error\":\"no_host\"}");
                    return;
                }
                Agent.AgentSession session;
                string errorJson;
                var status = host.ValidateChatStream(path, out session, out errorJson);
                if (status != 200)
                {
                    SendSyntheticError(id, status, errorJson);
                    return;
                }

                Post(BuildHead(id, 200, "text/event-stream"));
                headSent = true;
                await host.RunChatStreamAsync(
                        session, bodyText,
                        bytes => Post(
                            "{\"type\":\"agentoffice.stream.data\",\"id\":\"" + Escape(id) +
                            "\",\"b64\":\"" + Convert.ToBase64String(bytes) + "\"}"),
                        ct)
                    .ConfigureAwait(false);

                Post("{\"type\":\"agentoffice.stream.end\",\"id\":\"" + Escape(id) + "\"}");
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    // Page cancelled the stream — nothing to report.
                }
                else if (headSent)
                {
                    Post("{\"type\":\"agentoffice.stream.error\",\"id\":\"" + Escape(id) +
                         "\",\"message\":\"" + Escape(ex.Message) + "\"}");
                }
                else
                {
                    SendSyntheticError(id, 500,
                        "{\"message\":\"" + Escape(ex.Message) +
                        "\",\"error\":\"agent_error\"}");
                }
            }
            finally
            {
                StreamHandle done;
                _streams.TryRemove(id, out done);
            }
        }

/// <summary>Send a synthetic HTTP error framed as head + body + end.</summary>
        private void SendSyntheticError(string id, int status, string json)
        {
            Post(BuildHead(id, status, "application/json"));
            Post("{\"type\":\"agentoffice.stream.data\",\"id\":\"" + Escape(id) +
                 "\",\"b64\":\"" + Convert.ToBase64String(
                     Encoding.UTF8.GetBytes(json)) + "\"}");
            Post("{\"type\":\"agentoffice.stream.end\",\"id\":\"" + Escape(id) + "\"}");
        }

        private static string BuildHead(string id, int status, string contentType)
        {
            return "{\"type\":\"agentoffice.stream.head\",\"id\":\"" + Escape(id) +
                   "\",\"status\":" + status +
                   ",\"contentType\":\"" + Escape(contentType ?? "application/octet-stream") + "\"}";
        }

/// <summary>PostWebMessageAsJson must run on the UI thread.</summary>
        private void Post(string json)
        {
            try
            {
                if (_marshaller.IsDisposed || !_marshaller.IsHandleCreated) return;
                _marshaller.BeginInvoke(new Action(() =>
                {
                    try { _core?.PostWebMessageAsJson(json); }
                    catch { /* shutdown */ }
                }));
            }
            catch { /* shutdown */ }
        }

        private static string Get(Dictionary<string, object> msg, string key)
        {
            object value;
            return msg.TryGetValue(key, out value) ? value as string : null;
        }

        private static string Escape(string value)
        {
            if (value == null) return string.Empty;
            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
