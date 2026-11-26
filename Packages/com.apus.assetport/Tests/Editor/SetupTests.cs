using NUnit.Framework;

namespace Apus.AssetPort.Tests
{
    public class SetupTests
    {
        const string k_Staging = "https://cdn.example.com/live/skyrise/staging-abc123/Android/1.2.0";

        // Shape of Library/com.unity.addressables/aa/<platform>/settings.json.
        static string SettingsJson(string loadPath) =>
            "{\"m_CatalogLocations\":[{\"m_Keys\":[\"AddressablesMainContentCatalogRemoteHash\"],\"m_InternalId\":\"" + loadPath + "/catalog_content.hash\"}]}";

        [Test]
        public void PlayerBuildGuard_passes_when_built_catalog_url_matches()
        {
            Assert.IsNull(PlayerBuildGuard.Problem(SettingsJson(k_Staging), k_Staging));
            Assert.IsNull(PlayerBuildGuard.Problem(SettingsJson(k_Staging), k_Staging + "/"));
        }

        [Test]
        public void PlayerBuildGuard_fails_on_other_env_or_app_version()
        {
            var prod = "https://cdn.example.com/live/skyrise/prod/Android/1.2.0";
            StringAssert.Contains(k_Staging, PlayerBuildGuard.Problem(SettingsJson(k_Staging), prod));
            Assert.IsNotNull(PlayerBuildGuard.Problem(SettingsJson(k_Staging), k_Staging.Replace("1.2.0", "1.2.1")));
            // "1.2.0" must not match a build made for "1.2.01".
            Assert.IsNotNull(PlayerBuildGuard.Problem(SettingsJson(k_Staging + "1"), k_Staging));
        }

        [Test]
        public void PlayerBuildGuard_fails_without_built_content_or_remote_catalog()
        {
            Assert.IsNotNull(PlayerBuildGuard.Problem(null, k_Staging));
            StringAssert.Contains("không có remote catalog", PlayerBuildGuard.Problem("{\"m_CatalogLocations\":[]}", k_Staging));
        }

        [Test]
        public void IsIgnored_matches_whole_id_only()
        {
            var ignored = new[] { " Bundle-Naming ", "" };
            Assert.IsTrue(ConfigChecker.IsIgnored(ignored, "bundle-naming"));
            Assert.IsFalse(ConfigChecker.IsIgnored(ignored, "bundle"));
            Assert.IsFalse(ConfigChecker.IsIgnored(ignored, "content-state-path"));
            Assert.IsFalse(ConfigChecker.IsIgnored(null, "bundle-naming"));
        }
    }
}
