import { create } from "zustand"
import { logger } from "@/lib/utils/logger"
import type { AssistantMessage, ChatMessage, ToolMessage } from "@/types/chat"
import type { PersistedSession, AgentSessionMessage } from "@/types/session"
import { partsToDisplay } from "@/lib/attachments"

// ---------------------------------------------------------------------------
// Storage key
// ---------------------------------------------------------------------------

const SESSION_STORAGE_KEY = "agent-office:session"

// ---------------------------------------------------------------------------
// Tool progress shape
// ---------------------------------------------------------------------------

export interface ToolProgress {
  toolName: string
  delta: string
}

// ---------------------------------------------------------------------------
// State shape
// ---------------------------------------------------------------------------

interface ChatState {
  messages: ChatMessage[]
  activeSessionId: string | null
  isLoading: boolean
  /** ID of the assistant message currently being streamed. */
  streamingMessageId: string | null
  toolProgress: ToolProgress | null
  contextUsage: { tokens: number; window: number } | null
  sessionCost: number | null

  // --- Session actions ---
  setSession: (sessionId: string | null) => void
  clearSession: () => void

  // --- Metrics actions ---
  /** Update the footer's context usage (tokens, and window when known). */
  setContextUsage: (tokens: number, window?: number | null) => void
  /** Update the session's running spend (USD). */
  setSessionCost: (cost: number | null) => void


  // --- Message actions ---
  addMessage: (message: ChatMessage) => void
  /** Append a streaming delta to an in-flight AssistantMessage. */
  appendDelta: (messageId: string, delta: string) => void
  /** Append thinking/reasoning delta to an in-flight AssistantMessage. */
  appendReasoning: (messageId: string, delta: string) => void
  /** Replace the full content of a message and mark it as no longer streaming. */
  finalizeMessage: (messageId: string, content: string, reasoning?: string, images?: string[]) => void
  /** Remove a message (e.g. an assistant bubble a pure tool-call step left empty). */
  removeMessage: (messageId: string) => void

  // --- Tool progress actions ---
  startToolProgress: (toolName: string, preview?: string) => void
  updateToolProgress: (toolName: string, delta: string) => void
  completeToolProgress: (toolName: string, preview?: string | null) => void
  clearToolProgress: () => void
  /** Reconcile live messages with canonical server messages on run completed. */
  reconcileTurnMessages: (turnMessages: AgentSessionMessage[]) => void

  // --- Loading ---
  setLoading: (loading: boolean) => void

  // --- Persistence ---
  /** Load session + messages from localStorage. */
  loadPersistedSession: () => void
  /** Save current session + messages to localStorage. */
  persistSession: () => void
}

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

