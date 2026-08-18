import { useCallback, useEffect } from "react"
import {
  useConnectionStore,
  selectConnectionConfig,
  selectConnectionStatus,
  selectGatewayVersion,
  selectHostApp,
  selectProviderConfigured,
  selectProviderModel,
} from "@/store/connection.store"
import type { AgentConnectionConfig } from "@/types/session"

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------
export function useConnection() {
  const config = useConnectionStore(selectConnectionConfig)
  const status = useConnectionStore(selectConnectionStatus)
  const gatewayVersion = useConnectionStore(selectGatewayVersion)
  const hostApp = useConnectionStore(selectHostApp)
  const providerConfigured = useConnectionStore(selectProviderConfigured)
  const providerModel = useConnectionStore(selectProviderModel)
  const checkHealthAction = useConnectionStore((s) => s.checkHealth)
  const setConfigAction = useConnectionStore((s) => s.setConfig)
  const replaceConfigAction = useConnectionStore((s) => s.replaceConfig)

  // Run once on mount — gives an immediate health indication.
  useEffect(() => {
    void checkHealthAction()
  }, [checkHealthAction])

  /** Merge a partial config update and persist it. */
  const updateConfig = useCallback(
    (patch: Partial<AgentConnectionConfig>) => {
      setConfigAction(patch)
    },
    [setConfigAction],
  )

  /** Replace the full config (e.g. from the settings form). */
  const replaceConfig = useCallback(
    (next: AgentConnectionConfig) => {
      replaceConfigAction(next)
    },
    [replaceConfigAction],
  )

  /** Manually re-run the health check. */
  const checkHealth = useCallback(() => {
    void checkHealthAction()
  }, [checkHealthAction])

  return {
    config,
    status,
    gatewayVersion,
    hostApp,
    providerConfigured,
    providerModel,
    updateConfig,
    replaceConfig,
    checkHealth,
    /** `true` when the gateway is confirmed reachable. */
    isConnected: status === "healthy",
    /** `true` while the health check is in flight. */
    isChecking: status === "checking",
  }
}
