using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Controls;
using AowEmailWrapper.Helpers;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// The Aliases tab's list: one name per address, kept between runs, and shown in place of the
    /// address wherever the Wrapper shows one. The names in use are process-wide, so these tests run
    /// on their own.
    /// </summary>
    [Collection(nameof(ProcessStateCollection))]
    public class AliasTests : IDisposable
    {
        private const string Bob = "bob@example.com";
        private const string BobAtWork = "bob@work.example.org";
        private const string Carol = "carol@example.org";

        private readonly AliasList _before = AliasHelper.Current;

        public void Dispose()
        {
            AliasHelper.Current = _before;
        }

        private static AliasList BobAndCarol()
        {
            AliasList list = new AliasList();
            list.Set("Bob the Builder", Bob, null);
            list.Set("Bob the Builder", BobAtWork, null);
            list.Set("Carol", Carol, null);
            return list;
        }

        [Fact]
        public void An_address_has_one_name_and_a_name_may_have_several_addresses()
        {
            AliasList list = BobAndCarol();

            Assert.Equal("Bob the Builder", list.NameFor(" BOB@example.com "));
            Assert.Equal("Bob the Builder", list.NameFor(BobAtWork));
            Assert.Null(list.NameFor("dave@example.net"));
            Assert.Equal(new[] { "Bob the Builder", "Carol" }, list.Names);

            //Naming an address again renames it rather than adding a second entry
            list.Set("Robert", Bob, null);
            Assert.Equal(3, list.Aliases.Count);
            Assert.Equal("Robert", list.NameFor(Bob));

            //An edit may change the address: the old one goes
            list.Set("Carol", "carol@new.example.org", Carol);
            Assert.Null(list.NameFor(Carol));
            Assert.Equal("Carol", list.NameFor("carol@new.example.org"));

            Assert.True(list.Remove(BobAtWork));
            Assert.False(list.Remove(BobAtWork));
            Assert.Equal(2, list.Aliases.Count);
        }

        [Fact]
        public void The_list_survives_its_file_and_a_copy_is_independent()
        {
            AliasList list = BobAndCarol();
            XmlSerializer serializer = new XmlSerializer(typeof(AliasList));
            string xml;
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, list);
                xml = writer.ToString();
            }
            AliasList back;
            using (StringReader reader = new StringReader(xml))
            {
                back = (AliasList)serializer.Deserialize(reader);
            }

            Assert.Equal(3, back.Aliases.Count);
            Assert.Equal("Carol", back.NameFor(Carol));

            AliasList copy = back.Clone();
            copy.Remove(Carol);
            Assert.Equal("Carol", back.NameFor(Carol));
        }

        [Fact]
        public void Addresses_show_as_names_wherever_the_player_named_them()
        {
            AliasHelper.Current = BobAndCarol();

            Assert.Equal("Carol", AliasHelper.Display(Carol));
            Assert.Equal("dave@example.net", AliasHelper.Display("dave@example.net"));
            Assert.Null(AliasHelper.Display(null));
            Assert.Equal("Carol (carol@example.org), dave@example.net", AliasHelper.DisplayListWithAddresses(Carol + ";dave@example.net;"));
        }

        [Fact]
        public void Only_whole_addresses_in_a_message_are_replaced()
        {
            AliasList list = BobAndCarol();
            list.Set("$1 Dollar", "dollar@example.com", null);
            AliasHelper.Current = list;

            Assert.Equal("Sent to [Bob the Builder] successfully.", AliasHelper.InText("Sent to [BOB@example.com] successfully."));
            Assert.Equal("Carol has held 'x.asg' since 1 Oct. Carol sent it.", AliasHelper.InText("carol@example.org has held 'x.asg' since 1 Oct. carol@example.org sent it."));
            //Another address that merely contains a named one stays as it is
            Assert.Equal("xbob@example.com and bob@example.com.au", AliasHelper.InText("xbob@example.com and bob@example.com.au"));
            //A name is shown as typed, even one that looks like a replacement pattern
            Assert.Equal("from $1 Dollar", AliasHelper.InText("from dollar@example.com"));
            Assert.Equal(string.Empty, AliasHelper.InText(string.Empty));
        }

        [Fact]
        public void The_dialog_takes_a_name_and_one_address_that_is_not_someone_elses()
        {
            AliasList list = BobAndCarol();

            Assert.NotNull(AliasDialog.Problem(" ", "dave@example.net", list, null));
            foreach (string bad in new[] { "dave", "dave@", "@example.net", "dave@example.net;eve@example.net", "dave smith@example.net", "a@b@c" })
            {
                Assert.NotNull(AliasDialog.Problem("Dave", bad, list, null));
            }
            Assert.Null(AliasDialog.Problem("Dave", " dave@example.net ", list, null));

            //Carol's address belongs to Carol, unless it is Carol's entry being edited
            Assert.Contains("Carol", AliasDialog.Problem("Dave", Carol, list, null));
            Assert.Null(AliasDialog.Problem("Caroline", Carol.ToUpperInvariant(), list, Carol));
        }
    }
}
