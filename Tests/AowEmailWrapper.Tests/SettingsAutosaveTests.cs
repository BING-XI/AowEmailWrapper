using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Controls;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// The Settings tab is saved as it changes. It reports a box ticked or a choice made at once, and a text field
    /// once it is finished with, not at each key: the port is checked as it is read, and a port half typed would be
    /// put back to the default.
    /// </summary>
    public class SettingsAutosaveTests
    {
        private static void OnStaThread(Action body)
        {
            Exception failure = null;
            Thread thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private static T Find<T>(Control parent, string name) where T : Control
        {
            return (T)parent.Controls.Find(name, true).Single();
        }

        /// <summary>The tab filled in from saved settings, counting its reports; each report reads the settings, as the Wrapper does.</summary>
        private static PreferencesConfig Tab(out Func<int> reports)
        {
            PreferencesConfig tab = new PreferencesConfig();
            int count = 0;
            tab.Config_Changed += (sender, e) =>
            {
                count++;
                PreferencesConfigValues read = tab.Config;
            };
            tab.Config = new PreferencesConfigValues(true) { PlayerName = "Andries" };
            reports = () => count;
            return tab;
        }

        [Fact]
        public void Filling_the_tab_in_from_the_saved_settings_reports_nothing()
        {
            OnStaThread(() =>
            {
                Func<int> reports;
                using (PreferencesConfig tab = Tab(out reports))
                {
                    tab.FinishEditing();

                    Assert.Equal(0, reports());
                }
            });
        }

        [Fact]
        public void A_ticked_box_is_reported_at_once()
        {
            OnStaThread(() =>
            {
                Func<int> reports;
                using (PreferencesConfig tab = Tab(out reports))
                {
                    FormBlockCheckBox sound = Find<FormBlockCheckBox>(tab, "fbEmailSound");
                    sound.Checked = !sound.Checked;

                    Assert.Equal(1, reports());
                }
            });
        }

        [Fact]
        public void A_name_is_reported_once_it_is_finished_with_not_at_each_key()
        {
            OnStaThread(() =>
            {
                Func<int> reports;
                using (PreferencesConfig tab = Tab(out reports))
                {
                    Find<FormBlockText>(tab, "fbPlayerName").TextValue = "Moraff";
                    Assert.Equal(0, reports());

                    tab.FinishEditing();
                    Assert.Equal(1, reports());
                    Assert.Equal("Moraff", tab.Config.PlayerName);

                    //Finished with again, unchanged
                    tab.FinishEditing();
                    Assert.Equal(1, reports());
                }
            });
        }

        [Fact]
        public void A_port_being_typed_is_left_as_typed_until_it_is_finished_with()
        {
            OnStaThread(() =>
            {
                Func<int> reports;
                using (PreferencesConfig tab = Tab(out reports))
                {
                    FormBlockText port = Find<FormBlockText>(tab, "fbGameWrapperDataPort");
                    port.TextValue = "5";
                    Assert.Equal("5", port.TextValue);
                    Assert.Equal(0, reports());

                    //Not a port a game may use: reading the settings puts the default back, and that counts as reported
                    tab.FinishEditing();
                    Assert.Equal(1, reports());
                    Assert.Equal(PreferencesConfigValues.GameWrapperDataPortDefault.ToString(), port.TextValue);
                    tab.FinishEditing();
                    Assert.Equal(1, reports());
                }
            });
        }

        [Fact]
        public void A_name_from_setting_up_an_account_is_reported()
        {
            OnStaThread(() =>
            {
                Func<int> reports;
                using (PreferencesConfig tab = Tab(out reports))
                {
                    tab.Config = new PreferencesConfigValues(true);
                    tab.OfferPlayerName("Shizznitski");

                    Assert.Equal(1, reports());
                    Assert.Equal("Shizznitski", tab.Config.PlayerName);
                }
            });
        }
    }
}
