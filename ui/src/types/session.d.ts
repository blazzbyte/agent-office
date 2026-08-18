/**
 * Session & connection domain types.
 */

import type { ChatMessage } from "@/types/chat"

// ---------------------------------------------------------------------------
// Agent session resource
// ---------------------------------------------------------------------------

/**
 * An agent session as returned by GET /api/sessions/{id}.
 */
export interface AgentSession {
  id: string
  source?: string
  user_id?: string
  model?: string
  title?: string
  started_at?: string
  ended_at?: string
  end_reason?: string
  message_count?: number
  tool_call_count?: number
  input_tokens?: number
  output_tokens?: number
  cache_read_tokens?: number
  cache_write_tokens?: number
  reasoning_tokens?: number
  estimated_cost_usd?: number
  actual_cost_usd?: number
  /** Running spend (USD, estimated from catalog pricing) — host-side. */
  cost?: number
  api_call_count?: number
  parent_session_id?: string
  last_active?: string
  preview?: string
  has_system_prompt?: boolean
  has_model_config?: boolean
}

/**
 * Per-message usage blob (persisted on assistant messages; powers the
 * context/cost footer after reloads).
 */
export interface AgentMessageUsage {
  model?: string | null
  input_tokens?: number
  output_tokens?: number
  reasoning_tokens?: number
  cache_read_tokens?: number
  total_tokens?: number
  cost?: number
  /** Wall time (ms) the step took — aggregated for the last-run summary. */
  duration_ms?: number
}

/**
 * A single message in a session's history as returned by
 * GET /api/sessions/{id}/messages.
 */
export interface AgentSessionMessage {
  id: string
  session_id: string
  role: "user" | "assistant" | "tool" | "compaction"
  content?: string
  /** Raw content parts when the message is multimodal (attachments, generated images). */
  content_parts?: Array<Record<string, unknown>> | null
  tool_call_id?: string
  tool_calls?: unknown
  tool_name?: string
  timestamp?: string
  token_count?: number
  finish_reason?: string
  reasoning?: string
  reasoning_content?: string
  message_id?: string
  usage?: AgentMessageUsage
}

// ---------------------------------------------------------------------------
// Local persistence
// ---------------------------------------------------------------------------

/**
 * What we persist to localStorage for session resumption.
 * Key: `agent-office:session`
 */
export interface PersistedSession {
  sessionId: string
  messages: ChatMessage[]
  createdAt: number
  title?: string
}

// ---------------------------------------------------------------------------
// Connection config
// ---------------------------------------------------------------------------
export interface AgentConnectionConfig {
  baseUrl: string

  /**
   * Stable per-installation identifier sent as `X-AgentOffice-Session-Key`.
   * Auto-generated with `crypto.randomUUID()` on first launch.
   */
  sessionKey: string
}

/**
 * LLM provider settings owned by the host (GET/PUT /api/settings/provider).
 */
export interface ProviderSettings {
  apiKey: string
  baseUrl: string
  model: string
  contextWindow: number
  maxTokens: number
  /** Safety cap on tool-use steps per turn (default 50, ceiling 500). */
  maxSteps: number
  /** Optional image-generation model id (generate_image tool). Empty = off. */
  imageModel: string
  /** Optional provider overrides for the image call (empty = same as chat). */
  imageBaseUrl: string
  imageApiKey: string
  imageProvider: string
  /** True when generate_image can run (model + credentials, or local a1111). */
  imageConfigured: boolean
  /** True when the host can call the provider (apiKey + model present). */
  configured: boolean
}
export interface ProviderModelInfo {
  id: string
  name: string
  /** Context window in tokens (0 = unknown). */
  context: number
  /** Max output tokens (0 = unknown). */
  output: number
  price_input: number
  price_output: number
  price_cache_read: number
  reasoning: boolean
  /** Accepts image inputs (drives whether captures are sent as images). */
  vision: boolean
  /** Non-text input modalities ("image","video","audio","pdf"). */
  modalities_input?: string[]
  /** Non-text output modalities ("image","audio"). */
  modalities_output?: string[]
  /** provider | models.dev | default — metadata provenance. */
  source: string
}

