using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Localization.Framework;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// Every text the Wrapper shows must exist in every language it offers. The translator quietly
    /// falls back to English for a key a language lacks, so a missing translation never shows up as
    /// an error; these tests make it one. Together with PseudoLocalizationTests, which catches text
    /// that bypasses the translator altogether, a new string cannot ship untranslated.
    /// </summary>
    public class LocalizationTests
    {
        private static readonly Regex Placeholder = new Regex(@"\{\d+\}");

        //Constants named ...Key that name App.config settings or build metadata, not texts
        private static readonly HashSet<string> NotTextKeys = new HashSet<string>
        {
            "BugReportHelper.EmailKey", "ConfigHelper.NotifySoundKey", "ConfigHelper.SentSoundKey",
            "ConfigHelper.AutostartPauseSecondsKey", "ConfigHelper.EndedFolderKey", "MicrosoftOAuth.ClientIdKey",
            "MicrosoftOAuth.AuthorityKey", "BuildInfo.CommitDateMetadataKey", "UpdateHelper.RepositoryKey",
        };

        internal static Languages Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Localization.xml");
            using (FileStream stream = File.OpenRead(path))
            {
                return (Languages)new XmlSerializer(typeof(Languages)).Deserialize(stream);
            }
        }

        internal static Language English(Languages languages)
        {
            return languages.LanguageList.Single(language => language.Code == "en");
        }

        [Fact]
        public void Every_language_has_exactly_the_keys_English_has()
        {
            Languages languages = Load();
            Assert.True(languages.LanguageList.Count >= 11, "the language file did not load");
            HashSet<string> english = new HashSet<string>(English(languages).LoopupList.Select(lookup => lookup.Key));

            List<string> problems = new List<string>();
            foreach (Language language in languages.LanguageList)
            {
                List<string> keys = language.LoopupList.Select(lookup => lookup.Key).ToList();
                problems.AddRange(keys.GroupBy(key => key).Where(group => group.Count() > 1).Select(group => $"{language.Code}: '{group.Key}' appears {group.Count()} times"));
                problems.AddRange(english.Except(keys).Select(key => $"{language.Code}: missing '{key}'"));
                problems.AddRange(keys.Except(english).Distinct().Select(key => $"{language.Code}: '{key}' is not an English key"));
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Every_translation_has_text_where_English_has_text()
        {
            Languages languages = Load();
            Dictionary<string, string> english = English(languages).LoopupList.ToDictionary(lookup => lookup.Key, lookup => lookup.Value);

            List<string> problems = languages.LanguageList
                .SelectMany(language => language.LoopupList
                    .Where(lookup => english.ContainsKey(lookup.Key) && !string.IsNullOrWhiteSpace(english[lookup.Key]) && string.IsNullOrWhiteSpace(lookup.Value))
                    .Select(lookup => $"{language.Code}: '{lookup.Key}' is empty"))
                .ToList();

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Every_translation_has_the_same_placeholders_as_English()
        {
            //A missing {0} drops the address or file name from the message; an extra one throws a FormatException
            Languages languages = Load();
            Dictionary<string, string> english = English(languages).LoopupList.ToDictionary(lookup => lookup.Key, lookup => lookup.Value ?? string.Empty);

            List<string> problems = new List<string>();
            foreach (Language language in languages.LanguageList)
            {
                foreach (Lookup lookup in language.LoopupList.Where(lookup => english.ContainsKey(lookup.Key)))
                {
                    string expected = string.Join(",", Placeholder.Matches(english[lookup.Key]).Select(match => match.Value).Distinct().OrderBy(value => value));
                    string actual = string.Join(",", Placeholder.Matches(lookup.Value ?? string.Empty).Select(match => match.Value).Distinct().OrderBy(value => value));
                    if (expected != actual)
                    {
                        problems.Add($"{language.Code}: '{lookup.Key}' has [{actual}], English has [{expected}]");
                    }
                    else if (expected.Length > 0)
                    {
                        try
                        {
                            string.Format(lookup.Value, "a", "b", "c", "d", "e");
                        }
                        catch (FormatException)
                        {
                            problems.Add($"{language.Code}: '{lookup.Key}' is not a valid format string");
                        }
                    }
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Every_key_the_code_names_exists_in_English()
        {
            //Keys are kept in constants named ...Key, and the tray and list menus use ..._Tag constants
            Language english = English(Load());
            HashSet<string> keys = new HashSet<string>(english.LoopupList.Select(lookup => lookup.Key));

            List<string> problems = typeof(Main).Assembly.GetTypes()
                .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(field => field.IsLiteral && field.FieldType == typeof(string) && (field.Name.EndsWith("Key", StringComparison.Ordinal) || field.Name.EndsWith("_Tag", StringComparison.Ordinal)))
                .Select(field => new { Field = field, Value = (string)field.GetRawConstantValue() })
                .Where(constant => !NotTextKeys.Contains(constant.Field.DeclaringType.Name + "." + constant.Field.Name))
                .Where(constant => !string.IsNullOrEmpty(constant.Value) && !keys.Contains(constant.Value))
                .Select(constant => $"{constant.Field.DeclaringType.Name}.{constant.Field.Name} = '{constant.Value}'")
                .ToList();

            Assert.True(problems.Count == 0, "Keys named in code but missing from the English table:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Every_enum_value_the_screens_show_has_a_translation()
        {
            HashSet<string> keys = new HashSet<string>(English(Load()).LoopupList.Select(lookup => lookup.Key));
            IEnumerable<Enum> shown = new Enum[] { IconState.Sending, IconState.Checking, IconState.EmailWaiting }
                .Concat(Enum.GetValues(typeof(EmailType)).Cast<Enum>())
                .Concat(Enum.GetValues(typeof(SSLType)).Cast<Enum>())
                .Concat(Enum.GetValues(typeof(EmailSaveFolder)).Cast<Enum>())
                .Concat(Enum.GetValues(typeof(InstallSource)).Cast<Enum>())
                .Concat(Enum.GetValues(typeof(ActivityState)).Cast<Enum>());

            List<string> problems = shown.Where(value => !keys.Contains("enum" + value)).Select(value => $"{value.GetType().Name}.{value}: no 'enum{value}'").ToList();

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
    }
}
