using System.IO;
using System.Reflection;
using UnityEditor.PackageManager;
// UnityEditor has a PackageInfo of its own (the asset-store kind), so the package manager's has
// to be named outright — the same reason PrefabWriter aliases it.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Yozolab.DaerD.Bridge
{
    /// <summary>
    /// The DD version string stamped onto saved data (<see cref="GraphFrameData.savedByVersion"/>)
    /// so a controller carries, by itself, which DD build last wrote it.
    ///
    /// <para>WHY NOT PackageSource.Registry AS THE DEV SIGNAL.</para>
    /// A VPM/VCC install copies the package straight into <c>Packages/</c> rather than resolving
    /// it through the registry cache, so Unity reports it as <c>Embedded</c> — indistinguishable
    /// BY SOURCE ALONE from a package somebody is actively developing in place. A release archive
    /// never carries a <c>.git</c> folder (nor does one land in a worktree checkout, where it is
    /// a file instead of a directory, but either counts), so that presence is the actual signal
    /// that this copy can drift from the tag it was resolved at. <c>Local</c> and <c>Git</c>
    /// sources are treated the same way on the same grounds: neither is a resolved, addressed
    /// copy either.
    /// </summary>
    static class SavedByVersion
    {
        static string s_cached;
        static bool s_resolved;

        /// <summary>
        /// The string to stamp, resolved once per domain reload and cached — resolving asks the
        /// package manager for this assembly's package, which is not a thing to do on every save.
        /// </summary>
        public static string Current
        {
            get
            {
                if (!s_resolved)
                {
                    s_cached = Resolve();
                    s_resolved = true;
                }
                return s_cached;
            }
        }

        static string Resolve()
        {
            var package = PackageInfo.FindForAssembly(Assembly.GetExecutingAssembly());
            if (package == null) return Format(null, null, false);
            return Format(package.version, package.source, HasDotGit(package.resolvedPath));
        }

        static bool HasDotGit(string resolvedPath)
        {
            if (string.IsNullOrEmpty(resolvedPath)) return false;
            var marker = Path.Combine(resolvedPath, ".git");
            // A worktree checkout's .git is a FILE (it points at the real one); a normal
            // checkout's is a directory. Either one answers "this is a working checkout".
            return Directory.Exists(marker) || File.Exists(marker);
        }

        /// <summary>
        /// Pure: facts in, the string to stamp out. <paramref name="source"/> being Local or Git,
        /// or <paramref name="hasDotGit"/>, reads as a development build past its named release —
        /// <c>"X.Y.Z+dev"</c>. Everything else (Registry, or Embedded with no <c>.git</c>) reads
        /// as the release it names — bare <c>"X.Y.Z"</c>. An empty or null
        /// <paramref name="version"/> fails open to <c>""</c>: no stamp beats a wrong one.
        /// </summary>
        public static string Format(string version, PackageSource? source, bool hasDotGit)
        {
            if (string.IsNullOrEmpty(version)) return "";
            bool dev = hasDotGit || source == PackageSource.Local || source == PackageSource.Git;
            return dev ? version + "+dev" : version;
        }
    }
}
