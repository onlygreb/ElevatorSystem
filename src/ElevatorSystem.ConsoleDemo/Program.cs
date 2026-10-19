using ElevatorSystem.ConsoleDemo;

// Runs the elevator core (one car, floors 1-10, FIFO scheduling) against Domain + Application only,
// so its behaviour can be explored in seconds without Docker. See DemoApp for the options.
return await DemoApp.RunAsync(args);
