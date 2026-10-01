using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace AowEmailWrapper.ConfigFramework
{
    /// <summary>The name the player knows another player by, for one of that player's email addresses.</summary>
    public class PlayerAlias
    {
        public const int MaxNameLength = 60;

        [XmlAttribute("name")]
        public string Name { get; set; }

        [XmlAttribute("address")]
        public string Address { get; set; }

        public PlayerAlias()
        { }

        public PlayerAlias(string name, string address)
        {
            Name = name;
            Address = address;
        }
    }

    /// <summary>
    /// The player's own list of the people they play with, kept on this PC only. One entry per address,
    /// so someone who plays from two addresses has two entries under the same name.
    /// </summary>
    [XmlRoot("aliases")]
    public class AliasList
    {
        private List<PlayerAlias> _aliases = new List<PlayerAlias>();

        [XmlElement("alias")]
        public List<PlayerAlias> Aliases
        {
            get { return _aliases; }
            set { _aliases = value ?? new List<PlayerAlias>(); }
        }

        /// <summary>The entry for the address, or null.</summary>
        public PlayerAlias Find(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }
            string wanted = address.Trim();
            return _aliases.FirstOrDefault(alias => alias != null && string.Equals((alias.Address ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The name for the address, or null when it has none.</summary>
        public string NameFor(string address)
        {
            PlayerAlias alias = Find(address);
            return alias != null && !string.IsNullOrWhiteSpace(alias.Name) ? alias.Name.Trim() : null;
        }

        /// <summary>Every name in the list once, in order, for offering when another address is added.</summary>
        public List<string> Names
        {
            get
            {
                return _aliases.Where(alias => alias != null && !string.IsNullOrWhiteSpace(alias.Name))
                    .Select(alias => alias.Name.Trim())
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>
        /// Gives the address the name, in place of the entry for <paramref name="replacing"/> when that is
        /// set (an edit, which may change the address) or of an entry the address already has.
        /// </summary>
        public void Set(string name, string address, string replacing)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            Remove(replacing);
            Remove(address);
            _aliases.Add(new PlayerAlias(name.Trim(), address.Trim()));
        }

        public bool Remove(string address)
        {
            PlayerAlias alias = Find(address);
            return alias != null && _aliases.Remove(alias);
        }

        public AliasList Clone()
        {
            return new AliasList { Aliases = _aliases.Where(alias => alias != null).Select(alias => new PlayerAlias(alias.Name, alias.Address)).ToList() };
        }
    }
}
