using System.Linq;
using NUnit.Framework;

namespace Apus.AssetPort.Tests
{
    /// <summary>
    /// Same cases as the portal's worker/test/shared.test.ts: ManifestValidator is a hand-made mirror of
    /// shared/src/index.ts, and a manifest this side accepts must not be rejected by the portal after a long build.
    /// </summary>
    public class ManifestValidatorTests
    {
        const string k_Md5 = "d41d8cd98f00b204e9800998ecf8427e";

        static ApusManifest Valid()
        {
            var m = new ApusManifest
            {
                project = "skyrise",
                platform = "Android",
                appVersion = "1.2.0",
                kind = PortalConstants.KindBaseline,
                unityVersion = "6000.0.75f1",
                addressablesVersion = "2.7.4",
                gitCommit = null,
                builtAt = "2026-11-23T00:00:00.000Z",
            };
            m.files.Add(new ManifestFile { name = "catalog_content.hash", size = 32, md5 = k_Md5 });
            m.files.Add(new ManifestFile { name = "catalog_content.json", size = 100, md5 = k_Md5 });
            m.files.Add(new ManifestFile { name = "remote group_assets_all_abc.bundle", size = 1000, md5 = k_Md5 });
            return m;
        }

        [Test]
        public void AcceptsAValidManifestAndItsJsonRoundTrip()
        {
            Assert.IsEmpty(ManifestValidator.Validate(Valid()));
            var back = ApusManifest.FromJson(Valid().ToJson());
            Assert.IsEmpty(ManifestValidator.Validate(back));
            Assert.IsNull(back.gitCommit);
            Assert.AreEqual(3, back.files.Count);
            Assert.AreEqual(1000, back.files[2].size);
        }

        [Test]
        public void RejectsLowercasePlatform()
        {
            var m = Valid();
            m.platform = "android";
            Assert.IsNotEmpty(ManifestValidator.Validate(m));
        }

        [Test]
        public void RequiresTheFixedCatalogNames()
        {
            var m = Valid();
            m.files.RemoveAll(f => f.name == "catalog_content.hash");
            Assert.IsNotEmpty(ManifestValidator.Validate(m));

            m = Valid();
            m.files.Add(new ManifestFile { name = "catalog_content.bin", size = 1, md5 = k_Md5 });
            Assert.IsNotEmpty(ManifestValidator.Validate(m));
        }

        [Test]
        public void RejectsDuplicatesUppercaseMd5AndItself()
        {
            var m = Valid();
            m.files.Add(m.files.Last());
            Assert.IsNotEmpty(ManifestValidator.Validate(m));

            m = Valid();
            m.files[2].md5 = k_Md5.ToUpperInvariant();
            Assert.IsNotEmpty(ManifestValidator.Validate(m));

            m = Valid();
            m.files.Add(new ManifestFile { name = PortalConstants.ManifestFileName, size = 1, md5 = k_Md5 });
            Assert.IsNotEmpty(ManifestValidator.Validate(m));
        }

        [Test]
        public void BundleSubdirMustEqualUnityVersion()
        {
            var m = Valid();
            m.bundleSubdir = m.unityVersion;
            Assert.IsEmpty(ManifestValidator.Validate(m));
            m.bundleSubdir = "6000.0.1f1";
            Assert.IsNotEmpty(ManifestValidator.Validate(m));
        }

        [TestCase("1.2")]
        [TestCase("1.2.3.4")]
        [TestCase("2.0.0-beta")]
        [TestCase("10")]
        public void AppVersionAllowsStoreStyleVersions(string v) => Assert.IsTrue(ManifestValidator.IsValidAppVersion(v));

        [TestCase("")]
        [TestCase(".")]
        [TestCase("..")]
        [TestCase("1/2")]
        [TestCase("1 2")]
        [TestCase("1.2\n")]
        [TestCase("123456789012345678901234567890123")]
        public void AppVersionIsNeverAPathSegment(string v) => Assert.IsFalse(ManifestValidator.IsValidAppVersion(v));

        [TestCase("remote group_assets_all_abc.bundle")]
        [TestCase("nhân_vật_ác.bundle")]
        [TestCase("日本語_assets.bundle")]
        [TestCase("grp[1]{x}!~,(a)+b=c@d.bundle")]
        [TestCase("café.bundle")]
        public void FileNamesAllowUnicodeAndSafePunctuation(string v) => Assert.IsTrue(ManifestValidator.IsValidFileName(v));

        [TestCase("a/b.bundle")]
        [TestCase(".hidden")]
        [TestCase("a\\b")]
        [TestCase("a#b")]
        [TestCase("a?b")]
        [TestCase("a%20b")]
        [TestCase("a\"b")]
        [TestCase("a'b")]
        [TestCase("a*b")]
        [TestCase("a:b")]
        [TestCase("a\nb")]
        [TestCase("a.bundle\n")]
        [TestCase(" lead")]
        [TestCase("x..y")]
        [TestCase("..")]
        public void FileNamesRejectPathAndUrlSpecials(string v) => Assert.IsFalse(ManifestValidator.IsValidFileName(v));

        [Test]
        public void FileNamesAreAtMost200Chars()
        {
            Assert.IsTrue(ManifestValidator.IsValidFileName(new string('a', 200)));
            Assert.IsFalse(ManifestValidator.IsValidFileName(new string('a', 201)));
        }

        [TestCase("skyrise", true)]
        [TestCase("a1", true)]
        [TestCase("a", false)]
        [TestCase("Skyrise", false)]
        [TestCase("-ab", false)]
        [TestCase("a_b", false)]
        public void Slug(string v, bool ok) => Assert.AreEqual(ok, ManifestValidator.IsValidSlug(v));
    }
}
