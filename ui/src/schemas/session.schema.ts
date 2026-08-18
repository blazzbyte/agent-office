import * as z from "zod"

// ---------------------------------------------------------------------------
// Shared primitives
// ---------------------------------------------------------------------------

const nullishString = z.string().nullish()
const nullishNumber = z.number().nullish()
const nullishBool = z.boolean().nullish()
const nullishDateOrNumber = z.union([z.string(), z.number()]).nullish()
const stringOrNumberId = z.union([z.string(), z.number()]).transform((v) => String(v))

// ---------------------------------------------------------------------------
// AgentSession
// ---------------------------------------------------------------------------

export const AgentSessionSchema = z.object({
  id: stringOrNumberId,
  source: nullishString,
  user_id: nullishString,
  model: nullishString,
  title: nullishString,
  started_at: nullishDateOrNumber,
  ended_at: nullishDateOrNumber,
  end_reason: nullishString,
  message_count: nullishNumber,
  tool_call_count: nullishNumber,
  input_tokens: nullishNumber,
  output_tokens: nullishNumber,
  cache_read_tokens: nullishNumber,
  cache_write_tokens: nullishNumber,
  reasoning_tokens: nullishNumber,
  estimated_cost_usd: nullishNumber,
  actual_cost_usd: nullishNumber,
  api_call_count: nullishNumber,
  last_active: nullishDateOrNumber,
  preview: nullishString,
  has_system_prompt: nullishBool,
  has_model_config: nullishBool,
  parent_session_id: nullishString,
})

// ---------------------------------------------------------------------------
// GET /api/sessions
// ---------------------------------------------------------------------------

export const sessionListResponseSchema = z.object({
  object: z.literal("list"),
  data: z.array(AgentSessionSchema),
  limit: nullishNumber,
  offset: nullishNumber,
  has_more: nullishBool,
})

// ---------------------------------------------------------------------------
// POST /api/sessions  |  GET /api/sessions/{id}
// ---------------------------------------------------------------------------

export const sessionResponseSchema = z.object({
  object: z.literal("agentoffice.session"),
  session: AgentSessionSchema,
})

// ---------------------------------------------------------------------------
// DELETE /api/sessions/{id}
// ---------------------------------------------------------------------------

export const sessionDeletedResponseSchema = z.object({
  object: z.literal("agentoffice.session.deleted"),
  id: stringOrNumberId,
  deleted: z.boolean(),
})

// ---------------------------------------------------------------------------
// GET /api/sessions/{id}/messages
// ---------------------------------------------------------------------------

/** Per-message usage blob (persisted on assistant rows). */
export const sessionMessageUsageSchema = z.object({
  model: z.string().nullish(),
  input_tokens: z.number().optional(),
  output_tokens: z.number().optional(),
  reasoning_tokens: z.number().optional(),
  cache_read_tokens: z.number().optional(),
  total_tokens: z.number().optional(),
  cost: z.number().optional(),
  duration_ms: z.number().optional(),
})

export const sessionMessageSchema = z.object({
  id: stringOrNumberId,
  session_id: stringOrNumberId,

  role: z.enum(["user", "assistant", "tool", "compaction"]),
  content: nullishString,
  /** Raw parts when multimodal (attachments / generated images). */
  content_parts: z.array(z.record(z.string(), z.unknown())).nullish(),
  tool_call_id: nullishString,
  tool_name: nullishString,
  tool_calls: z.any().nullish(),
  timestamp: nullishDateOrNumber,
  token_count: nullishNumber,
  finish_reason: nullishString,
  reasoning: nullishString,
  reasoning_content: nullishString,
  usage: sessionMessageUsageSchema.nullish(),
})


export const sessionMessagesResponseSchema = z.object({
  object: z.literal("list"),
  session_id: z.string(),
  data: z.array(sessionMessageSchema),
})

// ---------------------------------------------------------------------------
// GET /health
// ---------------------------------------------------------------------------

export const healthResponseSchema = z.object({
  status: z.literal("ok"),
  platform: z.string(),
  version: z.string(),
  /** Host-side provider readiness (apiKey + model present). */
  configured: z.boolean().nullish(),
  /** Active model id (informational). */
  model: nullishString,
  /** Context window (tokens) of the active model per the model registry. */
  model_context: z.number().nullish(),
  /** Whether the active model is a reasoning model (per registry). */
  model_reasoning: z.boolean().nullish(),
  /** Whether the active model accepts image inputs (per registry). */
  model_vision: z.boolean().nullish(),
  /** Non-text input modalities of the active model ("image","video","audio","pdf"). */
  model_modalities_input: z.array(z.string()).nullish(),
  /** Non-text output modalities of the active model ("image","audio"). */
  model_modalities_output: z.array(z.string()).nullish(),
})

