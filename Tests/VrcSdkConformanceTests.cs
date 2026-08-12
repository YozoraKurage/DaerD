using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// What DaerD believes about the VRChat SDK, checked against the SDK. Everything DaerD
    /// knows about the Parameter Driver is reached by NAME — the serialized field names it
    /// reads and writes, the numbering of the change types, and the callback that decides when
    /// a driver happens — because DaerD carries no reference to the SDK and must work without
    /// it. A name is a fine way to talk to something and a terrible way to remember it: the
    /// SDK renames a field or adds one, and everything still compiles, still runs, and quietly
    /// does less.
    ///
    /// So this reads the shipped type and fails when the two drift. It is skipped when the SDK
    /// is absent, which is also when none of it could be wrong.
    /// </summary>
    public class VrcSdkConformanceTests
    {
        /// <summary>
        /// The SDK's own driver, or a skipped test. Found the way DaerD finds it — by name —
        /// which without the SDK finds the stub beside these tests instead. The stub answers to
        /// the same name and has none of the ancestry, and that is exactly how to tell them
        /// apart: nothing here can be wrong about an SDK that is not installed.
        /// </summary>
        static Type Base()
        {
            var driver = VrcParameterDriver.FindType();
            for (var type = driver; type != null; type = type.BaseType)
                if (type.Name == "VRC_AvatarParameterDriver")
                    return type;
            Assert.Ignore("The VRChat SDK is not installed in this project.");
            return null;
        }

        static readonly BindingFlags Members =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void TheChangeTypesAreTheNumbersDaerDWritesDown()
        {
            var kinds = Base().GetNestedType("ChangeType", Members | BindingFlags.Static);
            Assert.IsNotNull(kinds);
            // ControllerIR.DriverEntry.kind says "0 = Set, 1 = Add, 2 = Random, 3 = Copy" and
            // every builder and reader in DaerD writes those numbers by hand.
            var expected = new Dictionary<string, int>
            {
                { "Set", 0 }, { "Add", 1 }, { "Random", 2 }, { "Copy", 3 },
            };
            foreach (var pair in expected)
            {
                Assert.IsTrue(Enum.IsDefined(kinds, pair.Key), "no ChangeType." + pair.Key);
                Assert.AreEqual(pair.Value, (int)Enum.Parse(kinds, pair.Key),
                    "ChangeType." + pair.Key + " moved");
            }
        }

        [Test]
        public void TheDriverIsStillAListOfParametersAndALocalOnlyFlag()
        {
            var owner = Base();
            Assert.IsNotNull(owner.GetField("parameters", Members), "the list DaerD walks");
            Assert.IsNotNull(owner.GetField("localOnly", Members),
                "how DaerD tells the wearer's drivers from everyone's");
        }

        [Test]
        public void EveryFieldDaerDReadsIsStillCalledThat()
        {
            var parameter = Base().GetNestedType("Parameter", Members | BindingFlags.Static);
            Assert.IsNotNull(parameter);
            foreach (var name in new[]
                     {
                         "type", "name", "source", "value", "valueMin", "valueMax", "chance",
                         "preventRepeats", "convertRange",
                         "sourceMin", "sourceMax", "destMin", "destMax",
                     })
                Assert.IsNotNull(parameter.GetField(name, Members),
                    "DaerD reads and writes '" + name + "' by name, and the SDK no longer has it");
        }

        /// <summary>
        /// The other direction, and the one that catches silence: a field DaerD does not carry
        /// is a field the exporter drops on the way through. This lists the ones it knowingly
        /// ignores, so an SDK that grows a new one fails here instead of quietly losing it on
        /// somebody's controller.
        /// </summary>
        [Test]
        public void NothingNewHasAppearedForDaerDToLose()
        {
            var parameter = Base().GetNestedType("Parameter", Members | BindingFlags.Static);
            var carried = new HashSet<string>
            {
                "type", "name", "source", "value", "valueMin", "valueMax", "chance",
                "preventRepeats", "convertRange",
                "sourceMin", "sourceMax", "destMin", "destMax",
            };
            // Object references to a parameter asset rather than a name. DaerD works in names
            // throughout, and a controller that used these would be describing something the
            // rest of the tooling has no way to talk about.
            var knowinglyIgnored = new HashSet<string> { "sourceParam", "destParam" };

            var strangers = new List<string>();
            foreach (var field in parameter.GetFields(Members))
                if (!carried.Contains(field.Name) && !knowinglyIgnored.Contains(field.Name))
                    strangers.Add(field.Name + " : " + field.FieldType.Name);
            CollectionAssert.IsEmpty(strangers,
                "the SDK's driver grew fields DaerD neither carries nor ignores on purpose; "
                + "a controller using them loses them on export");
        }

        /// <summary>
        /// WHEN a driver happens, which DD DynamicAnalyze has to model rather than run. The SDK
        /// overrides OnStateEnter and nothing else, so a driver is a thing that happens on the
        /// way into a state — not while it is held, and not on the way out.
        /// </summary>
        [Test]
        public void ADriverHappensOnTheWayIntoAState()
        {
            var owner = Base();
            var declared = new HashSet<string>();
            foreach (var method in owner.GetMethods(Members | BindingFlags.DeclaredOnly))
                if (method.Name.StartsWith("OnState", StringComparison.Ordinal))
                    declared.Add(method.Name);

            CollectionAssert.Contains(declared, "OnStateEnter");
            foreach (var name in new[] { "OnStateUpdate", "OnStateExit", "OnStateMachineEnter" })
                CollectionAssert.DoesNotContain(declared, name,
                    "the driver started doing something at another moment than state entry");
        }
    }
}
