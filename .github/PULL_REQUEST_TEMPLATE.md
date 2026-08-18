## What & why

<!-- What does this PR do, and why? Link issues with "Closes #N". -->

## How to verify

<!-- Steps a reviewer can follow to confirm it works (dev loop: pnpm build +
MSBuild + restart Excel, or MSI install). -->

## Checklist

- [ ] `pnpm --dir ui build` passes (chat UI changes)
- [ ] Solution builds in Debug **and** Release (add-in changes)
- [ ] Tested the full loop in Excel (badges connect, action round-trips)
- [ ] No secrets, `.pfx`, or generated artifacts committed
- [ ] Docs updated if behavior or setup changed (README / CONTRIBUTING / docs/)
