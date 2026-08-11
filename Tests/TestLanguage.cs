using NUnit.Framework;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Pins the UI language for the whole run. Plenty of tests assert on the text of a message
    /// — that the analyzer says "themselves unreachable" rather than "no incoming transition",
    /// that a warning names what it is warning about — and those assertions only mean anything
    /// against a known language. Run on a Japanese editor they compare English against
    /// Japanese and fail, which is exactly what happened.
    ///
    /// Pinned rather than assigned: <see cref="L.OverrideLanguage"/> does not touch the
    /// preference, so running the tests cannot leave someone's editor in another language.
    /// </summary>
    [SetUpFixture]
    public class TestLanguage
    {
        [OneTimeSetUp]
        public void Pin() => L.OverrideLanguage(DaerDLanguage.English);

        [OneTimeTearDown]
        public void Release() => L.OverrideLanguage(null);
    }
}
