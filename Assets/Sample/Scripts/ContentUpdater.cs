using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Apus.Sample
{
    /// <summary>
    /// Startup step every game needs (copy it into your project): ask the CDN whether the live catalog changed and
    /// switch to it. Network problems never block the player — the game continues with the cached catalog (or the
    /// one from the last successful update).
    /// </summary>
    /// <remarks>
    /// The catalog URL is baked into the player at build time from the active Addressables profile
    /// (dev/staging/prod), so a QA build reads staging and a store build reads prod. After a publish on the portal,
    /// clients see the new catalog within ~60 s (CDN cache of catalog_content.hash).
    /// </remarks>
    public static class ContentUpdater
    {
        public enum Result
        {
            /// <summary>A newer catalog was downloaded and is now in use.</summary>
            Updated,
            /// <summary>The cached catalog is already the live one.</summary>
            UpToDate,
            /// <summary>Could not reach the CDN; continuing with the cached catalog.</summary>
            Offline,
        }

        /// <summary>Initializes Addressables and updates the catalog. <paramref name="done"/> always runs.</summary>
        public static IEnumerator Run(Action<Result, string> done)
        {
            var init = Addressables.InitializeAsync(false);
            yield return init;
            if (init.Status != AsyncOperationStatus.Succeeded)
            {
                // Init only fails when no catalog is usable at all (first launch offline with a remote-only setup).
                var error = Describe(init.OperationException);
                Addressables.Release(init);
                done(Result.Offline, error);
                yield break;
            }
            Addressables.Release(init);

            // autoReleaseHandle: false so we can read Status/Result before releasing.
            var check = Addressables.CheckForCatalogUpdates(false);
            yield return check;
            if (check.Status != AsyncOperationStatus.Succeeded)
            {
                var error = Describe(check.OperationException);
                Addressables.Release(check);
                Debug.LogWarning("[Content] CheckForCatalogUpdates failed, using cached catalog: " + error);
                done(Result.Offline, error);
                yield break;
            }

            var catalogs = check.Result;
            Addressables.Release(check);
            if (catalogs == null || catalogs.Count == 0)
            {
                done(Result.UpToDate, null);
                yield break;
            }

            var update = Addressables.UpdateCatalogs(catalogs, false);
            yield return update;
            var ok = update.Status == AsyncOperationStatus.Succeeded;
            var updateError = ok ? null : Describe(update.OperationException);
            Addressables.Release(update);
            if (!ok) Debug.LogWarning("[Content] UpdateCatalogs failed, using cached catalog: " + updateError);
            done(ok ? Result.Updated : Result.Offline, updateError);
        }

        static string Describe(Exception e) => e == null ? "unknown error" : e.GetBaseException().Message;
    }
}
