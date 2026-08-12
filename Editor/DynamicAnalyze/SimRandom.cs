namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// The only source of randomness in a run, and a deterministic one: xorshift32 over a seed
    /// the caller chose. UnityEngine.Random would do the arithmetic just as well and would also
    /// share its state with everything else in the editor, which is exactly what a repeatable
    /// run cannot have.
    ///
    /// A struct passed by ref, so the sequence a caller draws from is its own: the clock's
    /// jitter and a Random parameter driver each get one, and neither can shift the other's
    /// numbers by drawing more or fewer of its own.
    /// </summary>
    struct SimRandom
    {
        uint _state;

        public SimRandom(int seed)
        {
            // Zero is the one state xorshift cannot leave, so it is spent rather than kept.
            _state = seed == 0 ? 0x9e3779b9u : (uint)seed;
        }

        public uint Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }

        /// <summary>[0, 1). 24 bits, which is every value a float can tell apart in that
        /// range.</summary>
        public float NextFloat() => (Next() & 0xFFFFFFu) / (float)0x1000000;

        /// <summary>[-1, 1).</summary>
        public float NextSigned() => NextFloat() * 2f - 1f;

        public float NextRange(float min, float max) => min + (max - min) * NextFloat();

        /// <summary>True with the given probability. 0 never, 1 always.</summary>
        public bool NextChance(float chance) => NextFloat() < chance;
    }
}
