using System.Text;
using PulseTerm.Terminal.Emulation;

namespace PulseTerm.Terminal.Tests;

[TestClass]
[TestCategory("Emulation")]
public class AltScreenCursorTests
{
    // ESC as its own literal is safe; never inline "\x1b7" — the \x escape greedily eats the 7.
    private const string ESC = "\x1b";

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.ASCII.GetBytes(s));

    [TestMethod]
    public void Exit1049_RestoresMainCursor_EvenWhenAppUsedDecscInAltScreen()
    {
        var e = new TerminalEmulator(80, 24);

        // Main-screen cursor at (col 5, row 3) — where "nano file" was launched.
        Feed(e, ESC + "[4;6H");
        Assert.AreEqual(5, e.CursorX);
        Assert.AreEqual(3, e.CursorY);

        // Enter alt screen (DECSET 1049 saves the main cursor).
        Feed(e, ESC + "[?1049h");
        Assert.IsTrue(e.IsAlternateScreen);

        // The app moves around and uses DECSC inside the alt screen — this must NOT clobber the
        // main cursor saved by 1049. Without the fix, exit restores this (10,10) onto the main
        // screen, landing the cursor "over old content".
        Feed(e, ESC + "[11;11H");
        Feed(e, ESC + "7");        // DECSC in alt screen
        Feed(e, ESC + "[1;1H");

        // Exit alt screen (DECRST 1049) — cursor returns to the MAIN position, not the alt one.
        Feed(e, ESC + "[?1049l");
        Assert.IsFalse(e.IsAlternateScreen);
        Assert.AreEqual(5, e.CursorX);
        Assert.AreEqual(3, e.CursorY);
    }

    [TestMethod]
    public void DecscDecrc_StillWorkIndependentlyOfAltScreen()
    {
        var e = new TerminalEmulator(80, 24);

        Feed(e, ESC + "[2;3H"); // (col 2, row 1)
        Feed(e, ESC + "7");     // DECSC saves (2, 1)
        Feed(e, ESC + "[10;10H");
        Feed(e, ESC + "8");     // DECRC restores (2, 1)

        Assert.AreEqual(2, e.CursorX);
        Assert.AreEqual(1, e.CursorY);
    }
}
