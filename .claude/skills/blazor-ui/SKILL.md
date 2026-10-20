---
name: blazor-ui
description: Conventions for the Blazor WebAssembly UI in ElevatorSystem.Web - folder structure, render mode, API and SignalR clients, component library and design tokens, the elevator shaft visualisation, accessibility (WCAG 2.2 AA), localisation (en-US and pt-BR), the admin area, error handling and bUnit/Playwright testing. Use when creating or changing any page, component, style or client-side service.
---

# Blazor UI

Decisions behind this skill: ADR-0006 (Blazor WebAssembly hosted by the API), ADR-0013 (the culture
preference lives in `localStorage`, not a cookie) and ADR-0016 (keeping the browser view in step with
the server).

## Structure (`src/ElevatorSystem.Web`)

```
Pages/               routable pages only, kept thin (Home hosts PassengerView in an ErrorBoundary)
Features/Passenger/  PassengerView, ElevatorShaft, HallButton, CarPanel, RequestQueue, ActivityFeed,
                     StatusLine, ConnectionBanner, ArrivalAnnouncer, Narration (contract values → words)
Features/Admin/      AdminView (sign-in → two-factor enrolment → dashboard), SignInForm, TwoFactorEnrolment,
                     ServiceControls, OccupancyPanel, AuditTrailPanel, StaffText (failures and actions → words, QR code)
Layout/              MainLayout: MudThemeProvider, app bar, error UI
Shared/              AppTheme (MudBlazor theme), CulturePicker
Services/            ElevatorApiClient, ElevatorHubClient, ElevatorStateStore, CulturePreference,
                     StaffApiClient (antiforgery, idempotency keys, StaffFailure), StaffSession
Resources/           SharedResource.resx (en-US, default), SharedResource.pt-BR.resx, Text (the keys)
wwwroot/css/         tokens.css (design tokens), app.css (all custom styles)
```

`wwwroot` of this project is served at the site root (`/css/tokens.css`), MudBlazor at
`/_content/MudBlazor/`. The API's `Components/App.razor` links them.

## Rules

- **Render mode** is `InteractiveWebAssembly` with prerendering **off**, set once in the API's
  `App.razor`. Components run only in the browser.
- **The UI reaches data only through the API:** a same-origin `HttpClient` (cookies sent by the
  browser) and the SignalR hub. It references `ElevatorSystem.Contracts` only, and the architecture
  tests enforce that.
- **Services sit behind small interfaces** so components test against fakes:
  - `IElevatorApiClient` (`ElevatorApiClient`): `GetConfigurationAsync`, `GetStatusAsync`,
    `RequestElevatorAsync` (hall call), `RequestDestinationAsync` (car call). JSON via
    `ContractsJsonContext`. A refusal becomes a `RequestOutcome` with a `RejectionReason` read from the
    ProblemDetails `reason` extension (exact enum names only) and a localised message. 429, 502–504,
    network failures and unknown bodies map to fixed messages; server text is never shown.
  - `IElevatorHubClient` (`ElevatorHubClient`): connects to `ElevatorHub.Path`, inserts
    `ContractsJsonContext` first in the JSON protocol's resolver chain, and feeds the store.
    `ReconnectBackoff` retries at 0, 2, 5, 10, 20, then every 30 s, forever. The first connection
    retries on the same schedule through `TimeProvider`.
- **State:**
  - `ElevatorStateStore` holds the configuration, the newest status, the last 30 activity entries
    (newest first) and the connection state (`Connecting`, `Connected`, `Reconnecting`).
  - A status with an equal or lower `Version` is ignored, except the first one after the connection
    was lost, which comes from the live server and always wins (ADR-0016).
  - Components subscribe to `Changed` (or `ActivityReceived` for single steps), re-render with
    `InvokeAsync(StateHasChanged)` and unsubscribe in `Dispose`.
  - The UI never guesses the elevator's state: lamps go out when the status says so, not on click.
  - A request already on its way for the same call is not sent again (holding Enter sends one).
- **Components:** MudBlazor (MIT) for layout and chrome (`MudLayout`, `MudAppBar`, `MudPaper`,
  `MudGrid`, `MudAlert`, `MudButton`). The shaft, buttons, queue chips and feed are custom HTML and CSS.
  Give MudBlazor alerts an explicit `role` (`alert` for refusals, `status` for the connection banner).
- **Design tokens:**
  - colours, spacing, radius, floor height and motion durations are `--es-*` custom properties in
    `tokens.css`, with a `prefers-color-scheme: dark` block. `app.css` never hard-codes a colour.
  - `Shared/AppTheme` builds the MudBlazor theme from the same hex values; `ThemeTokenTests` fails if
    the two drift, if a dark value is missing, or if any text pair drops below 4.5:1 (3:1 for
    outlines and the focus ring).
  - MudBlazor follows the system light/dark preference through `GetSystemDarkModeAsync`.

## Elevator visualisation

- `ElevatorShaft` renders one landing per floor, top floor first, beside the shaft. The shaft's
  height is `--es-floor-count` × `--es-floor-height`.
- The car is positioned inline with
  `transform: translateY(calc(-(floor - LowestFloor) * var(--es-floor-height)))` and
  `transition-duration` equal to `TravelTimePerFloor` (the server moves one floor per step).
