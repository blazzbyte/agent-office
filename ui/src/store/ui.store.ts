import { create } from "zustand"

// ---------------------------------------------------------------------------
// UI preferences (purely local — they never leave the taskpane's localStorage)
// ---------------------------------------------------------------------------
export type RenderStyle = "bubbles" | "flat"

const RENDER_STYLE_KEY = "agent-office:render-style"

function loadRenderStyle(): RenderStyle {
  try {
    return localStorage.getItem(RENDER_STYLE_KEY) === "flat" ? "flat" : "bubbles"
  } catch {
    return "bubbles"
  }
}

interface UiState {
  renderStyle: RenderStyle
  setRenderStyle: (style: RenderStyle) => void
}

export const useUiStore = create<UiState>((set) => ({
  renderStyle: loadRenderStyle(),
  setRenderStyle: (style) => {
    try {
      localStorage.setItem(RENDER_STYLE_KEY, style)
    } catch {
      /* private mode etc. — the in-memory switch still applies */
    }
    set({ renderStyle: style })
  },
}))

export const selectRenderStyle = (s: UiState) => s.renderStyle
