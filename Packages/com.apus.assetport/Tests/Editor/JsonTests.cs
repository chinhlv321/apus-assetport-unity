using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Apus.AssetPort.Tests
{
    /// <summary>Json runs inside the game (remote config): a parser bug only shows up on players' devices.</summary>
    public class JsonTests
    {
        [Test]
        public void ParsesEveryValueType()
        {
            var o = Json.AsObject(Json.Parse(" {\"s\":\"a\\n\\u00e9\\\"\",\"n\":-1.5e2,\"i\":7,\"t\":true,\"f\":false,\"z\":null,\"a\":[1,\"x\",{}]} "));
            Assert.AreEqual("a\né\"", Json.GetString(o, "s"));
            Assert.AreEqual(-150d, o["n"]);
            Assert.AreEqual(7L, Json.GetLong(o, "i"));
            Assert.AreEqual(true, o["t"]);
            Assert.AreEqual(false, o["f"]);
            Assert.IsNull(o["z"]);
            Assert.AreEqual(3, Json.AsArray(o["a"]).Count);
        }

        [TestCase("")]
        [TestCase("{")]
        [TestCase("{\"a\":1,}")]
        [TestCase("[1,]")]
        [TestCase("{\"a\":1} x")]
        [TestCase("01")]
        [TestCase("+1")]
        [TestCase(".5")]
        [TestCase("1.")]
        [TestCase("\"a\nb\"")]
        [TestCase("\"\\x\"")]
        [TestCase("{'a':1}")]
        [TestCase("tru")]
        public void RejectsInvalidJson(string text)
        {
            Assert.Throws<FormatException>(() => Json.Parse(text));
        }

        [Test]
        public void DeepNestingFailsInsteadOfOverflowingTheStack()
        {
            Assert.DoesNotThrow(() => Json.Parse(new string('[', 64) + new string(']', 64)));
            Assert.Throws<FormatException>(() => Json.Parse(new string('[', 65) + new string(']', 65)));
        }

        [Test]
        public void GetLongOnlyTakesIntegers()
        {
            var o = Json.AsObject(Json.Parse("{\"a\":3,\"b\":3.5,\"c\":\"3\",\"d\":1e30}"));
            Assert.AreEqual(3L, Json.GetLong(o, "a", -1));
            Assert.AreEqual(-1L, Json.GetLong(o, "b", -1));
            Assert.AreEqual(-1L, Json.GetLong(o, "c", -1));
            Assert.AreEqual(-1L, Json.GetLong(o, "d", -1));
            Assert.AreEqual(-1L, Json.GetLong(o, "missing", -1));
        }

        [Test]
        public void SerializeRoundTrips()
        {
            var value = new Dictionary<string, object>
            {
                ["s"] = "tab\t \"quote\" \\ \u0001 é",
                ["n"] = 0.1,
                ["l"] = 42L,
                ["b"] = true,
                ["z"] = null,
                ["a"] = new List<object> { 1d, "x" },
            };
            var back = Json.AsObject(Json.Parse(Json.Serialize(value)));
            Assert.AreEqual(value["s"], back["s"]);
            Assert.AreEqual(0.1, back["n"]);
            Assert.AreEqual(42L, Json.GetLong(back, "l"));
            Assert.AreEqual(true, back["b"]);
            Assert.IsNull(back["z"]);
            Assert.AreEqual("x", Json.AsArray(back["a"])[1]);
        }
    }
}
