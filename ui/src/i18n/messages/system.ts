import { defineMessages } from "../define"

export const system = defineMessages({
  en: {
    "system.sessionInitFailed": "Session init failed",
    "system.sessionCreateFailedBackend": "Failed to create session — check backend connection",
    "system.sessionCreateFailed": "Session creation failed",
    "system.sessionNotFound": "Session {sessionId} not found on gateway",
    "system.sessionSwitchFailed": "Failed to switch session",
    "system.streamEmptyBody": "Empty response body",
    "system.streamReadError": "Stream read error",
    "system.gatewayError": "Gateway error {status}: {statusText}",
    "system.backendConnectFailed": "Failed to connect to the agent backend",
    "system.chatErrorLine": "⚠️ An error occurred: {message}\n\nPlease try again.",
    "system.bridgeAuthRejected":
      "The native engine rejected authentication (Excel may have restarted). Reload the taskpane and try again.",
    "system.bridgeRequestFailed": "Native engine request failed (HTTP {status}).",
    "system.bridgeUnreachable": "The native engine is unreachable. Restart Excel and try again.",
  },
  es: {
    "system.sessionInitFailed": "No se pudo iniciar la sesión",
    "system.sessionCreateFailedBackend": "No se pudo crear la sesión — revisa la conexión con el backend",
    "system.sessionCreateFailed": "No se pudo crear la sesión",
    "system.sessionNotFound": "No se encontró la sesión {sessionId} en el gateway",
    "system.sessionSwitchFailed": "No se pudo cambiar de sesión",
    "system.streamEmptyBody": "La respuesta llegó vacía",
    "system.streamReadError": "Error al leer la respuesta en streaming",
    "system.gatewayError": "Error del gateway {status}: {statusText}",
    "system.backendConnectFailed": "No se pudo conectar con el backend del agente",
    "system.chatErrorLine": "⚠️ Ocurrió un error: {message}\n\nIntenta de nuevo.",
    "system.bridgeAuthRejected":
      "El motor nativo rechazó la autenticación (es posible que Excel se haya reiniciado). Recarga el panel e intenta de nuevo.",
    "system.bridgeRequestFailed": "Falló la solicitud al motor nativo (HTTP {status}).",
    "system.bridgeUnreachable": "No se puede contactar con el motor nativo. Reinicia Excel e intenta de nuevo.",
  },
})
