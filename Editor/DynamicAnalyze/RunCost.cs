using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// How big a run is about to be, worked out from the settings before any of it is computed.
    ///
    /// A batch run is computed whole and nothing draws until it finishes, so a run asked for by
    /// accident — an hour instead of a minute, eight other people instead of one — is an editor
    /// that has stopped answering with no way to say how long for. The number is cheap and the
    /// settings are all data, so it can simply be worked out and shown.
    ///
    /// Counted in SAMPLES, meaning one signal written once: frames × rows. Not seconds, and
    /// deliberately not — the wall-clock cost is dominated by stepping a real Animator per
    /// client per frame, which depends on the controller, the machine and what else Unity is
    /// doing, and a number of seconds guessed from that would be wrong in both directions. A
    /// sample count is arithmetic the settings answer exactly, and it moves with everything
    /// that makes a run expensive.
    /// </summary>
    static class RunCost
    {
        /// <summary>
        /// The rows a client gets per layer: where it is, whether it is moving, which
        /// transition it is moving through, and the layer's weight. Kept as a number here
        /// because this is an estimate made without building anything —
        /// <see cref="TraceRecorder"/> is where they are actually declared, and a row added
        /// there is a row to add here.
        /// </summary>
        const int RowsPerLayer = 4;

        /// <summary>
        /// Where a run stops being worth starting without being asked. About what a hundred
        /// parameters and twenty layers cost with four other people for a minute at 60 fps —
        /// 4.7 million, which is a big run somebody meant to ask for and is therefore not
        /// warned about. Past this, the usual cause is a field with an extra digit in it.
        ///
        /// A threshold rather than a refusal: the run that finds a bug is not this window's to
        /// decide, the same judgement <c>ComfortableRemotes</c> makes about how many people are
        /// worth simulating.
        /// </summary>
        public const long Uncomfortable = 5000000;

        /// <summary>
        /// Samples this run would record. <paramref name="parameters"/> and
        /// <paramref name="layers"/> are the controller's own counts — taken as numbers so the
        /// estimate can be checked without a controller, which is also what it is: arithmetic.
        /// </summary>
        public static long Samples(SimSettings settings, int parameters, int layers)
        {
            if (settings == null || settings.clock == null) return 0;
            return (long)settings.clock.Frames * Rows(settings, parameters, layers);
        }

        /// <summary>
        /// Rows a run of these settings declares — the same shape
        /// <see cref="TraceRecorder"/> builds, counted rather than made: every client's
        /// parameters and layers, the wire's own send/lost/arrived rows, and a lag row per
        /// parameter per other person.
        /// </summary>
        public static int Rows(SimSettings settings, int parameters, int layers)
        {
            if (settings == null) return 0;
            parameters = Mathf.Max(0, parameters);
            layers = Mathf.Max(0, layers);
            var wire = settings.wire;
            int remotes = wire != null ? wire.Remotes : 0;
            int rows = (1 + remotes) * (parameters + layers * RowsPerLayer);
            // One send for everybody, then a lost row and an arrived row each.
            if (wire != null) rows += 1 + 2 * remotes;
            if (wire != null && settings.lagRows) rows += remotes * parameters;
            return rows;
        }
    }
}
