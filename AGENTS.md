# Game compatibility

The replacement is a desktop text utility. Keep selection conversion and snippet expansion available for desktop use.

- Provide an **Exit** action in the tray menu that ends the process and releases its global shortcuts and input hooks. The user can exit before opening a protected game and reopen the utility afterward.
- Keep the permissions Windows provides on a normal launch. Request administrator permissions only through an explicit choice for desktop use. Keep launch at sign-in opt-in. Do not add a driver, service, watchdog, game integration, or anti-cheat detection.
- Document the exit workflow and verify that it leaves no utility process running. Compatibility claims require testing with the specific game and anti-cheat version.
