using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ViewTracker.Views
{
    public partial class SyncAreaWindow : Window
    {
        public string SelectedParameter { get; private set; }

        public SyncAreaWindow(List<string> availableParameters, string defaultParameter = "Superficie_Nette_Reel")
        {
            InitializeComponent();

            // Set window title based on language
            this.Title = Localization.Get("SyncArea.Title");

            // Populate ComboBox with available parameters
            ParameterComboBox.ItemsSource = availableParameters.OrderBy(p => p).ToList();

            // Set default value
            if (!string.IsNullOrEmpty(defaultParameter) && availableParameters.Contains(defaultParameter))
            {
                ParameterComboBox.SelectedItem = defaultParameter;
            }
            else if (availableParameters.Any())
            {
                ParameterComboBox.SelectedIndex = 0;
            }
            else
            {
                ParameterComboBox.Text = defaultParameter;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedParameter = ParameterComboBox.Text;

            if (string.IsNullOrWhiteSpace(SelectedParameter))
            {
                MessageBox.Show(
                    Localization.Get("SyncArea.EmptyParameter"),
                    Localization.Common.Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
