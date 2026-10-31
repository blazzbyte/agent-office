
import { loadConnectionConfig, buildUrl } from "@/lib/api/config"
import { logger } from "@/lib/utils/logger"
import { sseEventSchemas, type SseEventKey } from "@/schemas/sse.schema"
import type {
  SseCallbacks,
  SseAssistantCompleted,
  SseAssistantDelta,
  SseError,
  SseMessageStarted,
  SseRunCompleted,
  SseRunStarted,
  SseToolCompleted,
  SseToolProgress,
  SseToolStarted,
} from "@/types/sse"
import { translate } from "@/i18n"

// ---------------------------------------------------------------------------
// Recognised event names (guards against gateway future additions)
// ---------------------------------------------------------------------------

const RECOGNISED_EVENTS = new Set<SseEventKey>([
  "run.started",
  "message.started",
  "assistant.delta",
  "tool.started",
  "tool.progress",
  "tool.completed",
  "assistant.completed",
  "run.completed",
  "error",
  "done",
])

// ---------------------------------------------------------------------------
// Event dispatcher (validates with Zod then routes to callback)
// ---------------------------------------------------------------------------

function dispatchSseEvent(
  eventName: string,
  rawPayload: unknown,
  callbacks: SseCallbacks,
): boolean {
  if (!RECOGNISED_EVENTS.has(eventName as SseEventKey)) {
    logger.debug("SSE: ignoring unrecognised event", { eventName })
    return false
  }

  const name = eventName as SseEventKey
  const schema = sseEventSchemas[name]
  const result = schema.safeParse(rawPayload)

  const payload = result.success ? result.data : rawPayload
  if (!result.success) {
    logger.warn("SSE: event payload failed validation, using raw payload", {
      eventName,
      issues: result.error.issues,
      rawPayload,
    })
    console.warn("[EXCEL-AGENT SSE VALIDATION WARN]", eventName, result.error.issues, rawPayload)
  }

  switch (name) {
    case "run.started":
      callbacks.onRunStarted?.(payload as SseRunStarted)
      break
    case "message.started":
      callbacks.onMessageStarted?.(payload as SseMessageStarted)
      break
    case "assistant.delta":
      callbacks.onDelta?.(payload as SseAssistantDelta)
      break
    case "tool.started":
      callbacks.onToolStarted?.(payload as SseToolStarted)
      break
    case "tool.progress":
      callbacks.onToolProgress?.(payload as SseToolProgress)
      break
    case "tool.completed":
      callbacks.onToolCompleted?.(payload as SseToolCompleted)
      break
    case "assistant.completed":
      callbacks.onAssistantCompleted?.(payload as SseAssistantCompleted)
      break
    case "run.completed":
      callbacks.onRunCompleted?.(payload as SseRunCompleted)
      break
    case "error":
      callbacks.onError?.(payload as SseError)
      break
    case "done":
      // Handled in the stream loop — signals clean close.
      break
  }

  return name === "done"
}

// ---------------------------------------------------------------------------
// Stream processor
// ---------------------------------------------------------------------------

