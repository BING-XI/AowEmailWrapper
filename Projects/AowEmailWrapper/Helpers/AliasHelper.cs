using System;
using System.Linq;
using System.Text.RegularExpressions;
using AowEmailWrapper.ConfigFramework;

namespace AowEmailWrapper.Helpers
{
    /// <summary>
    /// Shows the players the player has named on the Aliases tab by those names wherever the Wrapper
    /// would show their email address. Addresses without a name are shown as they are.
    /// </summary>
    public static class AliasHelper
    {
        private static AliasList _current = new AliasList();

        /// <summary>The list in use; the Aliases tab replaces it whenever the player changes it.</summary>
        public static AliasList Current
        {
            get { return _current; }
            set { _current = value ?? new AliasList(); }
        }

        /// <summary>The name for the address, or the address itself when it has none.</summary>
        public static string Display(string address)
        {
            return Current.NameFor(address) ?? address;
        }

        /// <summary>"Bob (bob@example.com)" when the address has a name, the address alone otherwise.</summary>
        public static string DisplayWithAddress(string address)
        {
            string name = Current.NameFor(address);
            return name != null ? string.Format("{0} ({1})", name, address.Trim()) : address;
        }

        /// <summary>A ';' separated list of addresses, each as DisplayWithAddress shows it, separated by commas.</summary>
        public static string DisplayListWithAddresses(string addresses)
        {
            return string.Join(", ", (addresses ?? string.Empty)
                .Split(ActivityList.AddressSeparator)
                .Select(address => address.Trim())
                .Where(address => address.Length > 0)
                .Select(DisplayWithAddress));
        }

        /// <summary>
        /// The text with every address that has a name replaced by the name, for messages built before the
        /// names were looked at. Only whole addresses are replaced: one address inside a longer one is not.
        /// </summary>
        public static string InText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            foreach (PlayerAlias alias in Current.Aliases.Where(alias => alias != null && !string.IsNullOrWhiteSpace(alias.Address) && !string.IsNullOrWhiteSpace(alias.Name)))
            {
                string pattern = @"(?<![\w.+-])" + Regex.Escape(alias.Address.Trim()) + @"(?![\w-]|\.\w)";
                string name = alias.Name.Trim();
                text = Regex.Replace(text, pattern, match => name, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            return text;
        }
    }
}
