using NUnit.Framework;

namespace Yozolab.DaerD.Tests
{
    public class PoCatalogTests
    {
        [Test]
        public void Parse_ReadsEntries_AndSkipsTheHeaderAndComments()
        {
            const string po =
                "# a comment\n" +
                "msgid \"\"\n" +
                "msgstr \"\"\n" +
                "\"Language: ja\\n\"\n" +
                "\n" +
                "#. extracted comment\n" +
                "msgid \"Add\"\n" +
                "msgstr \"追加\"\n" +
                "\n" +
                "msgid \"Delete\"\n" +
                "msgstr \"削除\"\n";

            var catalog = PoCatalog.Parse(po);

            Assert.AreEqual(2, catalog.Count, "the header entry is not a translation");
            Assert.AreEqual("追加", catalog["Add"]);
            Assert.AreEqual("削除", catalog["Delete"]);
        }

        [Test]
        public void Parse_JoinsContinuationLines_AndResolvesEscapes()
        {
            const string po =
                "msgid \"\"\n" +
                "\"Delete {0} item(s)?\\n\"\n" +
                "\"\\n\"\n" +
                "\"This cannot be undone.\"\n" +
                "msgstr \"\"\n" +
                "\"{0} 件を削除しますか？\\n\"\n" +
                "\"\\n\"\n" +
                "\"元に戻せません。\"\n";

            var catalog = PoCatalog.Parse(po);

            Assert.AreEqual("{0} 件を削除しますか？\n\n元に戻せません。",
                catalog["Delete {0} item(s)?\n\nThis cannot be undone."]);
        }

        [Test]
        public void Parse_KeepsQuotesAndBackslashes()
        {
            const string po =
                "msgid \"Use '.' in the \\\"name\\\"\"\n" +
                "msgstr \"\\\"名前\\\" に '.' を使います\"\n";

            var catalog = PoCatalog.Parse(po);

            Assert.AreEqual("\"名前\" に '.' を使います", catalog["Use '.' in the \"name\""]);
        }

        [Test]
        public void Parse_LeavesUntranslatedEntriesOut_SoTheEnglishShowsThrough()
        {
            const string po =
                "msgid \"Translated\"\n" +
                "msgstr \"訳あり\"\n" +
                "\n" +
                "msgid \"Not translated yet\"\n" +
                "msgstr \"\"\n";

            var catalog = PoCatalog.Parse(po);

            Assert.IsTrue(catalog.ContainsKey("Translated"));
            Assert.IsFalse(catalog.ContainsKey("Not translated yet"));
        }

        [Test]
        public void Parse_SkipsContextAndPluralEntries_WithoutDerailingTheNextOne()
        {
            const string po =
                "msgctxt \"menu\"\n" +
                "msgid \"Open\"\n" +
                "msgstr \"開く\"\n" +
                "\n" +
                "msgid \"{0} file\"\n" +
                "msgid_plural \"{0} files\"\n" +
                "msgstr[0] \"{0} 個のファイル\"\n" +
                "\n" +
                "msgid \"Plain\"\n" +
                "msgstr \"ふつう\"\n";

            var catalog = PoCatalog.Parse(po);

            Assert.IsFalse(catalog.ContainsKey("Open"), "contextual entries are not indexed");
            Assert.IsFalse(catalog.ContainsKey("{0} file"));
            Assert.AreEqual("ふつう", catalog["Plain"], "parsing recovers for the entries after");
        }

        [Test]
        public void Parse_HandlesEntriesWithNoBlankLineBetweenThem()
        {
            const string po =
                "msgid \"One\"\n" +
                "msgstr \"1\"\n" +
                "msgid \"Two\"\n" +
                "msgstr \"2\"\n";

            var catalog = PoCatalog.Parse(po);

            Assert.AreEqual("1", catalog["One"]);
            Assert.AreEqual("2", catalog["Two"]);
        }
    }
}
