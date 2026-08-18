import { create } from "zustand"
import {
  loadConnectionConfig,
  saveConnectionConfig,
} from "@/lib/api/config"
import { checkHealth, checkExcelMcpInstalled } from "@/services/session.service"
import type { AgentConnectionConfig } from "@/types/session"


// ---------------------------------------------------------------------------
// State shape
// ---------------------------------------------------------------------------

export type ConnectionStatus = "idle" | "checking" | "healthy" | "unhealthy"

interface ConnectionState {
  config: AgentConnectionConfig
  status: ConnectionStatus
  gatewayVersion: string | null
  /** Host-side provider readiness (from /health). */
  providerConfigured: boolean | null
  /** Active model id reported by the host (informational). */
  providerModel: string | null
  /** Context window (tokens) of the active model per the model registry. */
  providerContext: number | null
  /** True when the active model is a reasoning model (per registry). */
  providerModelReasoning: boolean | null
  /** Non-text input modalities of the active model ("image","video","audio","pdf"). */
  providerModelInput: string[]
  /** Non-text output modalities of the active model ("image","audio"). */
  providerModelOutput: string[]
  /** Office app hosting the add-in ("excel" | "word" | "powerpoint"). */
  hostApp: string | null
  excelMcpInstalled: boolean | null
  lastChecked: number | null

  // Actions
  /** Merge a partial config update, persist to localStorage. */
  setConfig: (patch: Partial<AgentConnectionConfig>) => void
  /** Replace the full config, persist to localStorage. */
  replaceConfig: (config: AgentConnectionConfig) => void
  /** Ping `/health` and update `status`. */
  checkHealth: () => Promise<void>
  /** Reset status to `idle` without running a health check. */
  resetStatus: () => void
}

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

export const useConnectionStore = create<ConnectionState>((set, get) => ({
  config: loadConnectionConfig(),
  status: "idle",
  gatewayVersion: null,
  providerConfigured: null,
  providerModel: null,
  providerContext: null,
  providerModelReasoning: null,
  providerModelInput: [],
  providerModelOutput: [],
  hostApp: null,
  excelMcpInstalled: null,
  lastChecked: null,

  setConfig: (patch) => {
    const next: AgentConnectionConfig = { ...get().config, ...patch }
    saveConnectionConfig(next)
    set({ config: next })
  },

  replaceConfig: (config) => {
    saveConnectionConfig(config)
    set({ config })
  },

  checkHealth: async () => {
    set({ status: "checking" })
    const [health, mcpInstalled] = await Promise.all([
      checkHealth(),
      checkExcelMcpInstalled(),
    ])
    if (health) {
      set({
        status: "healthy",
        gatewayVersion: health.version,
        providerConfigured: health.configured ?? null,
        providerModel: health.model ?? null,
        providerContext: health.model_context ?? null,
        providerModelReasoning: health.model_reasoning ?? null,
        providerModelInput: health.model_modalities_input ?? [],
        providerModelOutput: health.model_modalities_output ?? [],
        hostApp: health.host_app ?? null,
        excelMcpInstalled: mcpInstalled,
        lastChecked: Date.now(),
      })
    } else {
      set({
        status: "unhealthy",
        providerConfigured: null,
        hostApp: null,
        excelMcpInstalled: false,
        lastChecked: Date.now(),
      })
    }
  },

  resetStatus: () => set({ status: "idle" }),
}))

// ---------------------------------------------------------------------------
// Selectors (stable references — prevent unnecessary re-renders)
// ---------------------------------------------------------------------------

export const selectConnectionConfig = (s: ConnectionState) => s.config
export const selectConnectionStatus = (s: ConnectionState) => s.status
export const selectGatewayVersion = (s: ConnectionState) => s.gatewayVersion
export const selectProviderConfigured = (s: ConnectionState) => s.providerConfigured
export const selectProviderModel = (s: ConnectionState) => s.providerModel
export const selectProviderContext = (s: ConnectionState) => s.providerContext
/** Active model's non-text input modalities — stable array reference. */
export const selectProviderModelInput = (s: ConnectionState) => s.providerModelInput
export const selectModelAccepts = (s: ConnectionState) => ({
  image: s.providerModelInput.includes("image"),
  video: s.providerModelInput.includes("video"),
  audio: s.providerModelInput.includes("audio"),
  pdf: s.providerModelInput.includes("pdf"),
})
export const selectExcelMcpInstalled = (s: ConnectionState) => s.excelMcpInstalled
/** Host Office app id — stable primitive, hook-safe. */
export const selectHostApp = (s: ConnectionState) => s.hostApp

