---
name: blazor-ui
description: Conventions for the Blazor WebAssembly UI in ElevatorSystem.Web - folder structure, render mode, API and SignalR clients, component library and design tokens, the building visualisation and the passenger journey, accessibility (WCAG 2.2 AA), localisation (en-US and pt-BR), the admin area, error handling and bUnit/Playwright testing. Use when creating or changing any page, component, style or client-side service.
---

# Blazor UI

Decisions behind this skill: ADR-0006 (Blazor WebAssembly hosted by the API), ADR-0013 (the culture
preference lives in `localStorage`, not a cookie) and ADR-0016 (keeping the browser view in step with
the server).

## Structure (`src/ElevatorSystem.Web`)

```
Pages/               routable pages only, kept thin (Home hosts PassengerView in an ErrorBoundary)
Features/Passenger/  PassengerView, JourneyPanel, CarPanel, RequestQueue, ActivityFeed, StatusLine,
                     ConnectionBanner, ArrivalAnnouncer, Narration (contract values → words)
Features/Building/   BuildingView (floors, people, shaft and car), DoorCountdown (ADR-0025)
Features/Admin/      AdminView (sign-in → two-factor enrolment → dashboard), SignInForm, TwoFactorEnrolment,
                     DemoScenarios, ServiceControls, OccupancyPanel, AuditTrailPanel, StaffText (failures and actions → words, QR code)
Layout/              MainLayout: MudThemeProvider, app bar, error UI
Shared/              AppTheme (MudBlazor theme), CulturePicker
Services/            ElevatorApiClient, PassengerApiClient, PassengerJourney, ElevatorHubClient,
                     ElevatorStateStore, CulturePreference,
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
  - `IElevatorApiClient` (`ElevatorApiClient`): `GetConfigurationAsync` and `GetStatusAsync`. JSON via
    `ContractsJsonContext`.
  - `IPassengerApiClient` (`PassengerApiClient`): the passenger steps of ADR-0026 (`JoinAsync`,
    `GetAsync`, `CallElevatorAsync`, `BoardAsync`, `ChooseFloorAsync`, `LeaveAsync`). An accepted step
    returns a `RequestOutcome` with its request id; 409, 429, 502–504, network failures and unknown
    bodies map to fixed localised messages; server text is never shown.
  - `PassengerJourney`: this tab's passenger. A random id in `sessionStorage` (`elevator.passenger`, ADR-0035)
    lets a reload pick the journey up again; where the passenger is follows their own activities
    from the hub. `Stage(status)` gives `Outside`, `OnFloor`, `Waiting`, `DoorOpenHere` or `InCar`,
    and the journey panel offers only what that stage allows. A refused step's
    `PassengerRefusalReason` is shown until the next step.
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
  - A step already on its way is not sent again: buttons are disabled while `PassengerJourney.Busy`.
- **Components:** MudBlazor (MIT) for layout and chrome (`MudLayout`, `MudAppBar`, `MudAlert`,
  `MudButton`, icons). Panels (`.es-panel`), the hero, the building, the journey buttons (`.es-cta`),
  queue chips, feed and demo cards are custom HTML and CSS.
  Give MudBlazor alerts an explicit `role` (`alert` for refusals, `status` for the connection banner).
- **Design tokens:**
  - colours, spacing, radius, floor height and motion durations are `--es-*` custom properties in
    `tokens.css`, with a `prefers-color-scheme: dark` block. `app.css` never hard-codes a colour.
  - `Shared/AppTheme` builds the MudBlazor theme from the same hex values; `ThemeTokenTests` fails if
    the two drift, if a dark value is missing, or if any text pair drops below 4.5:1 (3:1 for
    outlines and the focus ring).
  - MudBlazor follows the system light/dark preference through `GetSystemDarkModeAsync`.

## Building visualisation

- `BuildingView` renders one storey per floor, top floor first, beside the shaft; the lowest floor is
  named the lobby. The shaft's height is `--es-floor-count` × `--es-floor-height`.
- People are dots: waiting (with a ring) and present on each floor, riding inside the car. A group
  shows at most `MaxDots` dots, the last one reading "+n". Counts come from `BuildingOccupancyDto`,
  which only staff receive; a passenger sees only themself, marked "You" on their floor or in the car.
- The car is positioned inline with
  `transform: translateY(calc(-(floor - LowestFloor) * var(--es-floor-height)))` and
  `transition-duration` equal to `TravelTimePerFloor` (the server moves one floor per step). Its display
  shows the floor and an arrow while moving.
- Doors are two panels that slide apart while the status is `DoorOpen` (`--es-door-duration`).
  `prefers-reduced-motion` sets every transition and animation to 0 s and keeps the state changes.
- `DoorCountdown` shows how long the doors stay open: a bar that empties towards `DoorsCloseAt`
  (redrawn every 200 ms through `TimeProvider`), or a calm "waiting for a call" note while the car
  holds its doors open with nowhere to go (ADR-0025).
  - It compares `DoorsCloseAt` with `ElevatorStateStore.ServerNow`, never the browser's clock: the store
    learns the server's clock offset from each status's `TakenAt` (largest sample, reset on reconnection).
  - It ends `LagAllowance` (1 s) before the doors close, so a last-moment press still arrives in time.
- Out of service: the car is hatched and the status line shows a warning icon and the cause.
- Car buttons are lit (`aria-pressed="true"`, lamp colour, glow) while the status lists a pending car
  call for that floor.
- The FIFO queue is an ordered list of chips in `PendingCalls` order, with merged request counts,
  so people can see why the car passes a floor.

## Passenger journey

Every passenger starts outside and enters the building on the lowest floor. The journey panel shows
five steps (arrive, call, enter, choose, leave) and, per stage:
- `OnFloor`: call buttons (no ▲ on the top floor, no ▼ on the lowest).
- `Waiting`: animated dots; "call again" appears if the car left without them.
- `DoorOpenHere`: "Enter the elevator" with the door countdown.
- `InCar`: the car panel, then "Leave the elevator", enabled only while the doors are open. A rider
  who doesn't leave rides on until they leave or choose another floor.

## Accessibility (WCAG 2.2 AA)

- Real `<button type="button">` elements with visible text or names such as "Go to floor 5". The car
  panel is a named group.
- Everything works from the keyboard. One `:focus-visible` outline (3 px, `--es-color-focus`) for
  custom and MudBlazor controls. Targets are at least 24×24 px (journey buttons 52 px, car buttons 48 px).
- `ArrivalAnnouncer` is an `aria-live="polite"` region that announces door openings ("Floor 5, doors
  opening") and service changes, like a real car. Moving between floors stays quiet.
- The shaft is `role="img"` named "Car at floor 4, doors open". Each storey's people are named
  ("2 waiting, 1 on the floor"); the dots themselves are hidden from screen readers. The current
  journey step has `aria-current="step"`, and "where you are" is a polite live region.
- State is never shown by colour alone: lit buttons gain a ring and glow, feed entries have an icon
  per kind ("!" for refusals), your own entries a "You" tag and demo ones a "Demo" tag, and the car's
  floor gets a filled number.
- The scrollable activity feed is a focusable, named region.
- The page has a visually hidden `h1`; each panel is a `section` named by its `h2`. The hero's title
  is a paragraph, so the outline stays page → panels.

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
- Admins get the demo scenarios (ADR-0027): three cards that start simulated passengers, and a stop
  button. A 409 means the server has `Simulation:Enabled` off, and the panel says so.
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
- nothing sensitive in `localStorage`: only `elevator.culture` and `elevator.passenger`, a random id
  that names no person (ADR-0026)
- no inline scripts, so the CSP stays strict (`'wasm-unsafe-eval'` is the only relaxation). This is
  why `App.razor` has no `<ImportMap />`. Inline `style` attributes are allowed (`'unsafe-inline'` in
  `style-src`); MudBlazor needs them and the car uses them.
- logs from the client carry exception types, never messages or URLs

## Tests (`tests/ElevatorSystem.Web.Tests`)

- **bUnit** for every component with logic or conditional rendering. Derive from `WebTestContext`:
  real localisation, MudBlazor, `ElevatorStateStore` and `PassengerJourney`, a `FakeTimeProvider`
  stopped at `TestData.Noon` (`Clock`), `FakeElevatorApiClient`, `FakePassengerApiClient`
  (`Passengers`), `FakeElevatorHubClient` and `FakeStaffApiClient`, and loose JS interop. Play the
  server by calling the store (`Store.ApplyStatus`, `Store.AddActivity` with the passenger's own
  `PassengerId`, `Store.SetConnection`). No network.
- Every test runs in en-US (`[assembly: UseCulture("en-US")]`); mark a class or test
  `[UseCulture("pt-BR")]` for Portuguese.
- `TestData` builds contract objects with the real building's defaults.
- Client services are tested directly: the API clients with `StubHandler`, the hub's
  JSON options against SignalR's defaults.
- **Playwright (.NET)** end-to-end tests with axe checks come with the E2E project (phase 5).

```bash
dotnet test --project tests/ElevatorSystem.Web.Tests
```
