import { useCallback, useRef } from "react"
import { translate } from "@/i18n"
import { logger } from "@/lib/utils/logger"
import { streamChat } from "@/services/stream.service"
import { buildWireContent } from "@/lib/attachments"
import {
  useChatStore,
  selectMessages,
  selectIsLoading,
  selectToolProgress,
} from "@/store/chat.store"
import { useConnectionStore, selectModelAccepts } from "@/store/connection.store"
import { useSession } from "@/hooks/use-session"
import type { AssistantMessage, MessageAttachment, UserMessage } from "@/types/chat"
import type { SseError } from "@/types/sse"
import type { AgentSessionMessage } from "@/types/session"

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------
export function useChat() {
  const messages = useChatStore(selectMessages)
  const isLoading = useChatStore(selectIsLoading)
  const toolProgress = useChatStore(selectToolProgress)

  const addMessage = useChatStore((s) => s.addMessage)
  const appendDelta = useChatStore((s) => s.appendDelta)
  const appendReasoning = useChatStore((s) => s.appendReasoning)
  const finalizeMessage = useChatStore((s) => s.finalizeMessage)
  const removeMessage = useChatStore((s) => s.removeMessage)
  const startToolProgress = useChatStore((s) => s.startToolProgress)
  const updateToolProgress = useChatStore((s) => s.updateToolProgress)
  const completeToolProgress = useChatStore((s) => s.completeToolProgress)
  const clearToolProgress = useChatStore((s) => s.clearToolProgress)
  const reconcileTurnMessages = useChatStore((s) => s.reconcileTurnMessages)
  const setLoading = useChatStore((s) => s.setLoading)
  const persistSession = useChatStore((s) => s.persistSession)
  const setContextUsage = useChatStore((s) => s.setContextUsage)
  const setSessionCost = useChatStore((s) => s.setSessionCost)

  const { ensureSession, clearSession } = useSession()

  // Keep a ref to the active AbortController so `stopGeneration` can cancel it.
  const abortCtrlRef = useRef<AbortController | null>(null)
  const currentAssistantIdRef = useRef<string | null>(null)

  const startAssistantBubble = useCallback((): string => {
    const id = `assistant-${Date.now()}-${Math.random().toString(36).substring(2, 6)}`
    addMessage({
      id,
      role: "assistant",
      content: "",
      streaming: true,
      timestamp: Date.now(),
    } as AssistantMessage)
    currentAssistantIdRef.current = id
    return id
  }, [addMessage])

  /** Finalize the current bubble (or drop it when a pure tool-call step left it empty). */
  const settleAssistantBubble = useCallback(
    (content?: string, reasoning?: string, images?: string[] | null) => {
      const id = currentAssistantIdRef.current
      if (!id) return
      const msg = useChatStore.getState().messages.find((m) => m.id === id)
      if (!msg || msg.role !== "assistant" || !(msg as AssistantMessage).streaming) return
      const finalContent = content ?? (msg as AssistantMessage).content ?? ""
      const hasImages = !!images && images.length > 0
      if (finalContent === "" && !hasImages) {
        removeMessage(id)
      } else {
        finalizeMessage(id, finalContent, reasoning, images ?? undefined)
      }
    },
    [finalizeMessage, removeMessage]
  )

  // ---------------------------------------------------------------------------
  // sendMessage
  // ---------------------------------------------------------------------------

  const sendMessage = useCallback(
    async (content: string, attachments?: MessageAttachment[]): Promise<void> => {
      const trimmed = content.trim()
      if ((!trimmed && !attachments?.length) || isLoading) return

      // Ensure a host session exists.
      const sessionId = await ensureSession()
      if (!sessionId) {
        logger.error("useChat.sendMessage: no session available, aborting send")
        return
      }

      // Convert attachments to wire parts (PDF → native file part when the
      // model advertises pdf input, else local pdf.js page images).
      const accepts = selectModelAccepts(useConnectionStore.getState())
      const wireContent = await buildWireContent(trimmed, attachments, {
        image: accepts.image,
        pdf: accepts.pdf,
      })

      setLoading(true)

      // 2. Optimistic user bubble
      const userMsg: UserMessage = {
        id: `user-${Date.now()}`,
        role: "user",
        content: trimmed,
        ...(attachments && attachments.length > 0 ? { attachments } : {}),
        timestamp: Date.now(),
      }
      addMessage(userMsg)

      // 3. First assistant bubble (step 1 reuses this placeholder)
      startAssistantBubble()

      // 4. Start the SSE stream — the host loop may span several steps
      const ctrl = streamChat(
        sessionId,
        wireContent,
        {
          onMessageStarted: () => {
            const id = currentAssistantIdRef.current
            const msg = id
              ? useChatStore.getState().messages.find((m) => m.id === id)
              : undefined
            const reusable =
              msg &&
              msg.role === "assistant" &&
              (msg as AssistantMessage).streaming &&
              ((msg as AssistantMessage).content ?? "") === ""
            if (!reusable) startAssistantBubble()
          },
          onDelta: ({ delta }) => {
            const id = currentAssistantIdRef.current
            if (id) appendDelta(id, delta)
          },
          onToolStarted: ({ tool_name, preview }: { tool_name: string; preview?: string }) => {
            if (tool_name === "_thinking" || tool_name === "reasoning" || tool_name === "thinking") {
              return
            }
            startToolProgress(tool_name, preview)
          },
          onToolProgress: ({ tool_name, delta }: { tool_name: string; delta: string }) => {
            if (tool_name === "_thinking" || tool_name === "reasoning" || tool_name === "thinking") {
              const id = currentAssistantIdRef.current
              if (id) appendReasoning(id, delta)
              return
            }
            updateToolProgress(tool_name, delta)
          },
          onToolCompleted: ({ tool_name, preview }: { tool_name: string; preview?: string | null }) => {
            if (tool_name === "_thinking" || tool_name === "reasoning" || tool_name === "thinking") {
              return
            }
            completeToolProgress(tool_name, preview)
          },
          onAssistantCompleted: (payload) => {
            settleAssistantBubble(payload.content, payload.reasoning_content ?? undefined, payload.images)
            // Footer metrics: the LAST step's token footprint vs the model's
            // context window (opencode prompt-footer semantics).
            const usage = payload.usage
            if (usage) {
              const footprint =
                (usage.input_tokens ?? 0) + (usage.output_tokens ?? 0)
              const window = useConnectionStore.getState().providerContext
              setContextUsage(footprint, window && window > 0 ? window : null)
            }
          },
          onRunCompleted: (payload) => {
            const runMessages = payload?.messages as AgentSessionMessage[] | undefined
            if (runMessages) {
              reconcileTurnMessages(runMessages)
            }
            if (typeof payload?.session_cost === "number") {
              setSessionCost(payload.session_cost)
            }
            if (typeof payload?.context_window === "number" && payload.context_window > 0) {
              const current = useChatStore.getState().contextUsage
              if (current) setContextUsage(current.tokens, payload.context_window)
            }
            clearToolProgress()
            persistSession()
            abortCtrlRef.current = null
            setLoading(false)
            logger.debug("useChat: run completed")
          },
          onError: ({ message }: SseError) => {
            logger.error("useChat: stream error", { message })
            const errorLine = translate("system.chatErrorLine", { message })
            const id = currentAssistantIdRef.current
            const msg = id
              ? useChatStore.getState().messages.find((m) => m.id === id)
              : undefined
            if (id && msg && msg.role === "assistant" && (msg as AssistantMessage).streaming) {
              // Preserve any partially streamed answer; append the error.
              const partial = (msg as AssistantMessage).content ?? ""
              finalizeMessage(id, partial ? `${partial}\n\n${errorLine}` : errorLine)
            } else {
              const errorId = startAssistantBubble()
              finalizeMessage(errorId, errorLine)
            }
            clearToolProgress()
            setLoading(false)
            abortCtrlRef.current = null
          },
          onDone: () => {
            const stillLoading = useChatStore.getState().isLoading
            if (stillLoading) {
              settleAssistantBubble()
              setLoading(false)
              abortCtrlRef.current = null
              logger.warn("useChat: stream closed without run.completed or error event")
            }
          },
        },
      )

      abortCtrlRef.current = ctrl
    },
    [
      isLoading,
      ensureSession,
      addMessage,
      setLoading,
      appendDelta,
      appendReasoning,
      finalizeMessage,
      startAssistantBubble,
      settleAssistantBubble,
      startToolProgress,
      updateToolProgress,
      completeToolProgress,
      clearToolProgress,
      reconcileTurnMessages,
      persistSession,
      setContextUsage,
      setSessionCost,
    ]
  )

  // ---------------------------------------------------------------------------
  // stopGeneration
  // ---------------------------------------------------------------------------
  const stopGeneration = useCallback(() => {
    if (!abortCtrlRef.current) return
    abortCtrlRef.current.abort()
    abortCtrlRef.current = null
    settleAssistantBubble()
    setLoading(false)
    clearToolProgress()
    logger.debug("useChat: generation stopped by user")
  }, [setLoading, clearToolProgress, settleAssistantBubble])

  // ---------------------------------------------------------------------------
  // Return
  // ---------------------------------------------------------------------------

  return {
    messages,
    isLoading,
    toolProgress,
    sendMessage,
    stopGeneration,
    clearSession,
  }
}
