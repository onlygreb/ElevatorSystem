// When this tab goes away, its passenger leaves the building (ADR-0036). A reload sends the same notice, so the reloaded
// page says at once that its passenger is still here, long before the app has started; within the server's grace period
// that calls the departure off.
const passengerPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function thisTabsPassenger() {
    try {
        const passengerId = sessionStorage.getItem('elevator.passenger');
        return passengerId && passengerPattern.test(passengerId) ? passengerId : null;
    } catch {
        return null;
    }
}

const returning = thisTabsPassenger();
if (returning) {
    fetch(`/api/v1/passengers/${returning}`, { cache: 'no-store' }).catch(() => { });
}

addEventListener('pagehide', () => {
    const passengerId = thisTabsPassenger();
    if (passengerId) {
        navigator.sendBeacon(`/api/v1/passengers/${passengerId}/depart`);
    }
});
