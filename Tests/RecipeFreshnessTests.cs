using System;
using NUnit.Framework;
using Yozolab.DaerD.Authoring;
using Staleness = Yozolab.DaerD.Authoring.RecipeFreshness.Staleness;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The freshness guard's decision, held over facts rather than over an editor.
    ///
    /// What it exists for: a recipe's Build runs out of a LOADED assembly, so between saving the
    /// .cs and the domain reload that applies it, Generate rebuilds the controller from the
    /// previous version — silently, cleanly, with no warning. A compile error widens that window
    /// indefinitely, because Unity keeps the last assembly that built. The states worth pinning
    /// are exactly the ones nobody can reproduce on demand, which is why the decision takes
    /// timestamps and flags instead of an editor (the split <c>PrefabWriter.Judge</c> uses).
    ///
    /// The wiring is not mocked. Generate, Verify and Compare all ask this first, so a guard that
    /// wrongly refused a real recipe would take the whole recipe suite down with it — which is a
    /// louder test than any stub of the editor's flags would be.
    /// </summary>
    public class RecipeFreshnessTests
    {
        static readonly DateTime Built = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        static Staleness Judge(DateTime? assembly, DateTime? hand, DateTime? generated = null,
            bool compiling = false, bool failed = false) =>
            RecipeFreshness.Judge(assembly, hand, generated, compiling, failed);

        [Test]
        public void SourcesOlderThanTheAssemblyAreFresh()
        {
            Assert.AreEqual(Staleness.Fresh,
                Judge(Built, Built - TimeSpan.FromMinutes(5), Built - TimeSpan.FromMinutes(5)));
        }

        [Test]
        public void ACompileInFlightStopsTheRun()
        {
            Assert.AreEqual(Staleness.Compiling, Judge(Built, Built, compiling: true));
        }

        /// <summary>The failed compile outranks the compile in flight: a recompile finishes on
        /// its own, an error needs a person. The cost is that a recompile started right after
        /// fixing an error is announced as the error until it lands — both refuse the run either
        /// way, so only the sentence differs.</summary>
        [Test]
        public void AFailedCompileOutranksACompileInFlight()
        {
            Assert.AreEqual(Staleness.CompileFailed, Judge(Built, Built, failed: true));
            Assert.AreEqual(Staleness.CompileFailed,
                Judge(Built, Built, compiling: true, failed: true));
        }

        [Test]
        public void AHandHalfNewerThanTheAssemblyStopsTheRun()
        {
            Assert.AreEqual(Staleness.SourceNewer, Judge(Built, Built + TimeSpan.FromMinutes(1)));
        }

        /// <summary>Either half counts. The generated half is the one a re-export rewrites, so a
        /// run right after an export is exactly the case this catches.</summary>
        [Test]
        public void AGeneratedHalfNewerThanTheAssemblyStopsTheRunOnItsOwn()
        {
            Assert.AreEqual(Staleness.SourceNewer,
                Judge(Built, Built - TimeSpan.FromHours(1), Built + TimeSpan.FromMinutes(1)));
        }

        /// <summary>The slack is what stops the guard from refusing the very run it exists to
        /// make safe: a save and the compile it triggers routinely land in the same second.
        /// Exactly at the edge is still fresh; a millisecond past it is not.</summary>
        [Test]
        public void TheTwoSecondSlackIsInclusive()
        {
            Assert.AreEqual(Staleness.Fresh, Judge(Built, Built + RecipeFreshness.Slack));
            Assert.AreEqual(Staleness.SourceNewer,
                Judge(Built, Built + RecipeFreshness.Slack + TimeSpan.FromMilliseconds(1)));
            Assert.AreEqual(TimeSpan.FromSeconds(2), RecipeFreshness.Slack,
                "the slack this test describes and the one the guard uses have drifted apart");
        }

        /// <summary>
        /// Fail-open, in both directions. A missing assembly time (an assembly with no file on
        /// disk) or missing sources (a script Unity will not name) mean the comparison cannot be
        /// made, and a guard that refuses to run because it could not read a timestamp is a guard
        /// that gets switched off. This is the whole reason the guarantee is stated as
        /// best-effort rather than as "the code you are reading is the code that ran".
        /// </summary>
        [Test]
        public void MissingFactsFailOpen()
        {
            Assert.AreEqual(Staleness.Fresh, Judge(null, Built + TimeSpan.FromDays(7)),
                "no assembly time is not evidence of staleness");
            Assert.AreEqual(Staleness.Fresh, Judge(Built, null),
                "no source times is not evidence of staleness");
            Assert.AreEqual(Staleness.Fresh, Judge(null, null));
        }

        /// <summary>Missing facts stop nothing, but the flags are facts of their own — a compile
        /// in flight is known without any timestamp at all.</summary>
        [Test]
        public void TheFlagsStillDecideWithoutAnyTimestamps()
        {
            Assert.AreEqual(Staleness.Compiling, Judge(null, null, compiling: true));
            Assert.AreEqual(Staleness.CompileFailed, Judge(null, null, failed: true));
        }

        /// <summary>Every refusal has to be sayable. A verdict with no sentence would stop the
        /// run and put an empty bullet on screen, which teaches nothing.</summary>
        [Test]
        public void EveryRefusalHasASentenceAndFreshHasNone()
        {
            foreach (Staleness value in Enum.GetValues(typeof(Staleness)))
            {
                var reason = RecipeFreshness.Reason(value);
                if (value == Staleness.Fresh)
                    Assert.IsNull(reason, "Fresh must not produce a message");
                else
                    Assert.IsFalse(string.IsNullOrEmpty(reason), value + " has no sentence");
            }
        }
    }
}
