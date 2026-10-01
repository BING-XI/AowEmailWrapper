using System.Drawing;
using System.IO;
using System.Xml.Serialization;
using AowEmailWrapper.ConfigFramework;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>The window's size is kept in the preferences, at 96 dpi, and only a sensible size is taken back.</summary>
    public class WindowSizeTests
    {
        [Theory]
        [InlineData("900,700", 900, 700)]
        [InlineData("1600,1000", 1600, 1000)]
        public void A_saved_size_is_read_back(string saved, int width, int height)
        {
            Assert.Equal(new Size(width, height), Main.ParseWindowSize(saved));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("900")]
        [InlineData("900,700,1")]
        [InlineData("-900,700")]
        [InlineData("wide,tall")]
        [InlineData("100,100")]
        [InlineData("50000,700")]
        public void Anything_else_gives_the_default_size(string saved)
        {
            Assert.True(Main.ParseWindowSize(saved).IsEmpty);
        }

        [Fact]
        public void The_size_is_kept_in_the_preferences_file()
        {
            PreferencesConfigValues preferences = new PreferencesConfigValues(true) { WindowSize = "900,700" };
            XmlSerializer serializer = new XmlSerializer(typeof(PreferencesConfigValues));
            StringWriter writer = new StringWriter();
            serializer.Serialize(writer, preferences);
            PreferencesConfigValues back = (PreferencesConfigValues)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.Equal("900,700", back.WindowSize);
        }
    }
}