export const useChatStore = create<ChatState>((set, get) => ({
  messages: [],
  activeSessionId: null,
  isLoading: false,
  streamingMessageId: null,
  toolProgress: null,
  contextUsage: null,
  sessionCost: null,

  // ------- Session actions -------

  setSession: (sessionId) =>
    set({
      activeSessionId: sessionId,
      toolProgress: null,
    }),

  clearSession: () =>
    set({
      messages: [],
      activeSessionId: null,
      isLoading: false,
      streamingMessageId: null,
      toolProgress: null,
      contextUsage: null,
      sessionCost: null,
    }),

  // ------- Metrics -------

  setContextUsage: (tokens, window) =>
    set((state) => ({
      contextUsage: {
        tokens,
        window:
          window && window > 0
            ? window
            : state.contextUsage?.window ?? 0,
      },
    })),

  setSessionCost: (cost) => set({ sessionCost: cost }),

  // ------- Messages -------

  addMessage: (message) =>
    set((state) => ({
      messages: [...state.messages, message],
    })),

  removeMessage: (messageId) =>
    set((state) => ({
      messages: state.messages.filter((msg) => msg.id !== messageId),
      streamingMessageId:
        state.streamingMessageId === messageId ? null : state.streamingMessageId,
    })),

  appendDelta: (messageId, delta) =>
    set((state) => ({
      messages: state.messages.map((msg) =>
        msg.id === messageId && msg.role === "assistant"
          ? ({
            ...msg,
            content: (msg as AssistantMessage).content + delta,
            streaming: true,
          } as AssistantMessage)
          : msg,
      ),
      streamingMessageId: messageId,
    })),

  appendReasoning: (messageId, delta) =>
    set((state) => ({
      messages: state.messages.map((msg) =>
        msg.id === messageId && msg.role === "assistant"
          ? ({
            ...msg,
            reasoning: ((msg as AssistantMessage).reasoning || "") + delta,
          } as AssistantMessage)
          : msg,
      ),
    })),

  finalizeMessage: (messageId, content, reasoning, images) =>
    set((state) => ({
      messages: state.messages.map((msg) =>
        msg.id === messageId && msg.role === "assistant"
          ? ({
            ...msg,
            content,
            ...(reasoning ? { reasoning } : {}),
            ...(images && images.length > 0 ? { images } : {}),
            streaming: false,
          } as AssistantMessage)
          : msg,
      ),
      streamingMessageId: null,
    })),

  // ------- Tool progress -------

  startToolProgress: (toolName, preview) =>
    set((state) => {
      const newMsg: ToolMessage = {
        id: `tool-${Date.now()}-${Math.random().toString(36).substring(2, 6)}`,
        role: "tool",
        toolName,
        preview: preview || "",
        completed: false,
        timestamp: Date.now(),
      }
      const messages = [...state.messages]
      const lastIndex = messages.length - 1
      if (lastIndex >= 0 && messages[lastIndex].role === "assistant" && messages[lastIndex].streaming) {
        messages.splice(lastIndex, 0, newMsg)
      } else {
        messages.push(newMsg)
      }
      return {
        toolProgress: { toolName, delta: preview || "" },
        messages,
      }
    }),

  updateToolProgress: (toolName, delta) =>
    set((state) => {
      let lastToolIndex = -1
      for (let i = state.messages.length - 1; i >= 0; i--) {
        if (state.messages[i].role === "tool" && (state.messages[i] as ToolMessage).toolName === toolName) {
          lastToolIndex = i
          break
        }
      }
      if (lastToolIndex === -1) {
        const newMsg: ToolMessage = {
          id: `tool-${Date.now()}-${Math.random().toString(36).substring(2, 6)}`,
          role: "tool",
          toolName,
          preview: delta,
          completed: false,
          timestamp: Date.now(),
        }
        const messages = [...state.messages]
        const lastIndex = messages.length - 1
        if (lastIndex >= 0 && messages[lastIndex].role === "assistant" && messages[lastIndex].streaming) {
          messages.splice(lastIndex, 0, newMsg)
        } else {
          messages.push(newMsg)
        }
        return {
          toolProgress: { toolName, delta },
          messages,
        }
      }
      return {
        toolProgress: { toolName, delta },
        messages: state.messages.map((msg, idx) => {
          if (idx === lastToolIndex) {
            const existing = (msg as ToolMessage).preview || ""
            let updated = delta
            if (existing && !delta.startsWith(existing) && existing !== delta) {
              updated = `${existing}\n${delta}`
            }
            return { ...msg, preview: updated } as ToolMessage
          }
          return msg
        }),
      }
    }),

  completeToolProgress: (toolName, preview) =>
    set((state) => {
      let lastToolIndex = -1
      for (let i = state.messages.length - 1; i >= 0; i--) {
        if (state.messages[i].role === "tool" && (state.messages[i] as ToolMessage).toolName === toolName && !(state.messages[i] as ToolMessage).completed) {
          lastToolIndex = i
          break
        }
      }
      if (lastToolIndex === -1) {
        for (let i = state.messages.length - 1; i >= 0; i--) {
          if (state.messages[i].role === "tool" && (state.messages[i] as ToolMessage).toolName === toolName) {
            lastToolIndex = i
            break
          }
        }
      }
      return {
        toolProgress: state.toolProgress?.toolName === toolName ? null : state.toolProgress,
        messages: state.messages.map((msg, idx) => {
          if (idx === lastToolIndex) {
            const existingPreview = (msg as ToolMessage).preview || ""
            return {
              ...msg,
              preview: preview !== null && preview !== undefined && preview !== "" ? preview : existingPreview,
              completed: true,
            } as ToolMessage
          }
          return msg
        }),
      }
    }),

  clearToolProgress: () =>
    set((state) => ({
      toolProgress: null,
      messages: state.messages.map((msg) => {
        if (msg.role === "tool" && !(msg as ToolMessage).completed) {
          return { ...msg, completed: true } as ToolMessage
        }
        return msg
      }),
    })),

  reconcileTurnMessages: (turnMessages) =>
    set((state) => {
      if (!Array.isArray(turnMessages) || turnMessages.length === 0) {
        return state
      }

      const messages = [...state.messages]
      let lastSeenToolName = "tool"
      const toolIdxMap = new Map<string, number[]>()

      // First pass: index existing ToolMessages from oldest -> newest
      for (let i = 0; i < messages.length; i++) {
        if (messages[i].role === "tool") {
          const tName = (messages[i] as ToolMessage).toolName
          const list = toolIdxMap.get(tName) || []
          list.push(i)
          toolIdxMap.set(tName, list)
        }
      }

      const consumedToolMap = new Map<string, number>()

      for (const m of turnMessages) {
        if (m.role === "assistant" && Array.isArray(m.tool_calls) && m.tool_calls.length > 0) {
          const tc = m.tool_calls[0]
          lastSeenToolName = tc?.function?.name || tc?.name || "tool"
        }

        if (m.role === "tool") {
          const tName = m.tool_name || lastSeenToolName
          const consumedCount = consumedToolMap.get(tName) || 0
          const indices = toolIdxMap.get(tName) || []

          if (consumedCount < indices.length) {
            const idx = indices[consumedCount]
            const existing = messages[idx] as ToolMessage
            messages[idx] = {
              ...existing,
              preview: m.content || existing.preview,
              completed: true,
            }
            consumedToolMap.set(tName, consumedCount + 1)
          } else {
            messages.push({
              id: m.id || `tool-${Date.now()}-${Math.random().toString(36).substring(2, 6)}`,
              role: "tool",
              toolName: tName,
              preview: m.content || "",
              completed: true,
              timestamp: m.timestamp ? new Date(m.timestamp).getTime() : Date.now(),
            } as ToolMessage)
            consumedToolMap.set(tName, consumedCount + 1)
          }
        } else if (m.role === "assistant" && (m.content || m.content_parts?.length)) {
          const idx = messages.findIndex(
            (old) =>
              old.role === "assistant" &&
              (old.id === m.id || (m.message_id && old.id === m.message_id))
          )
          if (idx !== -1) {
            const parts = m.content_parts?.length ? partsToDisplay(m.content_parts) : null
            messages[idx] = {
              ...messages[idx],
              content: parts ? parts.text : m.content,
              reasoning: m.reasoning || m.reasoning_content || (messages[idx] as AssistantMessage).reasoning,
              usage: m.usage ?? (messages[idx] as AssistantMessage).usage,
              ...(parts && parts.images.length > 0 ? { images: parts.images } : {}),
              streaming: false,
            } as AssistantMessage
          }
        }
      }

      return { messages }
    }),

  // ------- Loading -------

  setLoading: (loading) => set({ isLoading: loading }),

  // ------- Persistence -------

  loadPersistedSession: () => {
    try {
      const raw = localStorage.getItem(SESSION_STORAGE_KEY)
      if (!raw) return

      const stored = JSON.parse(raw) as PersistedSession

      // Sanitise: ensure no messages are left in a streaming state
      const messages = stored.messages.map((msg) =>
        msg.role === "assistant" && (msg as AssistantMessage).streaming
          ? ({ ...msg, streaming: false } as AssistantMessage)
          : msg,
      )

      set({
        activeSessionId: stored.sessionId,
        messages,
      })

      logger.debug("Chat session restored from localStorage", {
        sessionId: stored.sessionId,
        messageCount: messages.length,
      })
    } catch (error) {
      logger.warn("Failed to load persisted session", { error })
    }
  },

  persistSession: () => {
    const { activeSessionId, messages } = get()
    if (!activeSessionId) return

    try {
      const payload: PersistedSession = {
        sessionId: activeSessionId,
        messages,
        createdAt: Date.now(),
      }
      localStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify(payload))
    } catch (error) {
      logger.warn("Failed to persist session to localStorage", { error })
    }
  },
}))

// ---------------------------------------------------------------------------
// Selectors
// ---------------------------------------------------------------------------

export const selectMessages = (s: ChatState) => s.messages
export const selectIsLoading = (s: ChatState) => s.isLoading
export const selectActiveSessionId = (s: ChatState) => s.activeSessionId
export const selectToolProgress = (s: ChatState) => s.toolProgress
export const selectStreamingMessageId = (s: ChatState) => s.streamingMessageId
export const selectContextUsage = (s: ChatState) => s.contextUsage
export const selectSessionCost = (s: ChatState) => s.sessionCost
