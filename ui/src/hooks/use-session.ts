import { useCallback, useEffect, useRef, useState } from "react"
import { logger } from "@/lib/utils/logger"
import { createSession, getSession, deleteSession } from "@/services/session.service"
import { partsToDisplay } from "@/lib/attachments"
import {
  useChatStore,
  selectActiveSessionId,
} from "@/store/chat.store"
import type { ChatMessage } from "@/types/chat"
import type { AgentSessionMessage } from "@/types/session"
import { translate } from "@/i18n"

// ---------------------------------------------------------------------------
// History mapping (shared by initSession + switchSession)
// ---------------------------------------------------------------------------
function mapHistoryMessage(
  msg: AgentSessionMessage,
  lastSeenToolName: string,
): { message: ChatMessage | null; lastSeenToolName: string } {
  const timestamp = msg.timestamp ? new Date(msg.timestamp).getTime() : Date.now()
  if (msg.role === "compaction") return { message: null, lastSeenToolName }
  if (msg.role === "user" || msg.role === "assistant") {
    if (msg.role === "user" && msg.content?.startsWith("[EXCEL_RESULTS]")) {
      return { message: null, lastSeenToolName }
    }
    if (msg.role === "assistant" && Array.isArray(msg.tool_calls) && msg.tool_calls.length > 0) {
      const tc = msg.tool_calls[0]
      lastSeenToolName = tc?.function?.name || tc?.name || "tool"
    }
    const parts = msg.content_parts?.length ? partsToDisplay(msg.content_parts) : null
    const mimeOf = (url: string): string =>
      url.startsWith("data:") ? url.slice(5, url.indexOf(";")) || "image/png" : "image/png"
    return {
      lastSeenToolName,
      message: {
        id: msg.id,
        role: msg.role,
        content: parts ? parts.text : (msg.content ?? ""),
        reasoning: msg.reasoning || msg.reasoning_content || undefined,
        timestamp,
        ...(msg.role === "assistant"
          ? {
              streaming: false,
              usage: msg.usage,
              ...(parts && parts.images.length > 0 ? { images: parts.images } : {}),
            }
          : {}),
        ...(msg.role === "user" && parts
          ? {
              attachments: [
                ...parts.attachments,
                ...parts.images.map((url, i) => ({
                  name: `image ${i + 1}`,
                  mime: mimeOf(url),
                  dataUrl: url,
                })),
              ],
            }
          : {}),
      } as ChatMessage,
    }
  }
  // tool
  const toolName = msg.tool_name || lastSeenToolName
  return {
    lastSeenToolName: toolName,
    message: {
      id: msg.id,
      role: "tool",
      toolName,
      preview: msg.content ?? "",
      completed: true,
      timestamp,
    } as ChatMessage,
  }
}

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------
export function useSession() {
  const sessionId = useChatStore(selectActiveSessionId)
  const setSession = useChatStore((s) => s.setSession)
  const clearSessionStore = useChatStore((s) => s.clearSession)
  const addMessage = useChatStore((s) => s.addMessage)
  const setContextUsage = useChatStore((s) => s.setContextUsage)
  const setSessionCost = useChatStore((s) => s.setSessionCost)

  const [isInitializing, setIsInitializing] = useState(false)
  const [initError, setInitError] = useState<string | null>(null)

  // Guard: don't run init twice if the component re-mounts.
  const initRunRef = useRef(false)
  const seedFooterMetrics = (
    session: { cost?: number } | null,
    history: AgentSessionMessage[],
  ): void => {
    setSessionCost(session?.cost ?? null)
    const lastUsage = [...history]
      .reverse()
      .find((m) => m.role === "assistant" && m.usage)?.usage
    if (lastUsage) {
      setContextUsage(
        (lastUsage.input_tokens ?? 0) + (lastUsage.output_tokens ?? 0),
        null,
      )
    }
  }

  // ---------------------------------------------------------------------------
  // initSession — called on mount
  // ---------------------------------------------------------------------------

  const initSession = useCallback(async (): Promise<string | null> => {
    if (initRunRef.current) return sessionId
    initRunRef.current = true
    setIsInitializing(true)
    setInitError(null)

    try {
      clearSessionStore()
      setSession(null)
      setIsInitializing(false)
      return null
    } catch (error) {
      const msg = error instanceof Error ? error.message : translate("system.sessionInitFailed")
      logger.error("useSession: unexpected error during init", { error })
      setInitError(msg)
      setIsInitializing(false)
      return null
    }
  }, [sessionId, setSession, clearSessionStore])



  // ---------------------------------------------------------------------------
  // ensureSession — lazy init specifically for useChat right before first message
  // ---------------------------------------------------------------------------

  const ensureSession = useCallback(async (): Promise<string | null> => {
    const current = useChatStore.getState().activeSessionId
    if (current) return current

    setIsInitializing(true)
    setInitError(null)
    try {
      // The host owns the provider model AND the agent system prompt
      // (Phase 4: native tool calling, see src/Agent/AgentPrompt.cs).
      const created = await createSession({})
      if (!created) {
        logger.error("Failed to create session — check backend connection")
        setInitError(translate("system.sessionCreateFailedBackend"))
        setIsInitializing(false)
        return null
      }
      setSession(created.id)
      logger.debug("useSession: lazy session created right on first message", { sessionId: created.id })
      setIsInitializing(false)
      return created.id
    } catch (error) {
      const msg = error instanceof Error ? error.message : translate("system.sessionCreateFailed")
      logger.error("useSession: unexpected error during ensureSession", { error })
      setInitError(msg)
      setIsInitializing(false)
      return null
    }
  }, [setSession])

  // ---------------------------------------------------------------------------
  // newSession — clear everything and start fresh without creating an orphan session
  // ---------------------------------------------------------------------------
  const newSession = useCallback(async (): Promise<string | null> => {
    setIsInitializing(false)
    setInitError(null)
    clearSessionStore()
    setSession(null)
    initRunRef.current = true
    logger.debug("useSession: newSession called (lazy mode: reset store, waiting for first message)")
    return null
  }, [clearSessionStore, setSession])


  // ---------------------------------------------------------------------------
  // switchSession — load an existing session by ID
  // ---------------------------------------------------------------------------

  /**
   * Switch the active session to an existing gateway session.
   * Clears the current message list and loads the new session's history.
   */
  const switchSession = useCallback(
    async (targetSessionId: string): Promise<boolean> => {
      if (targetSessionId === useChatStore.getState().activeSessionId) return true

      setIsInitializing(true)
      setInitError(null)

      try {
        const session = await getSession(targetSessionId)
        if (!session) {
          setInitError(translate("system.sessionNotFound", { sessionId: targetSessionId }))
          setIsInitializing(false)
          return false
        }

        // Clear current messages and switch session
        clearSessionStore()
        setSession(targetSessionId)
        initRunRef.current = true

        // Lazy-load message history for the target session
        const { getSessionMessages } = await import("@/services/session.service")
        const history = await getSessionMessages(targetSessionId)

        // Convert gateway message format to our ChatMessage format and add to store
        let lastSeenToolName = "tool"
        history.forEach((msg) => {
          const mapped = mapHistoryMessage(msg, lastSeenToolName)
          lastSeenToolName = mapped.lastSeenToolName
          if (mapped.message) addMessage(mapped.message)
        })

        seedFooterMetrics(session, history)

        logger.debug("useSession: switched session", {
          sessionId: targetSessionId,
          loadedMessages: history.length,
        })

        setIsInitializing(false)
        return true
      } catch (error) {
        logger.error("useSession: switchSession failed", { error })
        setInitError(translate("system.sessionSwitchFailed"))
        setIsInitializing(false)
        return false
      }
    },
    [clearSessionStore, setSession, addMessage],
  )

  // ---------------------------------------------------------------------------
  // clearSession — delete from gateway and reset
  // ---------------------------------------------------------------------------

  /**
   * Delete the current session from the gateway and clear local state.
   * Does not create a replacement — use `newSession()` for that.
   */
  const clearSession = useCallback(async () => {
    const currentId = useChatStore.getState().activeSessionId
    clearSessionStore()
    initRunRef.current = false

    if (currentId) {
      await deleteSession(currentId).catch((err) =>
        logger.warn("useSession: failed to delete session on gateway", { err }),
      )
    }
  }, [clearSessionStore])

  // ---------------------------------------------------------------------------
  // Auto-init on mount
  // ---------------------------------------------------------------------------

  useEffect(() => {
    const init = initSession
    queueMicrotask(() => { void init() })
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  return {
    sessionId,
    isInitializing,
    initError,
    initSession,
    ensureSession,
    newSession,
    switchSession,
    clearSession,
  }
}
