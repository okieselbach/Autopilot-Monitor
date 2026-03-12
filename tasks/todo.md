# Static Landing Page (No-JS Fallback)

## Goal
Make the landing page render full static HTML content server-side, so crawlers and users without JavaScript see the complete page (without animations/interactivity).

## Plan

### Architecture Change
Convert `page.tsx` from a full `"use client"` component to a **Server Component** that renders all static content as HTML, with small Client Components extracted only for interactive parts.

### What becomes static (Server Component):
- [ ] Hero section (headline, description, feature bullets)
- [ ] "How It Works" timeline section
- [ ] Comparison table
- [ ] Footer / CTA sections
- [ ] All text, images, layout, and CSS animations

### What stays as Client Components (extracted):
- [ ] `LoginButton.tsx` — Auth login button (needs MSAL context)
- [ ] `PlatformStats.tsx` — Live stats counter (fetches JSON, shows defaults as fallback)
- [ ] `AuthRedirect.tsx` — Redirect logic for already-authenticated users
- [ ] Theme-dependent styling (already handled by ThemeProvider in layout)

### Steps
1. [ ] Read and understand full `page.tsx` content
2. [ ] Identify all `"use client"` dependencies (hooks, browser APIs)
3. [ ] Extract interactive parts into small Client Components
4. [ ] Remove `"use client"` from `page.tsx` — make it a Server Component
5. [ ] Ensure CSS animations still work (they do — CSS is server-rendered)
6. [ ] Test that the static HTML contains all visible content
7. [ ] Verify no regressions in interactive behavior

### What users without JS will see:
- Full page content (text, images, layout)
- CSS animations (keyframes work without JS)
- Hover effects (CSS-only)
- Static default values for platform stats
- Login button visible but non-functional (acceptable)

### What they won't see:
- Live platform stats (JS fetch)
- Working login flow (requires MSAL)
- Auth-based redirects
- Theme toggle functionality
