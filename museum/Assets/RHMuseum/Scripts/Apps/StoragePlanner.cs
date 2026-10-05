using System;
using System.Collections.Generic;
using System.Linq;

namespace RHMuseum.Apps
{
    /// <summary>An installed project app as the storage manager sees it.</summary>
    public class InstalledApp
    {
        public string package;
        public long bytes;               // on-device footprint estimate (APK size × InstallFactor)
        public DateTime lastPlayedUtc;   // install time if never played
        public float rating;             // Bayesian average 1..5; 3 when unknown
    }

    /// <summary>
    /// Pure storage policy (no Unity types, so it's unit-tested with dotnet).
    /// CLAUDE.md: "when full, uninstall least-recently-played first, weighted by rating".
    ///   evictScore = hoursSinceLastPlayed × (6 − rating) / 3
    /// A 5-star app waits 4× longer than a 1-star app before it's evicted; ties go to the bigger app.
    /// </summary>
    public static class StoragePlanner
    {
        public const double InstallFactor = 1.6;            // APK -> installed size (extracted libs/assets, OBB-less builds)
        public static readonly TimeSpan RecentlyPlayed = TimeSpan.FromMinutes(15);

        public static long Footprint(long apkBytes) => (long)(apkBytes * InstallFactor);

        public static double EvictScore(InstalledApp a, DateTime nowUtc)
        {
            double hours = Math.Max(0, (nowUtc - a.lastPlayedUtc).TotalHours);
            float rating = a.rating <= 0 ? 3f : Math.Min(5f, Math.Max(1f, a.rating));
            return hours * (6 - rating) / 3.0;
        }

        /// <summary>
        /// Which apps to uninstall so <paramref name="needBytes"/> more fits under <paramref name="budgetBytes"/>.
        /// Protected apps (the batch being installed, recently played) are never chosen.
        /// Returns null when it can't fit even after evicting everything allowed.
        /// </summary>
        public static List<string> PlanEvictions(IEnumerable<InstalledApp> installed, long usedBytes, long needBytes,
                                                 long budgetBytes, ICollection<string> protectedPackages, DateTime nowUtc)
        {
            var plan = new List<string>();
            long over = usedBytes + needBytes - budgetBytes;
            if (over <= 0) return plan;
            var candidates = installed
                .Where(a => !protectedPackages.Contains(a.package) && nowUtc - a.lastPlayedUtc > RecentlyPlayed)
                .OrderByDescending(a => EvictScore(a, nowUtc))
                .ThenByDescending(a => a.bytes);
            foreach (var a in candidates)
            {
                plan.Add(a.package);
                over -= a.bytes;
                if (over <= 0) return plan;
            }
            return null;
        }

        /// <summary>Preload: best-rated apps first, as many as fit in the budget.</summary>
        public static List<T> PreloadSelection<T>(IEnumerable<T> apps, Func<T, long> bytes, Func<T, float> rating,
                                                  long usedBytes, long budgetBytes)
        {
            var picked = new List<T>();
            long room = budgetBytes - usedBytes;
            foreach (var a in apps.OrderByDescending(rating))
            {
                long b = bytes(a);
                if (b <= room)
                {
                    picked.Add(a);
                    room -= b;
                }
            }
            return picked;
        }

        /// <summary>Enough free disk to download an APK and keep a safety margin for the OS.</summary>
        public static bool CanDownload(long apkBytes, long freeDiskBytes, long reserveBytes = 1L << 30) =>
            freeDiskBytes - apkBytes - Footprint(apkBytes) >= reserveBytes;
    }
}
