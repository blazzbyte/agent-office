
import type { AgentSession, AgentSessionMessage } from "@/types/session"
import type { SseUsage } from "@/types/sse"

// ---------------------------------------------------------------------------
// Internal fetch helper types
// ---------------------------------------------------------------------------

export interface ParsedResponseData {
  json?: unknown
  text?: string
  isJson: boolean
}

export interface ApiResponse<T> {
  data: T
  parsedResponse: ParsedResponseData
  response: Response
  ok: boolean
}

// ---------------------------------------------------------------------------
// GET /api/sessions
// ---------------------------------------------------------------------------

export interface SessionListResponse {
  object: "list"
  data: AgentSession[]
  limit: number
  offset: number
  has_more: boolean
}

// ---------------------------------------------------------------------------
// GET /api/sessions/{id}
// ---------------------------------------------------------------------------

export interface SessionResponse {
  object: "agentoffice.session"
  session: AgentSession
}

// ---------------------------------------------------------------------------
// DELETE /api/sessions/{id}
// ---------------------------------------------------------------------------

export interface SessionDeletedResponse {
  object: "agentoffice.session.deleted"
  id: string
  deleted: boolean
}

// ---------------------------------------------------------------------------
// GET /api/sessions/{id}/messages
// ---------------------------------------------------------------------------

export interface SessionMessagesResponse {
  object: "list"
  session_id: string
  data: AgentSessionMessage[]
}

// ---------------------------------------------------------------------------
// POST /api/sessions/{id}/chat  (non-streaming variant)
// ---------------------------------------------------------------------------

export interface SessionChatResponse {
  object: "agentoffice.session.chat.completion"
  session_id: string
  message: { role: string; content: string }
  usage: SseUsage | null
}

// ---------------------------------------------------------------------------
// GET /health
// ---------------------------------------------------------------------------

export interface HealthResponse {
  status: "ok"
  platform: string
  version: string
  /** Host-side provider readiness (apiKey + model present). */
  configured?: boolean | null
  /** Active model id (informational). */
  model?: string | null
  /** Context window (tokens) of the active model per the model registry. */
  model_context?: number | null
  /** Whether the active model is a reasoning model (per registry). */
  model_reasoning?: boolean | null
  /** Whether the active model accepts image inputs (per registry). */
  model_vision?: boolean | null
  /** Non-text input modalities of the active model ("image","video","audio","pdf"). */
  model_modalities_input?: string[] | null
  /** Non-text output modalities of the active model ("image","audio"). */
  model_modalities_output?: string[] | null
  /** Office app hosting this add-in instance ("excel" | "word" | "powerpoint"). */
  host_app?: string | null
}

// ---------------------------------------------------------------------------
// GET /v1/capabilities
// ---------------------------------------------------------------------------

export interface CapabilitiesResponse {
  object: "agentoffice.agent.capabilities"
  platform: string
  model: string
  auth: { type: string; required: boolean }
  features: Record<string, boolean | string>
  endpoints: Record<string, { method: string; path: string }>
}

// ---------------------------------------------------------------------------
// POST /api/sessions  (request body)
// ---------------------------------------------------------------------------

export interface CreateSessionBody {
  id?: string
  model?: string
  system_prompt?: string
  title?: string
}
