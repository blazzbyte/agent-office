import { create } from "zustand"
import { probeBridge } from "@/services/bridge.service"
import { logger } from "@/lib/utils/logger"
import type { BridgeHealth } from "@/types/bridge"

// ---------------------------------------------------------------------------
// State shape
// ---------------------------------------------------------------------------

export type BridgeStatus = "idle" | "checking" | "connected" | "unavailable"

/** Highest bridge protocol this add-in understands. */
const SUPPORTED_BRIDGE_PROTOCOL = 1

interface BridgeState {
  status: BridgeStatus
  health: BridgeHealth | null
  lastChecked: number | null

  // Actions
  /** Probe `/bridge/v1/health` and update `status`. */
  checkBridge: () => Promise<void>
}

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

export const useBridgeStore = create<BridgeState>((set, get) => ({
  status: "idle",
  health: null,
  lastChecked: null,

  checkBridge: async () => {
    // Only show the transient "checking" state on the very first probe —
    // background re-probes must not flicker the UI badge.
    if (get().status === "idle") set({ status: "checking" })
    const health = await probeBridge()

    // Newer bridge protocol: stay in core mode to avoid mis-executing actions.
    if (health && (health.protocol ?? 1) > SUPPORTED_BRIDGE_PROTOCOL) {
      logger.warn("Bridge protocol newer than supported — staying in core mode", {
        bridgeProtocol: health.protocol,
        supported: SUPPORTED_BRIDGE_PROTOCOL,
      })
      set({ status: "unavailable", health: null, lastChecked: Date.now() })
      return
    }

    set({
      status: health ? "connected" : "unavailable",
      health,
      lastChecked: Date.now(),
    })
  },
}))

// ---------------------------------------------------------------------------
// Selectors & imperative helpers
// ---------------------------------------------------------------------------

export const selectBridgeStatus = (s: BridgeState) => s.status
export const selectBridgeHealth = (s: BridgeState) => s.health

/** True when the connected bridge advertises support for the given action. */
export function bridgeSupports(action: string): boolean {
  const { health } = useBridgeStore.getState()
  return !!health && health.actions.includes(action)
}