- Doors are two panels that slide apart while the status is `DoorOpen` (`--es-door-duration`).
  `prefers-reduced-motion` sets every transition to 0 s and keeps the state changes.
- Out of service: the doorway is hatched and the status line shows a warning icon and the cause.
- Hall buttons:
  - the top floor has no ▲ and the bottom floor has no ▼ (an empty slot keeps the columns aligned)
  - a button is lit (`aria-pressed="true"`, lamp colour, ring, filled arrow) while the status lists a
    pending hall call for that floor and direction
- Car buttons are lit the same way (filled lamp dot) for pending car calls.
- The FIFO queue is an ordered list of chips in `PendingCalls` order, with merged request counts,
  so people can see why the car passes a floor.

## Accessibility (WCAG 2.2 AA)

- Real `<button type="button">` elements, with accessible names such as "Call elevator going up from
  floor 3" and "Go to floor 5". Button groups have names ("Hall buttons, floor 3").
- Everything works from the keyboard. One `:focus-visible` outline (3 px, `--es-color-focus`) for
  custom and MudBlazor controls. Targets are at least 24×24 px (hall buttons 36 px).
- `ArrivalAnnouncer` is an `aria-live="polite"` region that announces door openings ("Floor 5, doors
  opening") and service changes, like a real car. Moving between floors stays quiet.
- The shaft is `role="img"` named "Car at floor 4, doors open". The car's landing has
  `aria-current="location"`.
- State is never shown by colour alone: lit buttons change shape (filled arrow or dot) and gain a
  ring, refused feed entries get a ⚠, the current landing gets a bar and bold number.
- The scrollable activity feed is a focusable, named region.
- The page has a visually hidden `h1`; each panel is a `section` named by its `h2`.

## Localisation

- Cultures: `en-US` (default, in the main assembly) and `pt-BR` (satellite assembly).
- Strings come from `IStringLocalizer<SharedResource>` with keys from `Resources/Text`. Every key
  must exist in both .resx files with the same `{n}` placeholders; `ResourceFileTests` checks that.
- `CulturePreference` applies the culture in `Program.cs` before the first render: the stored choice
  (`localStorage` key `elevator.culture`, only `en-US` or `pt-BR` accepted), else `pt-BR` for a
  Portuguese browser, else `en-US`. It also sets `<html lang>`. The picker saves and reloads.
  `localStorage`, not a cookie (ADR-0013), is a preference only, never anything sensitive.
- `BlazorWebAssemblyLoadAllGlobalizationData` is on. Times and numbers use the current culture
  (`2,5 s` in pt-BR).

## Admin area

- `/admin` (`Pages/Admin.razor`) shows one step at a time from `StaffSession`: the sign-in form, the
  two-factor enrolment for a password-only session, then the dashboard. There is no client-side
  authorization framework: **hiding UI is convenience, not security**; the API enforces every rule
  (ADR-0022), and an auditor simply gets no service controls.
- `StaffApiClient` fetches the antiforgery token before the first `POST`, forgets it when the session
  changes, retries once on an `Antiforgery` refusal, and reports refusals as `StaffFailure`; components
  show `StaffText.For(failure)`, never server text.
- After signing in or out, `StaffSession.ChangeAsync` restarts the hub so the server re-evaluates the
  staff group; occupancy arrives only on staff connections and is cleared on sign-out.
- The enrolment QR code is drawn in the browser (QRCoder, a PNG data URI): the key goes to no other service.
- Destructive or safety actions (emergency stop, maintenance mode, data deletion) need a
  confirmation dialog that names the effect. Each confirmed service action gets one `Idempotency-Key`,
  reused if the request is retried after a refusal.

## Errors

- One `ErrorBoundary` per feature area, with a localised message.
- ProblemDetails responses from the API are mapped to localised, human messages. Raw exception
  text is never shown. If the configuration can't be loaded, the view offers "Try again".

## Security in the browser

The WebAssembly bundle is public code:
- no secrets, keys or tokens in it
- nothing sensitive in `localStorage` (only `elevator.culture`)
- no inline scripts, so the CSP stays strict (`'wasm-unsafe-eval'` is the only relaxation). This is
  why `App.razor` has no `<ImportMap />`. Inline `style` attributes are allowed (`'unsafe-inline'` in
  `style-src`); MudBlazor needs them and the car uses them.
- logs from the client carry exception types, never messages or URLs

## Tests (`tests/ElevatorSystem.Web.Tests`)

- **bUnit** for every component with logic or conditional rendering. Derive from `WebTestContext`:
  real localisation, MudBlazor and `ElevatorStateStore`, with `FakeElevatorApiClient` and
  `FakeElevatorHubClient`, and loose JS interop. Play the server by calling the store
  (`Store.ApplyStatus`, `Store.AddActivity`, `Store.SetConnection`). No network.
- Every test runs in en-US (`[assembly: UseCulture("en-US")]`); mark a class or test
  `[UseCulture("pt-BR")]` for Portuguese.
- `TestData` builds contract objects with the real building's defaults.
- Client services are tested directly: `ElevatorApiClient` with a stub `HttpMessageHandler`, the hub's
  JSON options against SignalR's defaults.
- **Playwright (.NET)** end-to-end tests with axe checks come with the E2E project (phase 5).

```bash
dotnet test --project tests/ElevatorSystem.Web.Tests
```
