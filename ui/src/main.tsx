import { StrictMode } from "react"
import { createRoot } from "react-dom/client"

import "./index.css"
import App from "./App.tsx"
import { ThemeProvider } from "@/components/theme-provider.tsx"
import { useI18nStore } from "@/i18n"

// Resolve the UI language before the first paint (bounded, so a slow host
// never blocks the taskpane — it then renders in the browser language).
const languageReady = Promise.race([
  useI18nStore.getState().load(),
  new Promise((resolve) => setTimeout(resolve, 1500)),
])

void languageReady.finally(() => {
  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <ThemeProvider>
        <App />
      </ThemeProvider>
    </StrictMode>
  )
})