async function processStream(
  response: Response,
  callbacks: SseCallbacks,
  signal: AbortSignal,
): Promise<void> {
  if (!response.body) {
    logger.error("SSE: response body is null — cannot read stream")
    callbacks.onError?.({
      message: translate("system.streamEmptyBody"),
      session_id: "",
      run_id: "",
      seq: 0,
      ts: Date.now() / 1000,
    } as SseError)
    return
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ""
  let currentEvent = ""

  try {
    while (true) {
      if (signal.aborted) break

      const { done, value } = await reader.read()
      if (done) break

      buffer += decoder.decode(value, { stream: true })

      // Split on newline boundaries; keep the last (possibly incomplete) line
      const lines = buffer.split("\n")
      buffer = lines.pop() ?? ""

      for (const line of lines) {
        if (line.startsWith("event: ")) {
          currentEvent = line.slice(7).trim()
        } else if (line.startsWith("data: ")) {
          if (!currentEvent) continue // Orphan data line — ignore

          let payload: unknown
          try {
            payload = JSON.parse(line.slice(6))
          } catch {
            logger.warn("SSE: failed to parse data line", {
              event: currentEvent,
              preview: line.slice(0, 80),
            })
            currentEvent = ""
            continue
          }

          if (currentEvent === "done") {
            console.log("[EXCEL-AGENT SSE RAW EVENT] done")
            callbacks.onDone?.()
            return // Clean stream end
          }

          console.log(`[EXCEL-AGENT SSE RAW EVENT] ${currentEvent}`, payload)
          logger.info(`[EXCEL-AGENT SSE RAW EVENT] ${currentEvent}`, { payload })
          dispatchSseEvent(currentEvent, payload, callbacks)
          currentEvent = ""
        } else if (line.startsWith(": ")) {
          // SSE comment/keepalive — intentionally ignored
        }
      }
    }
  } catch (error) {
    if (signal.aborted) {
      logger.debug("SSE: stream cancelled by client")
      return
    }
    logger.error("SSE: stream read error", { error })
    callbacks.onError?.({
      message: error instanceof Error ? error.message : translate("system.streamReadError"),
      session_id: "",
      run_id: "",
      seq: 0,
      ts: Date.now() / 1000,
    } as SseError)
  } finally {
    try {
      reader.releaseLock()
    } catch {
      // Already released — safe to ignore
    }
  }
}

// ---------------------------------------------------------------------------
// Internal fetch runner (runs in the background, never blocks the caller)
// ---------------------------------------------------------------------------
export type ChatMessagePart =
  | { type: "text"; text: string }
  | { type: "image_url"; image_url: { url: string; detail?: string } }
  | { type: "file"; file: { filename: string; file_data: string } }

async function _runStream(
  sessionId: string,
  message: string | ChatMessagePart[],
  callbacks: SseCallbacks,
  systemMessage: string | undefined,
  signal: AbortSignal,
): Promise<void> {
  const config = loadConnectionConfig()
  const url = buildUrl(config.baseUrl, `/api/sessions/${sessionId}/chat/stream`)

  // No Authorization header — the provider key lives host-side.
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    Accept: "text/event-stream",
  }


  try {
    const response = await fetch(url, {
      method: "POST",
      headers,
      body: JSON.stringify({
        message,
        ...(systemMessage ? { system_message: systemMessage } : {}),
      }),
      signal,
    })

    if (!response.ok) {
      const errorText = await response.text().catch(() => "Unknown error")
      logger.error("SSE: stream request failed", {
        status: response.status,
        statusText: response.statusText,
        body: errorText.slice(0, 200),
      })
      callbacks.onError?.({
        message: translate("system.gatewayError", {
          status: response.status,
          statusText: response.statusText,
        }),
        session_id: sessionId,
        run_id: "",
        seq: 0,
        ts: Date.now() / 1000,
      } as SseError)
      callbacks.onDone?.()
      return
    }

    await processStream(response, callbacks, signal)
  } catch (error) {
    if (signal.aborted) {
      logger.debug("SSE: fetch aborted, stream stopped cleanly")
      callbacks.onDone?.()
      return
    }
    logger.error("SSE: failed to connect", { error })
    callbacks.onError?.({
      message:
        error instanceof Error
          ? error.message
          : translate("system.backendConnectFailed"),
      session_id: sessionId,
      run_id: "",
      seq: 0,
      ts: Date.now() / 1000,
    } as SseError)
  } finally {
    callbacks.onDone?.()
  }
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

export interface StreamChatOptions {
  systemMessage?: string
  /** External AbortSignal to chain (e.g. from a parent controller). */
  signal?: AbortSignal
}
export function streamChat(
  sessionId: string,
  message: string | ChatMessagePart[],
  callbacks: SseCallbacks,
  options?: StreamChatOptions,
): AbortController {
  const controller = new AbortController()

  if (options?.signal) {
    if (options.signal.aborted) {
      controller.abort()
    } else {
      options.signal.addEventListener("abort", () => controller.abort())
    }
  }

  // Fire-and-forget — the `finally` block in `_runStream` guarantees
  // `onDone` is always called regardless of outcome.
  void _runStream(
    sessionId,
    message,
    callbacks,
    options?.systemMessage,
    controller.signal,
  )

  return controller
}
