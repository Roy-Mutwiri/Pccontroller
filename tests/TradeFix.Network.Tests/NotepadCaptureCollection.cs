namespace TradeFix.Network.Tests;

/// <summary>
/// Groups every test that launches its own real notepad.exe for window-capture testing.
/// xUnit runs different test classes in parallel by default; without this, two Notepad-based
/// tests running concurrently would collide — this project's cleanup pattern kills *all* stray
/// "notepad" processes by name (belt-and-suspenders for the launcher-stub issue described in
/// WindowCaptureTests), which would kill a sibling test's still-in-use window mid-capture. Sharing
/// one named collection makes xUnit run these tests sequentially relative to each other while
/// still running in parallel with the rest of the suite.
/// </summary>
[CollectionDefinition("Notepad capture tests")]
public sealed class NotepadCaptureCollection
{
}
