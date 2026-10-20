using System;
using System.Collections;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Apus.Sample
{
    /// <summary>
    /// On-demand download of remote content by label (DLC packs, new levels…): ask how many bytes are missing from the
    /// local cache, download with progress, then load as usual. Bundles are cached by Unity (Caching), so the second
    /// time GetDownloadSize returns 0 and nothing is downloaded.
    /// </summary>
    public static class ContentDownloader
    {
        /// <summary>Bytes still to download for <paramref name="key"/> (0 = everything already cached).</summary>
        public static IEnumerator GetDownloadSize(object key, Action<long, string> done)
        {
            var size = Addressables.GetDownloadSizeAsync(key);
            yield return size;
            if (size.Status == AsyncOperationStatus.Succeeded) done(size.Result, null);
            else done(-1, size.OperationException?.GetBaseException().Message ?? "unknown error");
            Addressables.Release(size);
        }

        /// <summary>Downloads every bundle needed by <paramref name="key"/>; <paramref name="progress"/> gets 0..1.</summary>
        public static IEnumerator Download(object key, Action<float> progress, Action<string> done)
        {
            var download = Addressables.DownloadDependenciesAsync(key, false);
            while (!download.IsDone)
            {
                // GetDownloadStatus() counts bytes; PercentComplete also counts loading steps.
                progress(download.GetDownloadStatus().Percent);
                yield return null;
            }
            progress(1f);
            var error = download.Status == AsyncOperationStatus.Succeeded ? null : download.OperationException?.GetBaseException().Message ?? "unknown error";
            Addressables.Release(download);
            done(error);
        }

        /// <summary>Removes the cached bundles of <paramref name="key"/> (to test the download again).</summary>
        public static IEnumerator ClearCache(object key, Action done)
        {
            var clear = Addressables.ClearDependencyCacheAsync(key, false);
            yield return clear;
            Addressables.Release(clear);
            done();
        }
    }
}
