---
name: blazor-ui
description: Conventions for the Blazor WebAssembly UI in ElevatorSystem.Web - folder structure, render mode, API and SignalR clients, component library and design tokens, the elevator shaft visualisation, accessibility (WCAG 2.2 AA), localisation (en-US and pt-BR), the admin area, error handling and bUnit/Playwright testing. Use when creating or changing any page, component, style or client-side service.
---

# Blazor UI

Decision behind this skill: ADR-0006 (Blazor WebAssembly hosted by the API).

## Structure (`src/ElevatorSystem.Web`)

```
Pages/               routable pages only, kept thin
Features/Passenger/  shaft visualisation, hall buttons, car panel, live queue, event log
Features/Admin/      dashboard, controls, audit viewer, users, privacy requests
Shared/              layout, buttons, status badges, dialogs
Services/            ElevatorApiClient (HTTP), ElevatorHubClient (SignalR), ElevatorStateStore
Resources/           .resx files: en-US is the default, pt-BR the translation
wwwroot/css/         tokens.css (design tokens), app.css
```

## Rules

- **Render mode** is `InteractiveWebAssembly` with prerendering **off**, set once in the API's
  `App.razor`. Components run only in the browser.
- **The UI reaches data only through the API:** a typed `HttpClient` (same origin, cookies sent
  automatically) and the SignalR hub. It references `ElevatorSystem.Contracts` only, and the
  architecture tests enforce that.
- **State:**
  - `ElevatorStateStore` holds the last server snapshot and the connection status; components subscribe to it
  - The UI never guesses the elevator's state locally; it renders the last snapshot
  - On disconnect it shows a "Reconnecting…" banner and retries with backoff
- **Components:** MudBlazor (MIT) for admin chrome (tables, forms, dialogs, snackbars). The shaft
  visualisation is custom HTML and CSS.
- **Design tokens:**
  - colours, spacing, radius and motion durations are CSS custom properties in `tokens.css`, mapped into the MudBlazor theme
  - light and dark themes
  - text contrast at least 4.5:1
- **No hard-coded user-facing strings.** Use `IStringLocalizer<T>` and add every key to both `.resx` files.

## Elevator visualisation

- The car's vertical position is a CSS `transform: translateY(...)` driven by the snapshot floor.
  The transition duration equals the configured travel time per floor.
- Doors animate on `DoorOpen`. `prefers-reduced-motion` turns the animations off and keeps the
  state changes.
- Hall buttons:
  - floor 10 has no ▲ and floor 1 has no ▼
  - a pressed button stays lit (`aria-pressed="true"`) until the request is served, according to the server snapshot
- The FIFO queue is drawn as ordered chips, so people can see why the car skips a floor.

## Accessibility (WCAG 2.2 AA)

- Real `<button>` elements, with accessible names such as "Call elevator going up from floor 3".
- Everything works from the keyboard, with a visible focus indicator. Targets are at least 24×24 px.
- An `aria-live="polite"` region announces arrivals ("Floor 5, doors opening"), like a real car.
- State is never shown by colour alone: there's always a text or icon as well.

## Localisation

- Cultures: `en-US` (default) and `pt-BR`. The picker stores the choice in `localStorage`, not
  in a cookie (ADR-0013). It's a preference only, never anything sensitive.
- `BlazorWebAssemblyLoadAllGlobalizationData` is enabled, and dates and numbers are formatted
  with the current culture.

## Admin area

- Routes under `/admin` use `[Authorize(Policy = ...)]` and `AuthorizeView`. **Hiding UI is
  convenience, not security**: the API enforces every rule again.
- Destructive or safety actions (emergency stop, maintenance mode, data deletion) need a
  confirmation dialog that names the effect.

## Errors

- One `ErrorBoundary` per feature area.
- ProblemDetails responses from the API are mapped to localised, human messages. Raw exception
  text is never shown.

## Security in the browser

The WebAssembly bundle is public code:
- no secrets, keys or tokens in it
- nothing sensitive in `localStorage`
- no inline scripts, so the CSP stays strict (`'wasm-unsafe-eval'` is the only relaxation)

## Tests

- **bUnit** for every component with logic or conditional rendering: button availability per
  floor, lamp states, reconnect banner, localised text.
- **Playwright (.NET)** end-to-end tests for the main flows, with axe accessibility checks on every page.
