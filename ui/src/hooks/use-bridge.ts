import { useEffect } from "react"
import {
  useBridgeStore,
  selectBridgeStatus,
  selectBridgeHealth,
} from "@/store/bridge.store"

const REPROBE_INTERVAL_MS = 30_000

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------
export function useBridge() {
  const status = useBridgeStore(selectBridgeStatus)
  const health = useBridgeStore(selectBridgeHealth)
  const checkBridge = useBridgeStore((s) => s.checkBridge)

  useEffect(() => {
    void checkBridge()
    const timer = setInterval(() => void checkBridge(), REPROBE_INTERVAL_MS)
    return () => clearInterval(timer)
  }, [checkBridge])

  return {
    status,
    health,
    /** `true` when the native executor responded to the last probe. */
    isConnected: status === "connected",
    version: health?.version ?? null,
    /** Manually re-run the probe. */
    checkBridge,
  }
}
