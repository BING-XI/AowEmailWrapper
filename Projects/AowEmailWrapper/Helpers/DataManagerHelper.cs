using System.IO;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Pollers.MessageStore;
using AowEmailWrapper.Localization.Framework;

namespace AowEmailWrapper.Helpers
{
    public class DataManagerHelper
    {        
        private const string CONFIG_FILE_NAME = "config.xml";
        private const string ACTIVITY_FILE_NAME = "activity.xml";
        private const string ALIASES_FILE_NAME = "aliases.xml";
        private const string LOCALIZATION_FILE_NAME = "Localization.xml";
        private const string MessageStoreFileTemplate = "{0}@{1}.xml";
        private const string TurnLogFilenameTemplate = "{0}.log";

        #region Localization

        /// <summary>Set by the localisation tests to supply a pseudo language; the Wrapper never sets it.</summary>
        internal static Languages LanguagesOverride { get; set; }

        public static Languages LoadLanguages()
        {
            if (LanguagesOverride != null)
            {
                return LanguagesOverride;
            }

            string localizationFilePath = Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), LOCALIZATION_FILE_NAME);

            Languages returnVal = FileHelper.LoadXmlFile<Languages>(localizationFilePath);
            if (returnVal == null)
            {
                returnVal = new Languages();
            }

            return returnVal;
        }

        #endregion

        #region Config

        public static Config LoadConfig()
        {
            bool isNew;
            return LoadConfig(out isNew);
        }

        public static Config LoadConfig(out bool isNewConfig)
        {
            isNewConfig = false;

            string configFilePath = Path.Combine(AppDataHelper.Config.FullName, CONFIG_FILE_NAME);

            Config returnVal = FileHelper.LoadXmlFile<Config>(configFilePath);

            if (returnVal == null)
            {
                returnVal = new Config(true);
                isNewConfig = true;
            }
            else if (MigrateLegacyPasswords(returnVal))
            {
                SaveConfig(returnVal);
            }

            return returnVal;
        }

        /// <summary>
        /// Re-stores passwords saved by versions before 2.0 (reversible obfuscation) with Windows DPAPI.
        /// </summary>
        private static bool MigrateLegacyPasswords(Config config)
        {
            bool changed = false;

            if (config.AccountsList != null && config.AccountsList.Accounts != null)
            {
                foreach (AccountConfigValues account in config.AccountsList.Accounts)
                {
                    if (account.PollingConfig != null &&
                        !string.IsNullOrEmpty(account.PollingConfig.Password) &&
                        !CryptographyHelper.IsProtected(account.PollingConfig.Password))
                    {
                        account.PollingConfig.PasswordTrue = account.PollingConfig.PasswordTrue;
                        changed = true;
                    }

                    if (account.SmtpConfig != null &&
                        !string.IsNullOrEmpty(account.SmtpConfig.Password) &&
                        !CryptographyHelper.IsProtected(account.SmtpConfig.Password))
                    {
                        account.SmtpConfig.PasswordTrue = account.SmtpConfig.PasswordTrue;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        public static void SaveConfig(Config toSave)
        {
            string configFilePath = Path.Combine(AppDataHelper.Config.FullName, CONFIG_FILE_NAME);
            FileHelper.SaveXmlFile(configFilePath, toSave);
        }

        #endregion

        #region Activity Log

        public static ActivityList LoadActivityLog()
        {
            string activityFilePath = Path.Combine(AppDataHelper.ActivityLog.FullName, ACTIVITY_FILE_NAME);

            ActivityList returnVal = FileHelper.LoadXmlFile<ActivityList>(activityFilePath);
            if (returnVal == null) returnVal = new ActivityList();

            return returnVal;
        }

        public static void SaveActivityLog(ActivityList toSave)
        {
            string activityFilePath = Path.Combine(AppDataHelper.ActivityLog.FullName, ACTIVITY_FILE_NAME);
            FileHelper.SaveXmlFile(activityFilePath, toSave);
        }

        #endregion

        #region Aliases

        public static AliasList LoadAliases()
        {
            AliasList returnVal = FileHelper.LoadXmlFile<AliasList>(Path.Combine(AppDataHelper.Config.FullName, ALIASES_FILE_NAME));
            return returnVal ?? new AliasList();
        }

        public static void SaveAliases(AliasList toSave)
        {
            FileHelper.SaveXmlFile(Path.Combine(AppDataHelper.Config.FullName, ALIASES_FILE_NAME), toSave);
        }

        #endregion

        #region Message Store

        public static MessageStoreCollection LoadLocalMessageStore(string username, string host)
        {
            string theFilePath = GetLocalMessageStoreFileName(username, host);
            return FileHelper.LoadXmlFile<MessageStoreCollection>(theFilePath);
        }

        public static void SaveLocalMessageStore(string username, string host, MessageStoreCollection localMessageStore)
        {
            string theFilePath = GetLocalMessageStoreFileName(username, host);
            FileHelper.SaveXmlFile(theFilePath, localMessageStore);
        }

        private static string GetLocalMessageStoreFileName(string username, string host)
        {
            string fileName = string.Format(MessageStoreFileTemplate, username, host);
            return Path.Combine(AppDataHelper.MessageStore.FullName, fileName);
        }

        #endregion

        #region Turn Logger

        public static void SaveTurnLog(string fileName, string theLog)
        {
            string theFilePath = GetTurnLogFilePath(fileName);
            DeleteTurnLog(theFilePath);
            FileHelper.SaveTextFile(theFilePath, theLog, true);
        }

        public static string LoadTurnLog(string fileName)
        {
            string theFilePath = GetTurnLogFilePath(fileName);
            return FileHelper.LoadTextFile(theFilePath, true);
        }

        public static bool DeleteTurnLog(string fileName)
        {
            return FileHelper.DeleteTextFile(GetTurnLogFilePath(fileName));
        }

        private static string GetTurnLogFilePath(string fileName)
        {
            return Path.Combine(AppDataHelper.TurnLogs.FullName, string.Format(TurnLogFilenameTemplate, StringHelper.RemoveInvalidPathChars(fileName)));
        }

        #endregion
    }
}
