import * as React from "react"
import { Eye, EyeOff } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { cn } from "@/lib/utils"
function PasswordInput({ className, ...props }: React.ComponentProps<"input">) {
  const [show, setShow] = React.useState(false)

  return (
    <div className="relative" data-slot="password-input">
      <Input
        type={show ? "text" : "password"}
        data-slot="input"
        className={cn("hide-password-toggle pr-8", className)}
        {...props}
      />
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        tabIndex={-1}
        className="absolute top-0 right-0 h-full text-muted-foreground/60 hover:bg-transparent hover:text-foreground"
        onClick={() => setShow((prev) => !prev)}
        aria-label={show ? "Hide value" : "Show value"}
        title={show ? "Hide value" : "Show value"}
      >
        {show ? (
          <EyeOff size={14} aria-hidden="true" />
        ) : (
          <Eye size={14} aria-hidden="true" />
        )}
      </Button>

      {/* Hide the native browser reveal/clear toggles */}
      <style>{`
        .hide-password-toggle::-ms-reveal,
        .hide-password-toggle::-ms-clear {
          visibility: hidden;
          pointer-events: none;
          display: none;
        }
      `}</style>
    </div>
  )
}

export { PasswordInput }
