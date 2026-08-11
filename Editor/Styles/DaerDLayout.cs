namespace Yozolab.DaerD
{
    /// <summary>
    /// The three widths that are a convention rather than a local decision: each recurs across
    /// files, and a row where one of them is off by two pixels reads as a misaligned column.
    ///
    /// Deliberately three. Most of the widths in DaerD are used once, in one panel, to fit one
    /// thing — those are layout, not vocabulary, and hauling them in here would turn a page of
    /// numbers into a page of names that still have to be looked up. The test for this file is
    /// the eye: if a column lines up, it is right.
    ///
    /// One number is missing on purpose. 56 is used for both a value field and a Select button,
    /// two roles that happen to want the same width today. Naming it would have to pick one and
    /// pretend the other agrees.
    /// </summary>
    static class DaerDLayout
    {
        /// <summary>A button holding one glyph — ✕, ↑, ↓, +, −, ▾. Square-ish, and the same
        /// square everywhere, because they sit at the end of rows that line up.</summary>
        public const float GlyphButton = 22f;

        /// <summary>A mini button inside a list row: Ping, Fix, Add. Wide enough for a short
        /// word, narrow enough that the row's real content keeps the space.</summary>
        public const float RowAction = 46f;

        /// <summary>A button on a window's bottom edge — Cancel, Apply, Create, Import. Every
        /// dialog DaerD opens ends in a row of these, and they are the same width in all of
        /// them.</summary>
        public const float DialogButton = 100f;
    }
}
