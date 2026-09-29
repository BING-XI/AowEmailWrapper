using System.Linq;
using System.Windows.Forms;
using AowEmailWrapper.Helpers;
using Xunit;

namespace AowEmailWrapper.Tests
{
    public class ImageListTests
    {
        [Theory]
        [InlineData("Main", 9, "EmailWaiting", AowEmailWrapper.Games.AowGame.ZigguratIcon)]
        [InlineData("AccountsConfig", 21, "Other", "abv.bg")]
        [InlineData("MessageStoreList", 1, "Open", "Open")]
        public void EmbeddedImageListsLoadInDesignerOrderWithKeys(string listName, int count, string firstKey, string lastKey)
        {
            using (ImageList list = new ImageList())
            {
                ImageListLoader.Load(list, listName);

                Assert.Equal(count, list.Images.Count);
                Assert.Equal(firstKey, list.Images.Keys[0]);
                Assert.Equal(lastKey, list.Images.Keys[count - 1]);
                Assert.Equal(16, list.ImageSize.Width);
                Assert.Equal(ColorDepth.Depth32Bit, list.ColorDepth);

                //Forcing the native handle is what the designer's ListView does; it must not throw
                Assert.NotEqual(System.IntPtr.Zero, list.Handle);
            }
        }

        [Fact]
        public void ProviderIconsCoverTheDottedDomainNames()
        {
            using (ImageList list = new ImageList())
            {
                ImageListLoader.Load(list, "AccountsConfig");
                string[] keys = list.Images.Keys.Cast<string>().ToArray();
                Assert.Contains("mail.ru", keys);
                Assert.Contains("t-online.de", keys);
                Assert.Contains("googlemail.com", keys);
            }
        }
    }
}
