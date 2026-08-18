import { TooltipProvider } from "@/components/ui/tooltip"
import { ChatSection } from "@/sections/chat-section"

/**
 * Root shell of the Agent Office taskpane.
 * Providers live here; the ChatSection renders the full chat UI.
 */
export function App(): React.JSX.Element {
  return (
    <TooltipProvider>
      <ChatSection />
    </TooltipProvider>
  )
}

export default App
