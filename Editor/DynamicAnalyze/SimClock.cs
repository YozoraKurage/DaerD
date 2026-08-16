using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// The time the run is made of: how many frames, how long each one is, and how unevenly.
    /// Mecanim is only ever told a delta, so this IS the simulated clock — nothing else in
    /// DynamicAnalyze reads a wall clock, which is what makes a run reproducible.
    ///
    /// The frame COUNT is fixed by <see cref="fps"/> and <see cref="seconds"/>; jitter varies
    /// the lengths, not the count. So a jittered run covers slightly more or less simulated
    /// time than it was asked for, and the trace records the time it actually reached rather
    /// than the time it was aiming at. The alternative — run until the clock passes N seconds —
    /// makes the frame count depend on the noise, and then two runs of "the same" settings have
    /// different lengths and cannot be laid side by side.
    ///
    /// Jitter exists because a controller that only works at exactly 60 fps works nowhere: a
    /// dwell measured in normalized time, a smoothing built on feedback and anything reading
    /// the frame delta all move when the frame does. A seeded run is repeatable; changing the
    /// seed is how the same settings are asked a second question.
    /// </summary>
    sealed class SimClock
    {
        /// <summary>Frames per simulated second, before jitter.</summary>
        public float fps = 60f;
        /// <summary>How long the run covers, before jitter.</summary>
        public float seconds = 10f;
        /// <summary>How far a frame may stray from its nominal length, as a fraction of it.
        /// 0 is a perfectly even clock; 0.5 lets a frame be half again as long or half as
        /// short. Capped below 1, because a frame of length zero is not a frame.</summary>
        public float jitter;
        /// <summary>Fixes the jitter. Same seed, same settings, same run — which is the whole
        /// reason the noise is generated here rather than taken from the editor.</summary>
        public int seed = 1;

        /// <summary>The shortest frame that will ever be handed to Mecanim. A frame of zero
        /// advances nothing and would read as a hang rather than as fast.</summary>
        public const float MinimumStep = 1e-4f;

        public const float MaximumJitter = 0.95f;

        /// <summary>Frames the run will take. Fixed by the settings, not by the noise.</summary>
        public int Frames => Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(0f, seconds) * NominalFps));

        public float NominalFps => Mathf.Max(1f, fps);

        /// <summary>The length every frame would have with no jitter.</summary>
        public float NominalStep => 1f / NominalFps;

        /// <summary>
        /// The whole run's frame lengths, worked out up front. An array rather than a sequence
        /// so the schedule can be shown, compared and asserted on without running anything —
        /// the noise is part of the experiment, not an accident inside it.
        /// </summary>
        public float[] Steps()
        {
            var steps = new float[Frames];
            float nominal = NominalStep;
            float spread = Mathf.Clamp(jitter, 0f, MaximumJitter);
            var random = new SimRandom(seed);
            for (int i = 0; i < steps.Length; i++)
                steps[i] = spread <= 0f
                    ? nominal
                    : Mathf.Max(MinimumStep, nominal * (1f + spread * random.NextSigned()));
            return steps;
        }
    }
}
